using System.Diagnostics;
using System.Net.Sockets;
using System.IO;
using Microsoft.Win32;
using Microsoft.Extensions.Options;
using Recast.WindowsRecorder.Config;

namespace Recast.WindowsRecorder.Services
{
    public class VncManager
    {
        private Process? _userProcess;
        public int Port { get; private set; } = 5901; // default separate from 5900
        public bool IsRunning => IsPortOpen("127.0.0.1", Port, TimeSpan.FromMilliseconds(250));

        public VncManager(IOptions<RecorderOptions> options)
        {
            // JSON-configured port first
            try
            {
                var cfg = options?.Value;
                if (cfg?.VncPort is int cp && cp > 0) Port = cp;
            }
            catch { }
            // Environment override
            try
            {
                var env = Environment.GetEnvironmentVariable("VNC_PORT");
                if (!string.IsNullOrWhiteSpace(env) && int.TryParse(env, out var p) && p > 0) Port = p;
            }
            catch { }
            // Registry-detected port fallback
            try
            {
                var rfb = GetConfiguredPortFromRegistry();
                if (rfb > 0) Port = rfb;
            }
            catch { }
        }

        private static bool IsPortOpen(string host, int port, TimeSpan timeout)
        {
            try
            {
                using var client = new TcpClient();
                var task = client.ConnectAsync(host, port);
                return task.Wait(timeout) && client.Connected;
            }
            catch { return false; }
        }

        public async Task<bool> EnsureRunningAsync(int? preferredPort = null)
        {
            if (preferredPort.HasValue) Port = preferredPort.Value;
            if (IsRunning) return true;

            // Try to start service by name (two common names)
            string[] svcNames = new[] { "tvnserver", "TightVNC Server" };
            foreach (var name in svcNames)
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "sc",
                        Arguments = $"start \"{name}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    p?.WaitForExit(2000);
                    await Task.Delay(1000);
                    await RefreshActivePortAsync();
                    if (IsRunning) return true;
                }
                catch { }
            }

            // Try to run tvnserver.exe in user session
            var exe = ResolveTightVncExe();
            if (exe != null)
            {
                try
                {
                    _userProcess = Process.Start(new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "-run",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                }
                catch { }

                await Task.Delay(1500);
                await RefreshActivePortAsync();
                if (IsRunning) return true;
            }

            return IsRunning;
        }

        public async Task<bool> StopAsync()
        {
            var ok = true;
            try
            {
                if (_userProcess != null && !_userProcess.HasExited)
                {
                    _userProcess.Kill(true);
                    _userProcess.Dispose();
                    _userProcess = null;
                }
            }
            catch { ok = false; }

            // Try to stop service as well
            string[] svcNames = new[] { "tvnserver", "TightVNC Server" };
            foreach (var name in svcNames)
            {
                try
                {
                    var p = Process.Start(new ProcessStartInfo
                    {
                        FileName = "sc",
                        Arguments = $"stop \"{name}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    });
                    p?.WaitForExit(2000);
                }
                catch { ok = false; }
            }

            await Task.Delay(500);
            return ok && !IsRunning;
        }

        private static string? ResolveTightVncExe()
        {
            // Common locations
            var paths = new List<string>
            {
                Environment.GetEnvironmentVariable("TIGHTVNC_PATH") ?? string.Empty,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TightVNC", "tvnserver.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "TightVNC", "tvnserver.exe"),
            };
            foreach (var p in paths.Where(p => !string.IsNullOrWhiteSpace(p)))
                if (File.Exists(p)) return p;

            // Try PATH
            try
            {
                var which = Process.Start(new ProcessStartInfo
                {
                    FileName = "where",
                    Arguments = "tvnserver.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                });
                var outp = which?.StandardOutput.ReadToEnd();
                which?.WaitForExit(1000);
                if (!string.IsNullOrWhiteSpace(outp))
                {
                    var first = outp.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(first) && File.Exists(first)) return first;
                }
            }
            catch { }
            return null;
        }

        private static int GetConfiguredPortFromRegistry()
        {
            static int Read(RegistryKey baseKey, string subkey)
            {
                try
                {
                    using var key = baseKey.OpenSubKey(subkey);
                    if (key == null) return -1;
                    var val = key.GetValue("RfbPort");
                    if (val is int i) return i;
                    if (val is string s && int.TryParse(s, out var p)) return p;
                }
                catch { }
                return -1;
            }

            var port = Read(Registry.LocalMachine, "SOFTWARE\\TightVNC\\Server");
            if (port > 0) return port;
            port = Read(Registry.LocalMachine, "SOFTWARE\\WOW6432Node\\TightVNC\\Server");
            return port > 0 ? port : -1;
        }

        public async Task RefreshActivePortAsync()
        {
            // First try registry value
            try
            {
                var r = GetConfiguredPortFromRegistry();
                if (r > 0) Port = r;
            }
            catch { }
            // If not listening, scan a few common ports
            if (!IsRunning)
            {
                for (int p = 5900; p <= 5910; p++)
                {
                    if (IsPortOpen("127.0.0.1", p, TimeSpan.FromMilliseconds(150)))
                    {
                        Port = p; break;
                    }
                }
            }
            await Task.CompletedTask;
        }
    }
}
