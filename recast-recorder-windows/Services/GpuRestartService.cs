using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Recast.WindowsRecorder.Services
{
    public class GpuRestartService
    {
        private readonly ILogger<GpuRestartService> _log;

        public GpuRestartService(ILogger<GpuRestartService> log)
        {
            _log = log;
        }

        public async Task<bool> RestartNvidiaGpuAsync()
        {
            _log.LogWarning("Attempting to restart NVIDIA GPU...");
            
            var script = @"
# Get the Instance ID of the NVIDIA GPU
$gpu = Get-PnpDevice | Where-Object { $_.FriendlyName -match 'NVIDIA' -and $_.Class -eq 'Display' -and $_.Status -eq 'OK' }
# Restart the device
if ($gpu) {
    Write-Host ""Restarting GPU: $($gpu.FriendlyName)...""
    pnputil /restart-device $gpu.InstanceId
} else {
    Write-Host ""NVIDIA GPU not found.""
    exit 1
}
";

            try
            {
                // Encode script as Base64 to avoid escaping issues
                var scriptBytes = System.Text.Encoding.Unicode.GetBytes(script);
                var encodedScript = Convert.ToBase64String(scriptBytes);
                
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encodedScript}",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    }
                };

                if (!proc.Start())
                {
                    _log.LogError("Failed to start PowerShell process for GPU restart");
                    return false;
                }

                var stdout = await proc.StandardOutput.ReadToEndAsync();
                var stderr = await proc.StandardError.ReadToEndAsync();
                
                await proc.WaitForExitAsync();

                if (!string.IsNullOrWhiteSpace(stdout))
                    _log.LogInformation("GPU restart stdout: {Output}", stdout.Trim());
                
                if (!string.IsNullOrWhiteSpace(stderr))
                    _log.LogWarning("GPU restart stderr: {Error}", stderr.Trim());

                if (proc.ExitCode == 0)
                {
                    _log.LogInformation("GPU restart completed successfully");
                    await Task.Delay(3000);
                    return true;
                }
                else
                {
                    _log.LogError("GPU restart failed with exit code {ExitCode}", proc.ExitCode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Exception during GPU restart");
                return false;
            }
        }

        public void PerformReboot()
        {
            _log.LogWarning("Initiating system reboot due to choppy stream...");
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown",
                    Arguments = "/r /f /t 3",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Failed to initiate system reboot");
            }
        }
    }
}
