using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.StaticFiles;
using Serilog;
using Recast.WindowsRecorder.Models;
using Recast.WindowsRecorder.Services;

namespace Recast.WindowsRecorder
{
    public sealed class WebServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private WebServer(WebApplication app)
        {
            _app = app;
        }

        public static async Task<WebServer> StartAsync(IServiceProvider rootServices, IConfiguration config)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Host.UseSerilog();

            // Kestrel binding
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.Listen(IPAddress.Any, 5001);
            });

            // Share instances from root host so state is consistent
            builder.Services.AddSingleton(rootServices.GetRequiredService<VncManager>());
            builder.Services.AddSingleton(rootServices.GetRequiredService<SessionManager>());
            builder.Services.AddSingleton(rootServices.GetRequiredService<RecordingManager>());
            builder.Services.AddSingleton(rootServices.GetRequiredService<RecorderState>());

            builder.Services.AddRouting();
            builder.Services.AddDirectoryBrowser();

            var app = builder.Build();

            // Static files (wwwroot)
            app.UseDefaultFiles();
            app.UseStaticFiles();

            // Serve HLS output directory at /hls
            var hlsRoot = Path.Combine(AppContext.BaseDirectory, "hls");
            Directory.CreateDirectory(hlsRoot);
            var hlsProvider = new FileExtensionContentTypeProvider();
            hlsProvider.Mappings[".m3u8"] = "application/vnd.apple.mpegurl";
            hlsProvider.Mappings[".ts"] = "video/mp2t";
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(hlsRoot),
                RequestPath = "/hls",
                ContentTypeProvider = hlsProvider
            });
            app.UseDirectoryBrowser(new DirectoryBrowserOptions
            {
                FileProvider = new PhysicalFileProvider(hlsRoot),
                RequestPath = "/hls"
            });

            // Serve finalized recordings at /recordings
            var recRoot = Path.Combine(AppContext.BaseDirectory, "recordings");
            Directory.CreateDirectory(recRoot);
            var recProvider = new FileExtensionContentTypeProvider();
            recProvider.Mappings[".mp4"] = "video/mp4";
            recProvider.Mappings[".vtt"] = "text/vtt";
            recProvider.Mappings[".jpg"] = "image/jpeg";
            recProvider.Mappings[".jpeg"] = "image/jpeg";
            recProvider.Mappings[".png"] = "image/png";
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(recRoot),
                RequestPath = "/recordings",
                ContentTypeProvider = recProvider,
                ServeUnknownFileTypes = true
            });
            app.UseDirectoryBrowser(new DirectoryBrowserOptions
            {
                FileProvider = new PhysicalFileProvider(recRoot),
                RequestPath = "/recordings"
            });

            // Endpoints
            app.MapGet("/status", (SessionManager sm) => Results.Json(new
            {
                recorder_id = $"windows-{Environment.MachineName}",
                status = sm.HasAny ? "RECORDING" : "IDLE",
                current_job = (string?)null
            }));

            app.MapGet("/control", () => Results.Redirect("/control.html"));

            app.MapGet("/api/vnc/status", async (SessionManager sm, VncManager vnc) =>
            {
                await vnc.RefreshActivePortAsync();
                var map = new Dictionary<string, object?>();
                foreach (var name in sm.ControllerNames)
                {
                    var s = sm.Get(name);
                    map[name] = new
                    {
                        display = "windows",
                        mode = s?.Mode ?? "none",
                        vnc_running = vnc.IsRunning,
                        error = false,
                        error_message = (string?)null,
                        vnc_port = vnc.Port
                    };
                }
                return Results.Json(new { controllers = map });
            });

            app.MapPost("/api/vnc/start", async (HttpContext ctx, VncManager vnc) =>
            {
                var req = await ctx.Request.ReadFromJsonAsync<VncStartReq>() ?? new();
                var ok = await vnc.EnsureRunningAsync(req.Port ?? vnc.Port);
                return Results.Json(new { status = ok ? "success" : "error", display = "windows", port = vnc.Port });
            });

            app.MapPost("/api/vnc/stop", async (VncManager vnc) =>
            {
                var ok = await vnc.StopAsync();
                return Results.Json(new { status = ok ? "success" : "error" });
            });

            app.MapPost("/api/session/launch", async (HttpContext ctx, SessionManager sm, VncManager vnc) =>
            {
                var payload = await ctx.Request.ReadFromJsonAsync<LaunchReq>() ?? new();
                if (string.IsNullOrWhiteSpace(payload.Controller))
                    return Results.Json(new { status = "error", message = "controller required" }, statusCode: 400);

                var url = payload.Url;
                if (!string.IsNullOrWhiteSpace(url) && !(url.StartsWith("http://") || url.StartsWith("https://")))
                    url = "https://" + url;
                if (string.IsNullOrWhiteSpace(url)) url = "https://www.google.com/";

                var mode = (payload.Mode ?? "manual").ToLowerInvariant();

                await vnc.EnsureRunningAsync(vnc.Port);

                var ok = await sm.LaunchAsync(payload.Controller!, url!, mode, payload.Keep_open_on_error == true);
                if (!ok) return Results.Json(new { status = "error", message = "failed to launch session" }, statusCode: 500);

                return Results.Json(new { status = "success", display = "windows", port = vnc.Port });
            });

            app.MapPost("/api/session/stop", async (HttpContext ctx, SessionManager sm, VncManager vnc, RecordingManager rec) =>
            {
                var payload = await ctx.Request.ReadFromJsonAsync<StopReq>() ?? new();
                if (string.IsNullOrWhiteSpace(payload.Controller))
                    return Results.Json(new { status = "error", message = "controller required" }, statusCode: 400);
                await sm.StopAsync(payload.Controller);
                await rec.StopAsync();
                var mp4 = await rec.FinalizeAsync(deleteHls: true);
                await vnc.StopAsync();
                return Results.Json(new { status = "success", mp4 = mp4 });
            });

            app.Lifetime.ApplicationStopping.Register(() =>
            {
                try { app.Services.GetRequiredService<SessionManager>().StopAllAsync().GetAwaiter().GetResult(); } catch { }
            });

            app.MapGet("/api/live_ready", (RecordingManager rec) =>
            {
                int segs = 0;
                try
                {
                    var path = rec.CurrentPlaylistPath;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        var text = File.ReadAllText(path);
                        segs = text.Split('\n').Count(l => l.StartsWith("#EXTINF"));
                        if (segs <= 1)
                        {
                            try
                            {
                                var dir = Path.GetDirectoryName(path);
                                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                                {
                                    var tsCount = Directory.EnumerateFiles(dir, "*.ts").Count();
                                    if (tsCount > segs) segs = tsCount;
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        // No playlist yet; count TS files directly in the current HLS directory
                        var dir = rec.CurrentDir;
                        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        {
                            var tsCount = Directory.EnumerateFiles(dir, "*.ts").Count();
                            if (tsCount > segs) segs = tsCount;
                        }
                    }
                }
                catch { }
                var ready = segs > 0;
                var start = rec.LiveStart?.UtcDateTime.ToString("s");
                return Results.Json(new { ready = ready, segments = segs, start_time = start });
            });

            app.MapGet("/api/live_clients", () => Results.Json(new { clients = Array.Empty<object>() }));

            app.MapGet("/live", (int job_id) =>
            {
                var path = Path.Combine(AppContext.BaseDirectory, "hls", $"job_{job_id}", "stream.m3u8");
                if (!File.Exists(path)) return Results.NotFound();
                // Redirect to Shaka-based player page bundled in wwwroot
                return Results.Redirect($"/live.html?job_id={job_id}");
            });

            await app.StartAsync();
            return new WebServer(app);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _app.StopAsync(TimeSpan.FromSeconds(2));
                await _app.DisposeAsync();
            }
            catch { }
        }
    }
}
