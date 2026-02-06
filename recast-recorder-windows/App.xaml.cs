using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Recast.WindowsRecorder.Config;
using Recast.WindowsRecorder.Models;
using Recast.WindowsRecorder.Services;

namespace Recast.WindowsRecorder
{
    public partial class App : Application
    {
        private IHost? _host;
        private WebServer? _web;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Global exception hooks
            this.DispatcherUnhandledException += (s, ex) =>
            {
                try { Log.Error(ex.Exception, "DispatcherUnhandledException"); } catch { }
                MessageBox.Show(ex.Exception?.ToString() ?? "Unknown UI error", "Unhandled UI Exception");
                ex.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                try { Log.Error(ex.ExceptionObject as Exception, "UnhandledException"); } catch { }
                MessageBox.Show((ex.ExceptionObject as Exception)?.ToString() ?? "Unknown fatal error", "Unhandled Exception");
            };
            TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                try { Log.Error(ex.Exception, "UnobservedTaskException"); } catch { }
                ex.SetObserved();
            };

            try
            {
                var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .Enrich.FromLogContext()
                    .WriteTo.File(
                        Path.Combine(logDir, "recorder-.log"),
                        outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffzzz} [{Level:u4}] {Message:lj}{NewLine}{Exception}",
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7)
                    .CreateLogger();

                var config = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .Build();

                _host = Host.CreateDefaultBuilder(e.Args)
                    .UseSerilog()
                    .ConfigureServices((ctx, services) =>
                    {
                        services.Configure<RecorderOptions>(config.GetSection("Recorder"));
                        services.AddSingleton<RecorderState>();
                        services.AddSingleton<VncManager>();
                        services.AddSingleton<SessionManager>();
                        services.AddSingleton<GpuRestartService>();
                        services.AddSingleton<RecordingManager>();
                        services.AddHttpClient();
                        services.AddHostedService<ManagementService>();
                    })
                    .Build();

                await _host.StartAsync();

                var services = _host.Services;
                var state = services.GetRequiredService<RecorderState>();

                var webAttempts = 6;
                var webDelaySeconds = 5;
                for (var attempt = 1; attempt <= webAttempts; attempt++)
                {
                    try
                    {
                        _web = await WebServer.StartAsync(services, config);
                        break;
                    }
                    catch (Exception webEx)
                    {
                        Log.Error(webEx, "Failed to start embedded web server (attempt {Attempt}/{Total})", attempt, webAttempts);
                        if (attempt == webAttempts)
                        {
                            MessageBox.Show($"Failed to start embedded web server after {webAttempts} attempts: {webEx.Message}\nCheck if port 5001 is in use.", "Startup Error");
                        }
                        else
                        {
                            Log.Warning("Web server startup failed, retrying in {DelaySeconds}s", webDelaySeconds);
                            await Task.Delay(TimeSpan.FromSeconds(webDelaySeconds));
                        }
                    }
                }

                var win = new MainWindow { DataContext = state };
                win.Show();
            }
            catch (Exception ex)
            {
                try { Log.Fatal(ex, "Startup failure"); } catch { }
                MessageBox.Show(ex.ToString(), "Startup Failure");
                Shutdown(-1);
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            try
            {
                try
                {
                    if (_host != null)
                    {
                        var services = _host.Services;
                        var rec = services.GetService(typeof(Recast.WindowsRecorder.Services.RecordingManager)) as Recast.WindowsRecorder.Services.RecordingManager;
                        var sessions = services.GetService(typeof(Recast.WindowsRecorder.Services.SessionManager)) as Recast.WindowsRecorder.Services.SessionManager;
                        var vnc = services.GetService(typeof(Recast.WindowsRecorder.Services.VncManager)) as Recast.WindowsRecorder.Services.VncManager;
                        try { if (sessions != null) await sessions.StopAllAsync(); } catch { }
                        try { if (rec != null) await rec.StopAsync(); } catch { }
                        try { if (rec != null) await rec.FinalizeAsync(deleteHls: true); } catch { }
                        try { if (vnc != null) await vnc.StopAsync(); } catch { }
                    }
                }
                catch { }
                if (_web != null)
                {
                    await _web.DisposeAsync();
                    _web = null;
                }
                if (_host != null)
                {
                    await _host.StopAsync(TimeSpan.FromSeconds(2));
                    _host.Dispose();
                    _host = null;
                }
            }
            catch { }
            base.OnExit(e);
        }
    }
}
