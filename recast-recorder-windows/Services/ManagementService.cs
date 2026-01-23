using System.Net.Http.Json;
using System.Text.Json;
using System.Net.Http;
using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Recast.WindowsRecorder.Config;
using Recast.WindowsRecorder.Models;
using System.IO;
using System.Diagnostics;

namespace Recast.WindowsRecorder.Services
{
    public class ManagementService : BackgroundService
    {
        private readonly ILogger<ManagementService> _log;
        private readonly IHttpClientFactory _http;
        private readonly VncManager _vnc;
        private readonly SessionManager _sessions;
        private readonly RecordingManager _rec;
        private readonly RecorderState _state;

        private readonly string _managerUrl;
        private readonly string _recorderId;
        private readonly string _hostname;
        private readonly RecorderOptions _cfg;

        private int? _currentJobId;
        private CancellationTokenSource? _jobCts;
        private volatile bool _isConverting;
        private volatile bool _isStopping;
        private volatile bool _ddaProbeRunning;
        private DateTimeOffset? _lastDdaProbeUtc;

        public ManagementService(
            ILogger<ManagementService> log,
            IHttpClientFactory http,
            VncManager vnc,
            SessionManager sessions,
            RecordingManager rec,
            IOptions<RecorderOptions> options,
            RecorderState state)
        {
            _log = log;
            _http = http;
            _vnc = vnc;
            _sessions = sessions;
            _rec = rec;
            _state = state;

            _cfg = options.Value ?? new RecorderOptions();
            _managerUrl = (_cfg.ManagementServerUrl ?? Environment.GetEnvironmentVariable("MANAGEMENT_SERVER_URL") ?? "http://localhost:5000").TrimEnd('/');
            _recorderId = _cfg.RecorderId ?? Environment.GetEnvironmentVariable("RECORDER_ID") ?? $"windows-{Environment.MachineName}";
            _hostname = _cfg.RecorderHostname ?? Environment.GetEnvironmentVariable("RECORDER_HOSTNAME") ?? Environment.MachineName;

            _state.ManagementUrl = _managerUrl;
            _state.RecorderId = _recorderId;
            _state.Hostname = _hostname;
            _state.Status = "IDLE";
        }

        private async Task MaybeRunDdaProbeAsync(CancellationToken ct)
        {
            if (_ddaProbeRunning || _isConverting || _isStopping) return;
            if (_currentJobId != null || _rec.CurrentJobId != null) return;

            var cfg = _cfg;
            if (cfg?.DdaProbeEnabled != true) return;
            var captureMethod = (cfg.CaptureMethod ?? "gdigrab").Trim().ToLowerInvariant();
            if (captureMethod != "ddagrab") return;

            var intervalSeconds = cfg.DdaProbeIntervalSeconds ?? 300;
            var now = DateTimeOffset.UtcNow;
            if (_lastDdaProbeUtc.HasValue && (now - _lastDdaProbeUtc.Value).TotalSeconds < intervalSeconds)
                return;

            _ddaProbeRunning = true;
            _lastDdaProbeUtc = now;

            try
            {
                _log.LogInformation("[DdaProbe] Running idle ddagrab probe");
                var ok = await _rec.ProbeDdagrabAsync(ct);
                if (!ok)
                {
                    _log.LogWarning("[DdaProbe] Probe failed while idle");
                    if (cfg.DdaProbeRestartOnFail == true)
                    {
                        _log.LogError("[DdaProbe] Restart on failure is enabled. Logging off user session and exiting.");
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
                            _log.LogWarning(ex, "[DdaProbe] Failed to initiate system restart");
                        }
                        Environment.Exit(2);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[DdaProbe] Idle probe failed");
            }
            finally
            {
                _ddaProbeRunning = false;
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _log.LogInformation("ManagementService starting. manager={Url} recorderId={Id}", _managerUrl, _recorderId);
            var client = _http.CreateClient();
            var regOk = await RegisterAsync(client, stoppingToken);
            _state.ManagementConnected = regOk;

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var hbOk = await HeartbeatAsync(client, stoppingToken);
                    _state.LastHeartbeat = DateTimeOffset.UtcNow;
                    _state.ManagementConnected = hbOk;
                    _log.LogDebug("Loop tick: hbOk={HbOk} currentJobId={JobId} timeUtc={Utc:o}", hbOk, _currentJobId, DateTime.UtcNow);

                    if (_currentJobId == null)
                    {
                        _log.LogDebug("Polling for job... recorder={Recorder}", _recorderId);
                        var job = await GetJobAsync(client, stoppingToken);
                        if (job != null)
                        {
                            try
                            {
                                int id = job.Value.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : -1;
                                string? st = job.Value.TryGetProperty("start_time", out var stEl) ? stEl.GetString() : null;
                                string? ri = job.Value.TryGetProperty("recorder_id", out var riEl) ? riEl.GetString() : null;
                                _log.LogInformation("Job received: id={Id} start_time={Start} recorder_id={Recorder}", id, st, ri);
                            }
                            catch { }
                            _ = RunJobAsync(client, job.Value, stoppingToken);
                        }
                        else
                        {
                            _log.LogDebug("No job available");
                            await MaybeRunDdaProbeAsync(stoppingToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "Management loop error");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        private async Task<bool> RegisterAsync(HttpClient client, CancellationToken ct)
        {
            try
            {
                var payload = new
                {
                    recorder_id = _recorderId,
                    hostname = _hostname,
                    ip_address = GetLocalIp() ?? "",
                    capabilities = new[] { "windows", "chrome", "vnc", "hls" }
                };
                var resp = await client.PostAsJsonAsync($"{_managerUrl}/api/recorder/register", payload, ct);
                _log.LogInformation("Register recorder: {Status}", resp.StatusCode);
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to register recorder");
                return false;
            }
        }

        private async Task<bool> HeartbeatAsync(HttpClient client, CancellationToken ct)
        {
            try
            {
                var status = _state.Status;
                if (_isConverting) status = "CONVERTING";
                else if (_isStopping) status = "STOPPING";
                else if (_currentJobId == null) status = "IDLE";
                var payload = new
                {
                    recorder_id = _recorderId,
                    status = status,
                    current_job_id = _currentJobId
                };
                var resp = await client.PostAsJsonAsync($"{_managerUrl}/api/recorder/heartbeat", payload, ct);
                _log.LogDebug("Heartbeat response: {Code}", resp.StatusCode);
                if (!resp.IsSuccessStatusCode)
                {
                    try { var b = await resp.Content.ReadAsStringAsync(ct); _log.LogDebug("Heartbeat body: {Body}", b); } catch { }
                }
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Heartbeat failed");
                return false;
            }
        }

        private async Task<JsonElement?> GetJobAsync(HttpClient client, CancellationToken ct)
        {
            try
            {
                var payload = new { recorder_id = _recorderId };
                _log.LogDebug("get_job request: recorder_id={Recorder} utc_now={Utc:o}", _recorderId, DateTime.UtcNow);
                var resp = await client.PostAsJsonAsync($"{_managerUrl}/api/recorder/get_job", payload, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    try { var rb = await resp.Content.ReadAsStringAsync(ct); _log.LogInformation("get_job HTTP {Code} body={Body}", resp.StatusCode, rb); } catch { }
                    return null;
                }
                var respBody = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(respBody);
                if (doc.RootElement.TryGetProperty("status", out var s) && s.GetString() == "success")
                {
                    var jobEl = doc.RootElement.GetProperty("job");
                    try
                    {
                        int id = jobEl.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : -1;
                        string? st = jobEl.TryGetProperty("start_time", out var stEl) ? stEl.GetString() : null;
                        string? ri = jobEl.TryGetProperty("recorder_id", out var riEl) ? riEl.GetString() : null;
                        _log.LogInformation("get_job success: id={Id} start_time={Start} recorder_id={Recorder}", id, st, ri);
                        _log.LogDebug("get_job job body={Body}", jobEl.ToString());
                    }
                    catch { }
                    return jobEl.Clone();
                }
                else
                {
                    string? statusStr = s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                    _log.LogDebug("get_job returned status={Status} body={Body}", statusStr, respBody);
                }
                // Fallback: resume an ASSIGNED job for this recorder after restarts
                try
                {
                    var list = await client.GetAsync($"{_managerUrl}/api/jobs?status=ASSIGNED", ct);
                    if (list.IsSuccessStatusCode)
                    {
                        var lbody = await list.Content.ReadAsStringAsync(ct);
                        using var jdoc = JsonDocument.Parse(lbody);
                        if (jdoc.RootElement.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
                        {
                            int total = 0; int matched = 0;
                            foreach (var j in jobs.EnumerateArray())
                            {
                                try
                                {
                                    total++;
                                    var rid = j.TryGetProperty("recorder_id", out var r) ? r.GetString() : null;
                                    if (!string.IsNullOrWhiteSpace(rid) && string.Equals(rid, _recorderId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _log.LogInformation("Resuming previously ASSIGNED job {JobId}", j.GetProperty("id").GetInt32());
                                        matched++;
                                        return j.Clone();
                                    }
                                }
                                catch { }
                            }
                            _log.LogDebug("assigned fallback scanned total={Total} matched={Matched}", total, matched);
                        }
                        else
                        {
                            _log.LogDebug("assigned fallback: response had no jobs array body={Body}", lbody);
                        }
                    }
                    else
                    {
                        try { var lb = await list.Content.ReadAsStringAsync(ct); _log.LogDebug("assigned fallback HTTP {Code} body={Body}", list.StatusCode, lb); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    _log.LogDebug(ex, "assigned-jobs fallback failed");
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "get_job failed");
            }
            return null;
        }

        private async Task RunJobAsync(HttpClient client, JsonElement job, CancellationToken outerCt)
        {
            if (_currentJobId != null) return;
            _log.LogInformation("RunJobAsync invoked");
            int jobId = -1;
            string url = "";
            string controller = "generic";
            string endTimeStr = string.Empty;
            try
            {
                if (!job.TryGetProperty("id", out var idEl))
                {
                    _log.LogError("Job JSON missing id. job={Body}", job.ToString());
                    return;
                }
                jobId = idEl.GetInt32();
                url = job.TryGetProperty("url", out var urlEl) ? (urlEl.GetString() ?? "") : "";
                if (string.IsNullOrWhiteSpace(url))
                {
                    _log.LogError("Job {JobId} missing url. job={Body}", jobId, job.ToString());
                    return;
                }
                controller = job.TryGetProperty("browser_controller", out var bc) ? (bc.GetString() ?? "generic") : "generic";
                endTimeStr = job.TryGetProperty("end_time", out var etEl) ? (etEl.GetString() ?? string.Empty) : string.Empty;
                _log.LogInformation("Parsed job {JobId}: controller={Controller} url={Url}", jobId, controller, url);
            }
            catch (Exception parseEx)
            {
                _log.LogError(parseEx, "Failed to parse job element. job={Body}", job.ToString());
                return;
            }

            _currentJobId = jobId;
            _jobCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
            var ct = _jobCts.Token;

            _state.CurrentJobId = jobId;
            _state.Status = "STARTING";
            _log.LogInformation("State update -> {Status} jobId={JobId}", _state.Status, _state.CurrentJobId);
            try { await UpdateJobStatusAsync(client, jobId, "STARTING", null, ct); } catch { }
            string stopReason = "END";

            try
            {
                string? startStr = job.TryGetProperty("start_time", out var stEl) ? stEl.GetString() : null;
                string? startedAt = job.TryGetProperty("started_at", out var staEl) ? staEl.GetString() : null;
                _log.LogInformation("[job {Job}] Starting: controller={Controller} url={Url}", jobId, controller, url);
                _log.LogInformation("[job {Job}] timing: nowUtc={Now:o} start_time={Start} started_at={Started}", jobId, DateTime.UtcNow, startStr, startedAt);
                try
                {
                    var vncOk = await _vnc.EnsureRunningAsync(_vnc.Port);
                    _log.LogInformation("[job {Job}] VNC ensure: ok={Ok} port={Port}", jobId, vncOk, _vnc.Port);
                }
                catch (Exception vncEx)
                {
                    _log.LogError(vncEx, "[job {Job}] VNC ensure failed", jobId);
                }
                bool launched = false;
                try
                {
                    launched = await _sessions.LaunchAsync(controller, url, mode: "automated", keepOpen: true);
                    _log.LogInformation("[job {Job}] Session launch: {Launched}", jobId, launched);
                }
                catch (Exception sx)
                {
                    _log.LogError(sx, "[job {Job}] Session launch threw", jobId);
                    launched = false;
                }
                if (!launched)
                {
                    await UpdateJobStatusAsync(client, jobId, "FAILED", "launch failed", ct);
                    _currentJobId = null;
                    _state.CurrentJobId = null;
                    _state.Status = "IDLE";
                    return;
                }

                // Start HLS recording for live preview
                try
                {
                    var startAttempts = Math.Max(1, _cfg.StartRetryAttempts ?? 3);
                    var delaySeconds = Math.Max(1, _cfg.StartRetryDelaySeconds ?? 5);
                    var maxDelaySeconds = Math.Max(delaySeconds, _cfg.StartRetryMaxDelaySeconds ?? 30);

                    bool recOk = false;
                    for (var attempt = 1; attempt <= startAttempts; attempt++)
                    {
                        recOk = await _rec.StartAsync(jobId, 1920, 1080, 30);
                        _log.LogInformation("[job {Job}] Recording start attempt {Attempt}/{Max}: ok={Ok}", jobId, attempt, startAttempts, recOk);
                        if (recOk) break;

                        var reason = _rec.LastStartErrorMessage;
                        if (!string.IsNullOrWhiteSpace(reason))
                            _log.LogWarning("[job {Job}] Recording start attempt {Attempt} failed: {Reason}", jobId, attempt, reason);

                        if (attempt < startAttempts)
                        {
                            var sleep = Math.Min(delaySeconds, maxDelaySeconds);
                            _log.LogInformation("[job {Job}] Waiting {Seconds}s before retrying recording start", jobId, sleep);
                            await Task.Delay(TimeSpan.FromSeconds(sleep), ct);
                            delaySeconds = Math.Min(delaySeconds * 2, maxDelaySeconds);
                        }
                    }

                    if (!recOk)
                    {
                        var msg = _rec.LastStartErrorMessage;
                        if (string.IsNullOrWhiteSpace(msg)) msg = "ffmpeg failed to start";
                        await UpdateJobStatusAsync(client, jobId, "FAILED", msg, ct);
                        try { await _rec.StopAsync(); } catch { }
                        try { await _sessions.StopAllAsync(); } catch { }
                        try { await _vnc.StopAsync(); } catch { }
                        _currentJobId = null;
                        _state.CurrentJobId = null;
                        _state.Status = "IDLE";
                        return;
                    }
                }
                catch (Exception rx)
                {
                    _log.LogError(rx, "[job {Job}] Recording start failed", jobId);
                    try
                    {
                        var msg = _rec.LastStartErrorMessage;
                        if (string.IsNullOrWhiteSpace(msg)) msg = "ffmpeg failed to start";
                        await UpdateJobStatusAsync(client, jobId, "FAILED", msg, ct);
                    }
                    catch { }
                    try { await _rec.StopAsync(); } catch { }
                    try { await _sessions.StopAllAsync(); } catch { }
                    try { await _vnc.StopAsync(); } catch { }
                    _currentJobId = null;
                    _state.CurrentJobId = null;
                    _state.Status = "IDLE";
                    return;
                }
                await UpdateJobStatusAsync(client, jobId, "RECORDING", null, ct);
                _state.Status = "RECORDING";
                _log.LogInformation("State update -> {Status} jobId={JobId}", _state.Status, _state.CurrentJobId);

                // Wait until end time or cancellation/stop is requested
                var nowUtc = DateTimeOffset.UtcNow;
                DateTimeOffset endUtc;
                if (!TryParseJobTimeUtc(endTimeStr, out endUtc))
                {
                    endUtc = nowUtc.AddMinutes(5);
                    _log.LogWarning("[job {Job}] Could not parse end_time='{EndTime}', defaulting endUtc={EndUtc:o}", jobId, endTimeStr, endUtc);
                }
                else
                {
                    _log.LogInformation("[job {Job}] Parsed end_time='{EndTime}' -> endUtc={EndUtc:o} (nowUtc={NowUtc:o})", jobId, endTimeStr, endUtc, nowUtc);
                }
                while (!ct.IsCancellationRequested)
                {
                    if (DateTimeOffset.UtcNow >= endUtc) break;
                    // Poll job status for cancel/stop
                    try
                    {
                        var js = await client.GetAsync($"{_managerUrl}/api/job_status/{jobId}", ct);
                        if (js.IsSuccessStatusCode)
                        {
                            using var jdoc = JsonDocument.Parse(await js.Content.ReadAsStringAsync(ct));
                            string? polled = null;
                            if (jdoc.RootElement.TryGetProperty("status", out var stEl2) && stEl2.ValueKind == JsonValueKind.String)
                                polled = stEl2.GetString();
                            _log.LogDebug("[job {Job}] polled status={Status}", jobId, polled);
                            if (polled == "STOPPING")
                            {
                                _isStopping = true;
                                try { await UpdateJobStatusAsync(client, jobId, "STOPPING", null, ct); } catch { }
                                stopReason = "STOPPING";
                                _state.Status = "STOPPING";
                                try { _jobCts?.Cancel(); } catch { }
                                break;
                            }
                            if (polled == "CANCELLED")
                            {
                                _isStopping = true;
                                stopReason = "CANCELLED";
                                _state.Status = "STOPPING";
                                try { _jobCts?.Cancel(); } catch { }
                                break;
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[job {Job}] Job run failed", jobId);
            }
            finally
            {
                try { await _rec.StopAsync(); _log.LogInformation("[job {Job}] Recording stopped (reason={Reason})", jobId, stopReason); } catch (Exception ex) { _log.LogWarning(ex, "[job {Job}] Recording stop error", jobId); }
                // Stop browser and VNC before conversion, per requested sequence
                try { await _sessions.StopAllAsync(); _log.LogInformation("[job {Job}] Sessions stopped", jobId); } catch (Exception ex) { _log.LogWarning(ex, "[job {Job}] Sessions stop error", jobId); }
                try { await _vnc.StopAsync(); _log.LogInformation("[job {Job}] VNC stopped", jobId); } catch (Exception ex) { _log.LogWarning(ex, "[job {Job}] VNC stop error", jobId); }
                try { await UpdateJobStatusAsync(client, jobId, "CONVERTING", null, outerCt); } catch { }
                _state.Status = "CONVERTING";
                _isConverting = true;
                var startTs = _rec.LiveStart; // capture before finalize clears it
                string? mp4Path = null;
                try
                {
                    // Use configured hard cap (default 45 min) + 2 min buffer for the outer timeout
                    // This ensures we wait long enough for FinalizeAsync's internal timeouts to complete
                    var finalizeTimeoutMinutes = (_cfg.FinalizeHardCapMinutes ?? 45) + 2;
                    _log.LogInformation("[job {Job}] Starting finalization with timeout of {Timeout} minutes", jobId, finalizeTimeoutMinutes);
                    var finalizeTask = _rec.FinalizeAsync(deleteHls: true);
                    var finished = await Task.WhenAny(finalizeTask, Task.Delay(TimeSpan.FromMinutes(finalizeTimeoutMinutes), outerCt));
                    if (finished == finalizeTask)
                    {
                        mp4Path = await finalizeTask;
                    }
                    else
                    {
                        _log.LogWarning("[job {Job}] Finalize timeout after {Timeout} minutes, continuing cleanup", jobId, finalizeTimeoutMinutes);
                    }
                    _log.LogInformation("[job {Job}] Finalized MP4: {Mp4}", jobId, mp4Path ?? "<none>");
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[job {Job}] Finalize error", jobId);
                }
                finally
                {
                    _isConverting = false;
                    _isStopping = false;
                }
                // Clear job state so heartbeats stop reporting CONVERTING even if server update fails
                _currentJobId = null;
                _state.CurrentJobId = null;
                _state.Status = "IDLE";
                _log.LogInformation("State update -> {Status} jobId={JobId}", _state.Status, _state.CurrentJobId);
                // Upload recording metadata if we have an MP4
                try
                {
                    if (!string.IsNullOrWhiteSpace(mp4Path) && File.Exists(mp4Path))
                    {
                        long size = 0; try { size = new FileInfo(mp4Path).Length; } catch { }
                        int? dur = null;
                        if (startTs.HasValue)
                        {
                            var endTs = DateTimeOffset.UtcNow;
                            dur = (int)Math.Max(0, (endTs - startTs.Value).TotalSeconds);
                        }
                        var payload = new
                        {
                            job_id = jobId,
                            filename = Path.GetFileName(mp4Path),
                            file_size_bytes = size,
                            duration_seconds = dur,
                            actual_start_time = startTs?.UtcDateTime.ToString("s"),
                            actual_end_time = DateTimeOffset.UtcNow.UtcDateTime.ToString("s")
                        };
                        var resp = await client.PostAsJsonAsync($"{_managerUrl}/api/recorder/upload_recording", payload, outerCt);
                        _log.LogInformation("upload_recording -> {Code}", resp.StatusCode);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[job {Job}] upload_recording failed", jobId);
                }
                // Final status update
                var finalStatus = stopReason == "CANCELLED" ? "CANCELLED" : "COMPLETED";
                try { await UpdateJobStatusAsync(client, jobId, finalStatus, null, outerCt); } catch (Exception ex) { _log.LogWarning(ex, "[job {Job}] Final status update failed", jobId); }
            }
        }

        private static bool TryParseJobTimeUtc(string? value, out DateTimeOffset utc)
        {
            utc = default;
            if (string.IsNullOrWhiteSpace(value)) return false;

            // We accept:
            // - ISO 8601 with offset (e.g., 2025-12-24T15:52:55-07:00)
            // - Local time without offset (assume local)
            // - UTC time with Z
            var styles = DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, styles, out var dto) ||
                DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, styles, out dto))
            {
                utc = dto.ToUniversalTime();
                return true;
            }

            // Final fallback for unusual formats
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dt) ||
                DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out dt))
            {
                if (dt.Kind == DateTimeKind.Unspecified)
                {
                    dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                }
                utc = new DateTimeOffset(dt).ToUniversalTime();
                return true;
            }

            return false;
        }

        private async Task UpdateJobStatusAsync(HttpClient client, int jobId, string status, string? msg, CancellationToken ct)
        {
            try
            {
                var payload = new { job_id = jobId, status = status, error_message = msg };
                var resp = await client.PostAsJsonAsync($"{_managerUrl}/api/recorder/update_job_status", payload, ct);
                _log.LogInformation("update_job_status({JobId},{Status}) -> {Code}", jobId, status, resp.StatusCode);
                if (!resp.IsSuccessStatusCode)
                {
                    try { var b = await resp.Content.ReadAsStringAsync(ct); _log.LogDebug("update_job_status body: {Body}", b); } catch { }
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "update_job_status failed");
            }
        }

        private static string? GetLocalIp()
        {
            try
            {
                var host = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
                foreach (var ip in host.AddressList)
                {
                    if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !ip.ToString().StartsWith("127."))
                        return ip.ToString();
                }
            }
            catch { }
            return null;
        }
    }
}
