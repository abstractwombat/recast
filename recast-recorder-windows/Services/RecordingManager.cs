using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Recast.WindowsRecorder.Config;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;


namespace Recast.WindowsRecorder.Services
{
    public class RecordingManager
    {
        private readonly ILogger<RecordingManager> _log;
        private Process? _proc;
        public int Framerate { get; private set; } = 30;
        public string LiveRoot { get; }
        public string? CurrentDir { get; private set; }
        public int? CurrentJobId { get; private set; }
        public DateTimeOffset? LiveStart { get; private set; }
        public int SegmentCount { get; private set; }
        public string? LastStartErrorMessage { get; private set; }
        public string? LastStartFailureCategory { get; private set; }

        private int? _audioDisabledForJobId;

        private readonly IOptionsMonitor<RecorderOptions>? _options;
        private readonly GpuRestartService? _gpuRestartService;
        private volatile bool _finalizing;
        private FrameDropDetector? _frameDropDetector;
        private volatile bool _choppyStreamCorrectionInProgress;
        private CancellationTokenSource? _choppyCts;
        private Func<int, string, Task>? _jobFailureCallback;

        [DllImport("user32.dll")]
        private static extern bool GetInputState();

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SM_CMONITORS = 80;

        public RecordingManager(ILogger<RecordingManager> log, IOptionsMonitor<RecorderOptions>? options = null, GpuRestartService? gpuRestartService = null)
        {
            _log = log;
            _options = options;
            _gpuRestartService = gpuRestartService;
            LiveRoot = Path.Combine(AppContext.BaseDirectory, "hls");
            Directory.CreateDirectory(LiveRoot);
        }

        public void SetJobFailureCallback(Func<int, string, Task> callback)
        {
            _jobFailureCallback = callback;
        }

        public bool IsReady => CurrentDir != null && File.Exists(Path.Combine(CurrentDir!, "stream.m3u8")) && SegmentCount > 0;

        public async Task<bool> ProbeDdagrabAsync(CancellationToken ct)
        {
            var cfg = _options?.CurrentValue;
            var ffmpeg = cfg?.FfmpegPath
                         ?? Environment.GetEnvironmentVariable("FFMPEG")
                         ?? "ffmpeg";
            var outputIdx = cfg?.DdagrabOutputIdx ?? 0;
            var drawMouse = cfg?.DdagrabDrawMouse ?? true;
            var framerate = cfg?.Framerate ?? 30;
            var timeoutSeconds = cfg?.DdaProbeTimeoutSeconds ?? 8;
            var args = $"-y -nostdin -loglevel error -f lavfi -i \"ddagrab=output_idx={outputIdx}:draw_mouse={(drawMouse ? 1 : 0)}:framerate={framerate}\" -frames:v 1 -f null -";

            _log.LogInformation("[DdaProbe] Starting ddagrab probe: ffmpeg={Path} args={Args}", ffmpeg, args);

            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    }
                };

                if (!proc.Start())
                {
                    _log.LogWarning("[DdaProbe] Failed to start ffmpeg process");
                    return false;
                }

                var stderrLines = new List<string>();
                var stdoutLines = new List<string>();

                var stderrTask = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    {
                        if (stderrLines.Count < 20) stderrLines.Add(line);
                    }
                }, ct);

                var stdoutTask = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                    {
                        if (stdoutLines.Count < 20) stdoutLines.Add(line);
                    }
                }, ct);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

                try
                {
                    await proc.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { if (!proc.HasExited) proc.Kill(true); } catch { }
                    _log.LogWarning("[DdaProbe] Probe timed out after {Seconds}s", timeoutSeconds);
                    return false;
                }

                try { await Task.WhenAll(stderrTask, stdoutTask); } catch { }

                if (stderrLines.Count > 0)
                    _log.LogDebug("[DdaProbe] stderr:\n{Lines}", string.Join("\n", stderrLines));
                if (stdoutLines.Count > 0)
                    _log.LogDebug("[DdaProbe] stdout:\n{Lines}", string.Join("\n", stdoutLines));

                var ok = proc.ExitCode == 0;
                _log.LogInformation("[DdaProbe] ffmpeg exitCode={Code} ok={Ok}", proc.ExitCode, ok);
                return ok;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[DdaProbe] Probe failed");
                return false;
            }
        }

        public async Task<bool> StartAsync(int jobId, int width = 1920, int height = 1080, int framerate = 30, bool continueStream = false)
        {
            await StopAsync();
            var cfg = _options?.CurrentValue;
            width = cfg?.Width ?? width;
            height = cfg?.Height ?? height;
            framerate = cfg?.Framerate ?? framerate;
            Framerate = framerate;
            CurrentJobId = jobId;
            CurrentDir = Path.Combine(LiveRoot, $"job_{jobId}");
            Directory.CreateDirectory(CurrentDir);
            var playlist = Path.Combine(CurrentDir, "stream.m3u8");
            
            if (!continueStream)
            {
                try { File.Delete(playlist); } catch { }
                SegmentCount = 0;
            }
            else
            {
                _log.LogInformation("Continuing existing HLS stream for job {JobId}, preserving {Count} segments", jobId, SegmentCount);
            }
            
            LastStartErrorMessage = null;
            LastStartFailureCategory = null;
            _choppyStreamCorrectionInProgress = false;
            try { _choppyCts?.Cancel(); } catch { }
            _choppyCts = new CancellationTokenSource();

            var choppyDetectionEnabled = cfg?.ChoppyStreamDetectionEnabled ?? false;
            var gracePeriod = cfg?.ChoppyStreamGracePeriodSeconds ?? 15;
            if (choppyDetectionEnabled)
            {
                _frameDropDetector = new FrameDropDetector(_log, gracePeriod);
                _log.LogInformation("Choppy stream detection enabled with threshold={Threshold}/s, action={Action}, grace={Grace}s",
                    cfg?.ChoppyStreamThresholdPerSecond ?? 45, cfg?.ChoppyStreamCorrectionAction ?? "restart_gpu", gracePeriod);
            }
            else
            {
                _frameDropDetector = null;
            }

            string ffmpeg = _options?.CurrentValue?.FfmpegPath
                             ?? Environment.GetEnvironmentVariable("FFMPEG")
                             ?? "ffmpeg";

            // Log effective configuration
            LogEffectiveConfig(cfg, width, height, framerate);

            // Get FFmpeg log level from config (default to 'error' if not specified)
            var logLevel = cfg?.FfmpegLogLevel ?? "error";
            
            // If choppy stream detection is enabled, we need frame statistics with dup/drop counts
            // This requires loglevel 'info' or higher, and -stats flag
            if (choppyDetectionEnabled)
            {
                if (logLevel == "error" || logLevel == "warning" || logLevel == "quiet" || logLevel == "panic" || logLevel == "fatal")
                {
                    logLevel = "info";
                    _log.LogInformation("Choppy stream detection enabled: overriding FFmpeg log level to 'info' to capture frame statistics");
                }
            }
            var statsFlag = choppyDetectionEnabled ? "-stats " : "";

            // Prefer ddagrab (Desktop Duplication), fallback to gdigrab. Capture full desktop, scale/pad to output.
            var commonArgs = $"-y -nostdin -loglevel {logLevel} {statsFlag}";
            var segTmpl = Path.Combine(CurrentDir, "seg%05d.ts");
            var vf = $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}";
            var forceCfr = (cfg?.ForceCfr == true);
            var vPreset = string.IsNullOrWhiteSpace(cfg?.VideoPreset) ? "veryfast" : cfg!.VideoPreset!;
            var vCrf = cfg?.VideoCrf ?? 23;
            var vProfile = string.IsNullOrWhiteSpace(cfg?.VideoProfile) ? "main" : cfg!.VideoProfile!;
            var vPixFmt = string.IsNullOrWhiteSpace(cfg?.VideoPixFmt) ? "yuv420p" : cfg!.VideoPixFmt!;
            var vThreads = cfg?.VideoThreads ?? 2;

            // GOP settings: GopSeconds takes precedence over GopMult
            var gopMult = cfg?.GopMult ?? 2;
            var gopSeconds = cfg?.GopSeconds ?? 0;
            var gopSize = gopSeconds > 0 ? (framerate * gopSeconds) : (framerate * Math.Max(1, gopMult));

            // HLS settings
            var hlsTime = cfg?.HlsTime ?? 2;
            var hlsListSize = cfg?.HlsListSize ?? 0;
            var hlsFlags = string.IsNullOrWhiteSpace(cfg?.HlsFlags) ? "append_list+omit_endlist" : cfg!.HlsFlags!;
            var hlsPlaylistType = string.IsNullOrWhiteSpace(cfg?.HlsPlaylistType) ? "event" : cfg!.HlsPlaylistType!;

            // Audio settings
            var aRate = cfg?.AudioSampleRate ?? 48000;
            var aBr = cfg?.AudioBitrateK ?? 128;
            var aCh = cfg?.AudioChannels ?? 2;

            // FPS mode: use config or default to cfr
            var fpsMode = string.IsNullOrWhiteSpace(cfg?.FpsMode) ? "cfr" : cfg!.FpsMode!;
            var vsyncArg = $"-fps_mode {fpsMode} -r {framerate}";

            // Determine video encoder: HwAccel takes precedence, then VideoEncoder, then default to libx264
            var hwAccel = cfg?.HwAccel?.Trim().ToLowerInvariant() ?? "none";
            string vCodec;
            if (hwAccel == "nvenc")
                vCodec = "h264_nvenc";
            else if (hwAccel == "qsv")
                vCodec = "h264_qsv";
            else if (hwAccel == "amf")
                vCodec = "h264_amf";
            else if (!string.IsNullOrWhiteSpace(cfg?.VideoEncoder))
                vCodec = cfg!.VideoEncoder!;
            else
                vCodec = "libx264";

            var probe = "-probesize 100M -analyzeduration 5M";
            string outArgs;
            bool isNvenc = vCodec.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0;

            // Capture method needed early to determine if we skip vf for ddagrab+nvenc
            var captureMethod = (cfg?.CaptureMethod ?? "gdigrab").Trim().ToLowerInvariant();

            if (isNvenc)
            {
                outArgs = BuildNvencArgs(cfg, vCodec, vPixFmt, vProfile, gopSize, framerate, vf, vsyncArg, aRate, aBr, aCh, hlsTime, hlsListSize, hlsFlags, hlsPlaylistType, segTmpl, playlist, captureMethod);
            }
            else
            {
                // Software encoding (libx264)
                var x264Args = $"-c:v libx264 -pix_fmt {vPixFmt} -profile:v {vProfile} -preset {vPreset} -crf {vCrf} -threads {vThreads} -g {gopSize}";

                // Add bitrate constraints if specified
                if (cfg?.VideoBitrateK.HasValue == true)
                {
                    x264Args += $" -b:v {cfg.VideoBitrateK.Value}k";
                    if (cfg?.VideoMaxrateK.HasValue == true)
                        x264Args += $" -maxrate {cfg.VideoMaxrateK.Value}k";
                    if (cfg?.VideoBufsizeK.HasValue == true)
                        x264Args += $" -bufsize {cfg.VideoBufsizeK.Value}k";
                }

                outArgs = $"{vsyncArg} -vf \"{vf}\" {x264Args}" +
                          $" -c:a aac -ar {aRate} -b:a {aBr}k -ac {aCh} -af aresample=async=1:min_hard_comp=0.1:first_pts=0" +
                          $" -hls_time {hlsTime} -hls_list_size {hlsListSize} -hls_flags {hlsFlags} -hls_playlist_type {hlsPlaylistType}" +
                          $" -hls_segment_filename \"{segTmpl}\" -f hls \"{playlist}\"";
            }

            // Thread queue size from config
            var videoTqs = cfg?.VideoThreadQueueSize ?? 4096;
            var audioTqs = cfg?.AudioThreadQueueSize ?? 4096;
            var tqs = $"-thread_queue_size {videoTqs}";

            // ddagrab-specific options
            var ddagrabOutputIdx = cfg?.DdagrabOutputIdx ?? 0;
            var ddagrabDrawMouse = cfg?.DdagrabDrawMouse ?? true;

            // Build video input args based on capture method
            string videoInputArgs;
            if (captureMethod == "ddagrab")
            {
                // ddagrab uses lavfi filter input - Desktop Duplication API (better performance, requires Windows 8+)
                // Format: -f lavfi -i ddagrab=output_idx=0:draw_mouse=1:framerate=30
                var ddagrabOpts = new List<string>();
                ddagrabOpts.Add($"output_idx={ddagrabOutputIdx}");
                ddagrabOpts.Add($"draw_mouse={(ddagrabDrawMouse ? 1 : 0)}");
                ddagrabOpts.Add($"framerate={framerate}");
                videoInputArgs = $"-f lavfi -i \"ddagrab={string.Join(":", ddagrabOpts)}\"";
                _log.LogInformation("Using ddagrab capture method: {Args}", videoInputArgs);
            }
            else
            {
                // gdigrab - traditional GDI-based capture
                videoInputArgs = $"{tqs} -rtbufsize 512M -f gdigrab -framerate {framerate} -draw_mouse 1 -i desktop";
                _log.LogInformation("Using gdigrab capture method");
            }

            var audioApi = _options?.CurrentValue?.AudioApi?.Trim().ToLowerInvariant();
            var audioDev = _options?.CurrentValue?.AudioDevice?.Trim();

            // Audio input args: use wallclock timestamps for dshow to sync with video capture time
            // Audio input: delay audio by ~100ms to compensate for audio arriving ahead of video frames
            var audioTqsArg = $"-thread_queue_size {audioTqs}";
            var dshowAudioArgs = $"-itsoffset 0.1 {audioTqsArg} -rtbufsize 256M -f dshow -audio_buffer_size 50 -use_wallclock_as_timestamps 1";

            string tail;
            if (!string.IsNullOrWhiteSpace(audioDev) && (string.IsNullOrEmpty(audioApi) || audioApi == "dshow"))
            {
                if (_audioDisabledForJobId != jobId)
                {
                    tail = $"{videoInputArgs} {dshowAudioArgs} -i audio=\"{audioDev}\" {outArgs}";
                    _log.LogInformation("Using configured dshow audio device: {Dev}", audioDev);
                }
                else
                {
                    _log.LogWarning("Audio disabled for job {JobId} due to prior audio start failure; skipping configured audio device", jobId);
                    tail = $"{videoInputArgs} -an {outArgs}";
                }
            }
            else
            {
                tail = $"{videoInputArgs} -an {outArgs}";
            }

            var args = commonArgs + tail;
            var fullCommand = $"{ffmpeg} {args}";

            // Pre-flight system diagnostics
            _log.LogInformation("========== Pre-Flight System Diagnostics ==========");
            try
            {
                // Check desktop accessibility
                var desktopWindow = GetDesktopWindow();
                var desktopVisible = IsWindowVisible(desktopWindow);
                _log.LogInformation("Desktop window accessible: {Accessible}, visible: {Visible}", desktopWindow != IntPtr.Zero, desktopVisible);

                // Check if user input is available (not locked)
                var inputState = GetInputState();
                _log.LogInformation("User input state available: {Available}", inputState);

                // Get display information using Win32 API
                var monitorCount = GetSystemMetrics(SM_CMONITORS);
                var screenWidth = GetSystemMetrics(SM_CXSCREEN);
                var screenHeight = GetSystemMetrics(SM_CYSCREEN);
                _log.LogInformation("Display count: {Count}, Primary display: {Width}x{Height}",
                    monitorCount, screenWidth, screenHeight);

                // Check if ffmpeg executable exists and is accessible
                if (File.Exists(ffmpeg))
                {
                    var ffmpegInfo = new FileInfo(ffmpeg);
                    _log.LogInformation("FFmpeg executable: exists, size: {Size} bytes, last modified: {Modified}",
                        ffmpegInfo.Length, ffmpegInfo.LastWriteTime);
                }
                else
                {
                    _log.LogWarning("FFmpeg executable not found at path: {Path}", ffmpeg);
                }

                // Check working directory
                if (Directory.Exists(CurrentDir))
                {
                    var dirInfo = new DirectoryInfo(CurrentDir!);
                    _log.LogInformation("Working directory exists: {Dir}, writable test pending", CurrentDir);
                    try
                    {
                        var testFile = Path.Combine(CurrentDir!, ".write_test");
                        File.WriteAllText(testFile, "test");
                        File.Delete(testFile);
                        _log.LogInformation("Working directory is writable");
                    }
                    catch (Exception ex)
                    {
                        _log.LogError(ex, "Working directory is NOT writable");
                    }
                }

                // Log GPU/NVENC availability if using nvenc
                if (vCodec.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
                {
                    _log.LogInformation("Using NVENC encoder: {Codec} - GPU must be available and not locked", vCodec);
                    try
                    {
                        // Try to detect NVIDIA GPU processes
                        var nvidiaSmiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
                        if (File.Exists(nvidiaSmiPath))
                        {
                            _log.LogInformation("nvidia-smi found, GPU diagnostics available");
                        }
                    }
                    catch { }
                }

                // Check audio device availability if using audio
                if (tail.Contains("audio="))
                {
                    var audioDevMatch = System.Text.RegularExpressions.Regex.Match(tail, @"audio=""([^""]+)""");
                    if (audioDevMatch.Success)
                    {
                        var audioDevName = audioDevMatch.Groups[1].Value;
                        _log.LogInformation("Audio device requested: {Device}", audioDevName);

                        // Re-enumerate to verify device is still available
                        try
                        {
                            var currentDevices = EnumerateDshowAudioDevices(ffmpeg);
                            var deviceAvailable = currentDevices.Any(d => d.Equals(audioDevName, StringComparison.OrdinalIgnoreCase));
                            _log.LogInformation("Audio device currently available: {Available}", deviceAvailable);
                            if (!deviceAvailable)
                            {
                                _log.LogWarning("Audio device '{Device}' not found in current device list: {List}",
                                    audioDevName, string.Join(", ", currentDevices));
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogWarning(ex, "Failed to re-enumerate audio devices for verification");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Pre-flight diagnostics failed (non-fatal)");
            }
            _log.LogInformation("======================================================");

            _log.LogInformation("========== FFmpeg Recording Start ==========");
            _log.LogInformation("FFmpeg path: {Path}", ffmpeg);
            _log.LogInformation("FFmpeg full command:\n{Command}", fullCommand);
            _log.LogInformation("Working directory: {Dir}", CurrentDir);
            _log.LogInformation("============================================");
            try
            {
                _proc = Process.Start(new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    CreateNoWindow = true,
                    WorkingDirectory = CurrentDir,
                });
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to start ffmpeg attempt");
                LastStartFailureCategory = "start";
                LastStartErrorMessage = $"Failed to start ffmpeg process: {ex.Message}";
                _proc = null;
            }
            if (_proc != null)
            {
                var proc = _proc;
                _log.LogInformation("FFmpeg process started successfully, PID: {Pid}", proc.Id);
                var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                try { Directory.CreateDirectory(logDir); } catch { }
                var ffmpegLogPath = Path.Combine(logDir, $"ffmpeg-job-{(CurrentJobId ?? jobId)}-{DateTime.Now:yyyyMMdd_HHmmss}.log");
                _log.LogInformation("FFmpeg log file: {LogPath}", ffmpegLogPath);
                StreamWriter? ffLog = null;
                try { ffLog = new StreamWriter(new FileStream(ffmpegLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; } catch { }
                object logLock = new object();

                // Write the full command to the log file header
                try
                {
                    if (ffLog != null)
                    {
                        lock (logLock)
                        {
                            ffLog.WriteLine($"========== FFmpeg Recording Log ==========");
                            ffLog.WriteLine($"Job ID: {jobId}");
                            ffLog.WriteLine($"Start Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                            ffLog.WriteLine($"FFmpeg Path: {ffmpeg}");
                            ffLog.WriteLine($"Full Command:");
                            ffLog.WriteLine(fullCommand);
                            ffLog.WriteLine($"Working Directory: {CurrentDir}");
                            ffLog.WriteLine($"==========================================");
                            ffLog.WriteLine();
                        }
                    }
                }
                catch { }

                string? lastStderrLine = null;
                string? lastStdoutLine = null;
                string? failureCategory = null;
                bool audioFailureDetected = false;
                var recentStderrLines = new List<string>();
                var recentStdoutLines = new List<string>();
                object stderrLock = new object();
                object stdoutLock = new object();
                var processStartTime = DateTime.Now;
                bool firstOutputReceived = false;
                var firstOutputTime = DateTime.Now;

                var stderrTask = Task.Run(async () =>
                {
                    try
                    {
                        string? line;
                        while ((line = await proc.StandardError.ReadLineAsync()) != null)
                        {
                            if (!firstOutputReceived)
                            {
                                firstOutputReceived = true;
                                firstOutputTime = DateTime.Now;
                                var timeToFirstOutput = (firstOutputTime - processStartTime).TotalMilliseconds;
                                _log.LogInformation("First ffmpeg output received after {Ms}ms", timeToFirstOutput);
                            }
                            lastStderrLine = line;
                            // Keep last 20 lines for better error diagnosis
                            lock (stderrLock)
                            {
                                recentStderrLines.Add(line);
                                if (recentStderrLines.Count > 20)
                                    recentStderrLines.RemoveAt(0);
                            }

                            if (_frameDropDetector != null && !_choppyStreamCorrectionInProgress)
                            {
                                var threshold = cfg?.ChoppyStreamThresholdPerSecond ?? 45;
                                if (_frameDropDetector.ProcessFFmpegLine(line, threshold, out int dupRate, out int dropRate))
                                {
                                    _choppyStreamCorrectionInProgress = true;
                                    var cts = _choppyCts;
                                    _ = Task.Run(async () => await HandleChoppyStreamAsync(jobId, cfg, cts?.Token ?? CancellationToken.None));
                                }
                            }

                            var lower = line.ToLowerInvariant();
                            if (failureCategory == null)
                            {
                                if ((lower.Contains("dshow") || lower.Contains("audio=")) &&
                                    (lower.Contains("could not find") || lower.Contains("cannot") || lower.Contains("unable") || lower.Contains("i/o error") || lower.Contains("no such device") || lower.Contains("device is in use")))
                                {
                                    failureCategory = "audio";
                                    audioFailureDetected = true;
                                }
                                else if (lower.Contains("nvenc") &&
                                         (lower.Contains("no nvenc") || lower.Contains("cannot load") || lower.Contains("nvencapi") || lower.Contains("driver") || lower.Contains("not supported")))
                                {
                                    failureCategory = "nvenc";
                                }
                                else if (lower.Contains("ddagrab") || lower.Contains("gdigrab"))
                                {
                                    if (lower.Contains("error") || lower.Contains("failed") || lower.Contains("cannot") || lower.Contains("unable"))
                                        failureCategory = "capture";
                                }
                            }
                            try { if (ffLog != null) { lock (logLock) { ffLog.WriteLine($"[STDERR] {line}"); } } } catch { }

                            // Log key initialization milestones
                            if (line.Contains("Input #", StringComparison.OrdinalIgnoreCase))
                            {
                                _log.LogInformation("FFmpeg detected input: {Line}", line.Trim());
                            }
                            else if (line.Contains("Stream mapping:", StringComparison.OrdinalIgnoreCase))
                            {
                                _log.LogInformation("FFmpeg stream mapping started");
                            }
                            else if (line.Contains("Output #", StringComparison.OrdinalIgnoreCase))
                            {
                                _log.LogInformation("FFmpeg output initialized: {Line}", line.Trim());
                            }
                            else if (line.Contains("frame=", StringComparison.OrdinalIgnoreCase) && line.Contains("fps="))
                            {
                                // First frame progress line indicates encoding has started
                                _log.LogInformation("FFmpeg encoding started: {Line}", line.Trim());
                            }

                            // Log important ffmpeg messages to application log as well
                            if (line.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("Warning", StringComparison.OrdinalIgnoreCase) ||
                                line.Contains("failed", StringComparison.OrdinalIgnoreCase))
                            {
                                _log.LogWarning("FFmpeg stderr: {Line}", line);
                            }
                            if (line.Contains("Opening 'stream.m3u8' for writing") || line.Contains("hls muxer"))
                            {
                                _log.LogInformation("FFmpeg opening HLS playlist for writing");
                                LiveStart = DateTimeOffset.UtcNow;
                            }
                            if (line.Contains("#EXTINF"))
                            {
                                // Some builds echo segment logs to stderr; mark as ready hint
                                if (SegmentCount == 0) LiveStart = DateTimeOffset.UtcNow;
                            }
                        }
                    }
                    catch { }
                });

                var stdoutTask = Task.Run(async () =>
                {
                    try
                    {
                        string? line;
                        while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                        {
                            lastStdoutLine = line;
                            lock (stdoutLock)
                            {
                                recentStdoutLines.Add(line);
                                if (recentStdoutLines.Count > 20)
                                    recentStdoutLines.RemoveAt(0);
                            }
                            try { if (ffLog != null) { lock (logLock) { ffLog.WriteLine($"[STDOUT] {line}"); } } } catch { }
                            // Log stdout messages that might contain errors
                            var lower = line.ToLowerInvariant();
                            if (lower.Contains("error") || lower.Contains("warning") || lower.Contains("failed"))
                            {
                                _log.LogWarning("FFmpeg stdout: {Line}", line);
                            }
                        }
                    }
                    catch { }
                });

                var exitMonitorTask = Task.Run(async () =>
                {
                    try
                    {
                        await proc.WaitForExitAsync();
                        var exitTime = DateTime.Now;
                        var runtime = exitTime - processStartTime;
                        var exitCode = proc.ExitCode;
                        var msg = $"FFmpeg process exited after {runtime.TotalSeconds:F2}s with code {exitCode}";
                        _log.LogWarning(msg);
                        try { if (ffLog != null) { lock (logLock) { ffLog.WriteLine(); ffLog.WriteLine($"========== Process Exit Info =========="); ffLog.WriteLine($"Exit Code: {exitCode}"); ffLog.WriteLine($"Runtime: {runtime.TotalSeconds:F2} seconds"); ffLog.WriteLine($"Exit Time: {exitTime:yyyy-MM-dd HH:mm:ss}"); ffLog.WriteLine($"======================================="); } } } catch { }

                        // If process exited very quickly, it's likely a configuration error
                        if (runtime.TotalSeconds < 2)
                        {
                            _log.LogError("FFmpeg exited within 2 seconds - likely a configuration or initialization error");
                        }
                    }
                    catch { }
                    finally
                    {
                        try { ffLog?.Dispose(); } catch { }
                    }
                });
                // Wait up to ~15s for playlist and at least one segment (ddagrab may need longer to initialize)
                var hlsReadyTimeoutSeconds = cfg?.StartHlsReadyTimeoutSeconds ?? 15;
                var sw = Stopwatch.StartNew();
                var lastLogTime = DateTime.Now;
                var logIntervalSeconds = 3;
                int checkCount = 0;

                while (sw.Elapsed < TimeSpan.FromSeconds(hlsReadyTimeoutSeconds))
                {
                    checkCount++;
                    try
                    {
                        if (File.Exists(playlist))
                        {
                            var text = await File.ReadAllTextAsync(playlist);
                            var segs = CountSegments(text);
                            SegmentCount = segs;
                            if (segs > 0)
                            {
                                if (LiveStart == null) LiveStart = DateTimeOffset.UtcNow;
                                _log.LogInformation("HLS ready: segments={Segs} playlist={Playlist}", segs, playlist);
                                return true;
                            }
                        }

                        // Log progress every few seconds
                        if ((DateTime.Now - lastLogTime).TotalSeconds >= logIntervalSeconds)
                        {
                            lastLogTime = DateTime.Now;
                            var elapsed = sw.Elapsed.TotalSeconds;
                            var procStillRunning = !proc.HasExited;
                            var outputReceived = firstOutputReceived;
                            var timeSinceFirstOutput = outputReceived ? (DateTime.Now - firstOutputTime).TotalSeconds : 0;

                            _log.LogInformation(
                                "Waiting for HLS ready: elapsed={Elapsed:F1}s/{Timeout}s, checks={Checks}, " +
                                "procRunning={Running}, outputReceived={OutputReceived}, timeSinceFirstOutput={TimeSinceOutput:F1}s, " +
                                "playlistExists={PlaylistExists}, segments={Segments}",
                                elapsed, hlsReadyTimeoutSeconds, checkCount, procStillRunning, outputReceived,
                                timeSinceFirstOutput, File.Exists(playlist), SegmentCount);

                            // If process exited early, log it immediately
                            if (!procStillRunning)
                            {
                                _log.LogWarning("FFmpeg process has exited during wait period at {Elapsed:F1}s", elapsed);
                            }

                            // If no output received after 5 seconds, that's suspicious
                            if (!outputReceived && elapsed > 5)
                            {
                                _log.LogWarning("No ffmpeg output received after {Elapsed:F1}s - process may be hung", elapsed);
                            }
                        }
                    }
                    catch { }
                    await Task.Delay(500);
                }
                var procExited = _proc?.HasExited ?? true;
                var procExitCode = procExited ? (_proc?.ExitCode ?? -1) : -1;
                var playlistExists = File.Exists(playlist);
                var cat = failureCategory ?? "unknown";
                var err = $"HLS not ready after timeout ({hlsReadyTimeoutSeconds}s). category={cat} playlistExists={playlistExists} segments={SegmentCount} procExited={procExited} exitCode={procExitCode}";
                if (!string.IsNullOrWhiteSpace(lastStderrLine)) err += $" lastStderr='{lastStderrLine}'";
                if (!string.IsNullOrWhiteSpace(lastStdoutLine)) err += $" lastStdout='{lastStdoutLine}'";
                LastStartErrorMessage = err;
                LastStartFailureCategory = cat;
                if (audioFailureDetected)
                    _audioDisabledForJobId = jobId;
                _log.LogWarning("{Msg} - trying next fallback", err);
                // Log recent stderr lines for better diagnosis
                List<string> stderrSnapshot;
                List<string> stdoutSnapshot;
                lock (stderrLock) { stderrSnapshot = new List<string>(recentStderrLines); }
                lock (stdoutLock) { stdoutSnapshot = new List<string>(recentStdoutLines); }
                if (stderrSnapshot.Count > 0)
                {
                    _log.LogWarning("FFmpeg recent stderr ({Count} lines):\n{Lines}", stderrSnapshot.Count, string.Join("\n", stderrSnapshot));
                }
                if (stdoutSnapshot.Count > 0)
                {
                    _log.LogWarning("FFmpeg recent stdout ({Count} lines):\n{Lines}", stdoutSnapshot.Count, string.Join("\n", stdoutSnapshot));
                }
                // If process exited early, log additional diagnostic information
                if (procExited)
                {
                    var runtime = DateTime.UtcNow - processStartTime;
                    _log.LogError("FFmpeg process exited early after {Runtime:F2}s with exit code {ExitCode}. This indicates a critical failure during initialization.", runtime.TotalSeconds, procExitCode);
                    if (stderrSnapshot.Count == 0 && stdoutSnapshot.Count == 0)
                    {
                        _log.LogError("No output captured from FFmpeg - process may have crashed or failed to start properly. Check FFmpeg installation and dependencies.");
                    }
                }
                try { _proc?.Kill(true); } catch { }
                _proc = null;
            }
            return false;
        }

        public async Task<bool> StopAsync()
        {
            // Cancel any in-flight choppy stream correction so it won't restart ffmpeg
            try { _choppyCts?.Cancel(); } catch { }

            var p = _proc;
            if (p == null) return true;
            try
            {
                try
                {
                    if (!p.HasExited)
                    {
                        try { p.StandardInput.WriteLine("q"); } catch { }
                        await Task.Delay(500);
                    }
                }
                catch { }
                if (!p.HasExited)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await p.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
                }
                if (!p.HasExited)
                {
                    p.Kill(true);
                    using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await p.WaitForExitAsync(cts2.Token); } catch (OperationCanceledException) { }
                }
            }
            catch { }
            finally
            {
                try { p.Dispose(); } catch { }
                _proc = null;
            }
            // Keep CurrentJobId/Dir for FinalizeAsync
            return true;
        }

        public string? CurrentPlaylistPath => CurrentDir == null ? null : Path.Combine(CurrentDir, "stream.m3u8");

        public async Task<string?> FinalizeAsync(bool deleteHls = true)
        {
            if (_finalizing) return null;
            _finalizing = true;
            try
            {
                var playlist = CurrentPlaylistPath;
                if (string.IsNullOrWhiteSpace(playlist) || !File.Exists(playlist)) return null;
                string ffmpeg = _options?.CurrentValue?.FfmpegPath
                                 ?? Environment.GetEnvironmentVariable("FFMPEG")
                                 ?? "ffmpeg";
                var cfg = _options?.CurrentValue;
                var outputRoot = cfg != null && !string.IsNullOrWhiteSpace(cfg.OutputDirectory)
                    ? (Path.IsPathRooted(cfg.OutputDirectory!) ? cfg.OutputDirectory! : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, cfg.OutputDirectory!)))
                    : Path.Combine(LiveRoot, "..", "recordings");
                try { Directory.CreateDirectory(outputRoot); } catch { }
                var outName = $"recast_job_{CurrentJobId ?? 0}.mp4";
                var outPath = Path.GetFullPath(Path.Combine(outputRoot, outName));
                // Extra delay to let the last segment and playlist flush after stop
                try { await Task.Delay(3000); } catch { }

                // Prefer concat of TS segments first (more robust than remuxing an event playlist without ENDLIST)
                var workDir = CurrentDir ?? Path.GetDirectoryName(playlist!)!;
                try
                {
                    var segs = Directory.EnumerateFiles(workDir, "seg*.ts").OrderBy(f => f).ToList();
                    if (segs.Count > 0)
                    {
                        var listPath = Path.Combine(workDir, "files.txt");
                        try { await File.WriteAllLinesAsync(listPath, segs.Select(s => $"file '{s.Replace("'", "'\\''")}'")); } catch { }
                        var finalizeLogLevel = cfg?.FfmpegLogLevel ?? "error";
                        var argsConcat = $"-y -nostdin -loglevel {finalizeLogLevel} -f concat -safe 0 -i \"{listPath}\" -c copy -bsf:a aac_adtstoasc -movflags +faststart \"{outPath}\"";
                        _log.LogInformation("Finalizing via concat: {Args}", argsConcat);
                        using var pConcat = Process.Start(new ProcessStartInfo
                        {
                            FileName = ffmpeg,
                            Arguments = argsConcat,
                            UseShellExecute = false,
                            RedirectStandardError = true,
                            RedirectStandardOutput = true,
                            CreateNoWindow = true,
                            WorkingDirectory = workDir,
                        });
                        if (pConcat != null)
                        {
                            // Setup logging for finalize
                            var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                            try { Directory.CreateDirectory(logDir); } catch { }
                            var finalizeLogPath = Path.Combine(logDir, $"ffmpeg-finalize-job-{(CurrentJobId ?? 0)}-{DateTime.Now:yyyyMMdd_HHmmss}.log");
                            _log.LogInformation("FFmpeg finalize log file: {LogPath}", finalizeLogPath);
                            StreamWriter? ffLog = null;
                            try { ffLog = new StreamWriter(new FileStream(finalizeLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; } catch { }
                            object logLock = new object();
                            try
                            {
                                if (ffLog != null)
                                {
                                    lock (logLock)
                                    {
                                        ffLog.WriteLine($"========== FFmpeg Finalize Log ==========");
                                        ffLog.WriteLine($"Job ID: {CurrentJobId ?? 0}");
                                        ffLog.WriteLine($"Start Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                                        ffLog.WriteLine($"FFmpeg Path: {ffmpeg}");
                                        ffLog.WriteLine($"Command Args: {argsConcat}");
                                        ffLog.WriteLine($"Working Directory: {workDir}");
                                        ffLog.WriteLine($"=========================================");
                                        ffLog.WriteLine();
                                    }
                                }
                            }
                            catch { }

                            var stderrLines = new List<string>();
                            var stdoutLines = new List<string>();
                            var opened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            int segOpened = 0;
                            long totalInBytes = 0;
                            try { foreach (var s in segs) { try { totalInBytes += new FileInfo(s).Length; } catch { } } } catch { }
                            long lastSize = 0;
                            var lastProgressAt = DateTime.UtcNow;
                            var lastLogAt = DateTime.UtcNow;
                            var stderrTask = Task.Run(async () =>
                            {
                                try
                                {
                                    string? line;
                                    while ((line = await pConcat.StandardError.ReadLineAsync()) != null)
                                    {
                                        try { if (ffLog != null) { lock (logLock) { ffLog.WriteLine($"[STDERR] {line}"); } } } catch { }
                                        if (stderrLines.Count < 50) stderrLines.Add(line);
                                        try
                                        {
                                            int a = line.IndexOf("Opening '");
                                            if (a >= 0)
                                            {
                                                int b = line.IndexOf("' for reading", a + 9, StringComparison.Ordinal);
                                                if (b > a)
                                                {
                                                    var path = line.Substring(a + 9, b - (a + 9));
                                                    if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
                                                    {
                                                        if (opened.Add(path))
                                                        {
                                                            Interlocked.Increment(ref segOpened);
                                                            lastProgressAt = DateTime.UtcNow;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            });
                            var stdoutTask = Task.Run(async () =>
                            {
                                try
                                {
                                    string? line;
                                    while ((line = await pConcat.StandardOutput.ReadLineAsync()) != null)
                                    {
                                        try { if (ffLog != null) { lock (logLock) { ffLog.WriteLine($"[STDOUT] {line}"); } } } catch { }
                                        if (stdoutLines.Count < 10) stdoutLines.Add(line);
                                    }
                                }
                                catch { }
                            });

                            var pollCts = new CancellationTokenSource();
                            var logInterval = Math.Max(1, (_options?.CurrentValue?.FinalizeLogIntervalSeconds ?? 5));
                            var lastSzTs = DateTime.UtcNow;
                            var pollTask = Task.Run(async () =>
                            {
                                while (!pConcat.HasExited && !pollCts.IsCancellationRequested)
                                {
                                    try
                                    {
                                        long sz = 0;
                                        try { var fi = new FileInfo(outPath); if (fi.Exists) sz = fi.Length; } catch { }
                                        if (sz > lastSize) { lastSize = sz; lastProgressAt = DateTime.UtcNow; }
                                        if ((DateTime.UtcNow - lastLogAt) >= TimeSpan.FromSeconds(logInterval))
                                        {
                                            lastLogAt = DateTime.UtcNow;
                                            double pct = (totalInBytes > 0) ? Math.Max(0, Math.Min(100.0, (sz * 100.0) / totalInBytes)) : 0.0;
                                            int done = Math.Max(segOpened, opened.Count);
                                            int tot = segs.Count;
                                            double etaSec = 0;
                                            try
                                            {
                                                var now = DateTime.UtcNow;
                                                var dt = (now - lastSzTs).TotalSeconds;
                                                if (dt > 0.5)
                                                {
                                                    var rate = (double)(sz) / dt;
                                                    if (totalInBytes > 0 && rate > 1)
                                                    {
                                                        var remain = Math.Max(0, totalInBytes - sz);
                                                        etaSec = remain / rate;
                                                    }
                                                    lastSzTs = now;
                                                }
                                            }
                                            catch { }
                                            _log.LogInformation("Finalize progress: {Pct:F1}% bytes={W}/{T}MB segs={D}/{Tot} eta~{ETA}s", pct, (int)(sz / (1024 * 1024)), (int)(Math.Max(1, totalInBytes) / (1024 * 1024)), done, tot, (int)etaSec);
                                        }
                                    }
                                    catch { }
                                    await Task.Delay(1000);
                                }
                            });

                            var hardCap = TimeSpan.FromMinutes(_options?.CurrentValue?.FinalizeHardCapMinutes ?? 45);
                            var stallCap = TimeSpan.FromMinutes(_options?.CurrentValue?.FinalizeStallCapMinutes ?? 2);
                            var startAt = DateTime.UtcNow;
                            while (!pConcat.HasExited)
                            {
                                if ((DateTime.UtcNow - startAt) > hardCap) break;
                                if ((DateTime.UtcNow - lastProgressAt) > stallCap) break;
                                await Task.Delay(1000);
                            }
                            pollCts.Cancel();
                            if (!pConcat.HasExited)
                            {
                                try
                                {
                                    _log.LogWarning("Concat finalize timeout — killing ffmpeg and discarding partial MP4");
                                    pConcat.Kill(entireProcessTree: true);
                                }
                                catch { }
                                try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
                            }
                            else
                            {
                                try { await Task.WhenAll(Task.WhenAny(stderrTask, Task.Delay(1000)), Task.WhenAny(stdoutTask, Task.Delay(1000))); } catch { }
                                long sizeC = 0;
                                try { var fiC = new FileInfo(outPath); if (fiC.Exists) sizeC = fiC.Length; } catch { }
                                if (File.Exists(outPath) && sizeC > 300_000 && pConcat.ExitCode == 0)
                                {
                                    _log.LogInformation("MP4 created (concat): {Path}", outPath);
                                    if (deleteHls && CurrentDir != null)
                                    {
                                        try { Directory.Delete(CurrentDir, recursive: true); } catch { }
                                        CurrentDir = null;
                                    }
                                    CurrentJobId = null;
                                    LiveStart = null;
                                    SegmentCount = 0;
                                    try { ffLog?.Dispose(); } catch { }
                                    return outPath;
                                }
                                else
                                {
                                    _log.LogWarning("Concat finalize failed or output too small. exit={Exit} size={Size} stderr={Err}", pConcat.ExitCode, sizeC, string.Join(" | ", stderrLines));
                                }
                            }
                            try { ffLog?.Dispose(); } catch { }
                        }
                    }
                    else
                    {
                        _log.LogWarning("No TS segments found for concat in {Dir}", workDir);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Concat finalize attempt failed");
                }

                // No further fallbacks: if concat failed or MP4 is too small, return null
            }
            catch { }
            finally { _finalizing = false; }
            return null;
        }

        private static List<string> EnumerateDshowAudioDevices(string ffmpeg)
        {
            var list = new List<string>();
            try
            {
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = "-hide_banner -list_devices true -f dshow -i dummy",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                });
                if (p != null)
                {
                    bool inAudio = false;
                    string? line;
                    while ((line = p.StandardError.ReadLine()) != null)
                    {
                        if (line.IndexOf("DirectShow audio", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            inAudio = true; continue;
                        }
                        if (line.IndexOf("DirectShow video", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            inAudio = false; continue;
                        }
                        if (!inAudio) continue;
                        int a = line.IndexOf('"');
                        int b = line.LastIndexOf('"');
                        if (a >= 0 && b > a)
                        {
                            var name = line.Substring(a + 1, b - a - 1).Trim();
                            if (!string.IsNullOrEmpty(name)) list.Add(name);
                        }
                    }
                    try { p.WaitForExit(1500); } catch { }
                }
            }
            catch { }
            return list;
        }

        private static int CountSegments(string playlistText)
        {
            if (string.IsNullOrEmpty(playlistText)) return 0;
            int count = 0;
            using var sr = new StringReader(playlistText);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.StartsWith("#EXTINF", StringComparison.Ordinal)) count++;
            }
            return count;
        }

        private void LogEffectiveConfig(RecorderOptions? cfg, int width, int height, int framerate)
        {
            _log.LogInformation("========== Effective Recording Configuration ==========");
            _log.LogInformation("Resolution: {Width}x{Height} @ {Framerate}fps", width, height, framerate);

            // Capture method
            var captureMethod = cfg?.CaptureMethod ?? "gdigrab";
            _log.LogInformation("Capture method: {Method}", captureMethod);
            if (captureMethod.Equals("ddagrab", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("ddagrab: output_idx={Idx} draw_mouse={Mouse}",
                    cfg?.DdagrabOutputIdx ?? 0, cfg?.DdagrabDrawMouse ?? true);
            }

            // Video encoding
            var hwAccel = cfg?.HwAccel ?? "none";
            var encoder = cfg?.VideoEncoder ?? "libx264";
            _log.LogInformation("HW Accel: {HwAccel}, Encoder: {Encoder}", hwAccel, encoder);
            _log.LogInformation("Video: preset={Preset} crf={Crf} profile={Profile} pix_fmt={PixFmt} threads={Threads}",
                cfg?.VideoPreset ?? "veryfast", cfg?.VideoCrf ?? 23, cfg?.VideoProfile ?? "main",
                cfg?.VideoPixFmt ?? "yuv420p", cfg?.VideoThreads ?? 2);

            // Bitrate settings
            if (cfg?.VideoBitrateK.HasValue == true)
                _log.LogInformation("Video bitrate: {Bitrate}k maxrate={Maxrate}k bufsize={Bufsize}k",
                    cfg.VideoBitrateK, cfg?.VideoMaxrateK ?? 0, cfg?.VideoBufsizeK ?? 0);

            // GOP and HLS
            var gopSeconds = cfg?.GopSeconds ?? 0;
            var gopMult = cfg?.GopMult ?? 2;
            _log.LogInformation("GOP: seconds={GopSec} mult={GopMult} (effective={Effective})",
                gopSeconds, gopMult, gopSeconds > 0 ? framerate * gopSeconds : framerate * gopMult);
            _log.LogInformation("HLS: time={Time} list_size={ListSize} flags={Flags} type={Type}",
                cfg?.HlsTime ?? 2, cfg?.HlsListSize ?? 0, cfg?.HlsFlags ?? "append_list+omit_endlist", cfg?.HlsPlaylistType ?? "event");

            // FPS mode
            _log.LogInformation("FPS mode: {FpsMode}, ForceCFR: {ForceCfr}", cfg?.FpsMode ?? "cfr", cfg?.ForceCfr ?? false);

            // NVENC settings if applicable
            if (hwAccel == "nvenc" || (encoder?.Contains("nvenc", StringComparison.OrdinalIgnoreCase) == true))
            {
                _log.LogInformation("NVENC: preset={Preset} rc={Rc} tune={Tune} cq={Cq} profile={Profile}",
                    cfg?.NvencPreset ?? cfg?.HwPreset ?? "p4", cfg?.NvencRc ?? cfg?.HwRc ?? "vbr",
                    cfg?.NvencTune ?? "", cfg?.NvencCq ?? 0, cfg?.NvencProfile ?? "");
                _log.LogInformation("NVENC AQ: spatial={Spatial} temporal={Temporal} strength={Strength}",
                    cfg?.NvencSpatialAq ?? 0, cfg?.NvencTemporalAq ?? 0, cfg?.NvencAqStrength ?? 0);
                _log.LogInformation("NVENC: bframes={Bframes} lookahead={Lookahead} qp={Qp}",
                    cfg?.NvencBframes ?? 0, cfg?.NvencLookahead ?? 0, cfg?.NvencQp ?? 0);
            }

            // Audio
            _log.LogInformation("Audio: bitrate={Bitrate}k sample_rate={Rate} channels={Ch}",
                cfg?.AudioBitrateK ?? 128, cfg?.AudioSampleRate ?? 48000, cfg?.AudioChannels ?? 2);
            _log.LogInformation("Audio device: api={Api} device={Device}", cfg?.AudioApi ?? "auto", cfg?.AudioDevice ?? "auto");

            // Thread queue sizes
            _log.LogInformation("Thread queues: video={VideoTqs} audio={AudioTqs}",
                cfg?.VideoThreadQueueSize ?? 4096, cfg?.AudioThreadQueueSize ?? 4096);

            // FFmpeg log level
            _log.LogInformation("FFmpeg log level: {LogLevel}", cfg?.FfmpegLogLevel ?? "error");

            _log.LogInformation("=======================================================");
        }

        private string BuildNvencArgs(RecorderOptions? cfg, string vCodec, string vPixFmt, string vProfile,
            int gopSize, int framerate, string vf, string vsyncArg, int aRate, int aBr, int aCh,
            int hlsTime, int hlsListSize, string hlsFlags, string hlsPlaylistType, string segTmpl, string playlist,
            string captureMethod = "gdigrab")
        {
            // Get NVENC-specific settings (new options take precedence over legacy)
            var nvencPreset = cfg?.NvencPreset ?? cfg?.HwPreset;
            var nvencRc = (cfg?.NvencRc ?? cfg?.HwRc)?.Trim().ToLowerInvariant();
            var nvencTune = cfg?.NvencTune;
            var nvencProfile = cfg?.NvencProfile;
            int? vBit = cfg?.VideoBitrateK;
            int? vMax = cfg?.VideoMaxrateK;
            int? vBuf = cfg?.VideoBufsizeK;
            int? qp = cfg?.NvencQp;
            int? cq = cfg?.NvencCq;
            int? bframes = cfg?.NvencBframes;
            int? lookahead = cfg?.NvencLookahead;
            int? spatialAq = cfg?.NvencSpatialAq;
            int? temporalAq = cfg?.NvencTemporalAq;
            int? aqStrength = cfg?.NvencAqStrength;

            // Build rate control arguments
            string vRateArgs;
            if (nvencRc == "cqp" || nvencRc == "constqp")
            {
                vRateArgs = qp.HasValue ? $"-rc constqp -qp {qp.Value}" : "-rc vbr";
            }
            else if (nvencRc == "cq" || nvencRc == "vbr_hq")
            {
                if (cq.HasValue)
                    vRateArgs = $"-rc vbr -cq {cq.Value}";
                else if (vBit.HasValue)
                    vRateArgs = $"-rc vbr -b:v {vBit.Value}k" + (vMax.HasValue ? $" -maxrate {vMax.Value}k" : $" -maxrate {vBit.Value}k") + (vBuf.HasValue ? $" -bufsize {vBuf.Value}k" : "");
                else
                    vRateArgs = "-rc vbr";
            }
            else if (nvencRc == "cbr")
            {
                if (vBit.HasValue)
                {
                    var buf = vBuf ?? vBit.Value * 2;
                    vRateArgs = $"-rc cbr -b:v {vBit.Value}k -maxrate {vBit.Value}k -bufsize {buf}k";
                }
                else
                    vRateArgs = "-rc cbr";
            }
            else
            {
                // Default: VBR with bitrate if specified
                vRateArgs = vBit.HasValue
                    ? $"-rc vbr -b:v {vBit.Value}k" + (vMax.HasValue ? $" -maxrate {vMax.Value}k" : "") + (vBuf.HasValue ? $" -bufsize {vBuf.Value}k" : "")
                    : "-rc vbr";
            }

            // Build encoder arguments
            var presetArg = !string.IsNullOrWhiteSpace(nvencPreset) ? $" -preset {nvencPreset}" : "";
            var tuneArg = !string.IsNullOrWhiteSpace(nvencTune) ? $" -tune {nvencTune}" : "";
            var profileArg = !string.IsNullOrWhiteSpace(nvencProfile) ? $" -profile:v {nvencProfile}" : $" -profile:v {vProfile}";

            // Build advanced NVENC options
            var advancedArgs = "";
            if (bframes.HasValue && bframes.Value > 0)
                advancedArgs += $" -bf {bframes.Value}";
            if (lookahead.HasValue && lookahead.Value > 0)
                advancedArgs += $" -rc-lookahead {lookahead.Value}";
            if (spatialAq.HasValue && spatialAq.Value > 0)
                advancedArgs += " -spatial-aq 1";
            if (temporalAq.HasValue && temporalAq.Value > 0)
                advancedArgs += " -temporal-aq 1";
            if (aqStrength.HasValue && aqStrength.Value > 0)
                advancedArgs += $" -aq-strength {aqStrength.Value}";

            // Skip -vf and -pix_fmt for ddagrab since d3d11 output goes directly to NVENC without CPU filter chain
            var pixFmtArg = (captureMethod == "ddagrab") ? "" : $" -pix_fmt {vPixFmt}";
            var vfArg = (captureMethod == "ddagrab") ? "" : $" -vf \"{vf}\"";

            var vArgs = $"-c:v {vCodec}{presetArg}{tuneArg}{pixFmtArg}{profileArg} {vRateArgs} -g {gopSize}{advancedArgs}";

            return $"{vsyncArg}{vfArg} {vArgs}" +
                   $" -c:a aac -ar {aRate} -b:a {aBr}k -ac {aCh} -af aresample=async=1:min_hard_comp=0.1:first_pts=0" +
                   $" -hls_time {hlsTime} -hls_list_size {hlsListSize} -hls_flags {hlsFlags} -hls_playlist_type {hlsPlaylistType}" +
                   $" -hls_segment_filename \"{segTmpl}\" -f hls \"{playlist}\"";
        }

        private async Task HandleChoppyStreamAsync(int jobId, RecorderOptions? cfg, CancellationToken ct)
        {
            try
            {
                var action = cfg?.ChoppyStreamCorrectionAction?.Trim().ToLowerInvariant() ?? "restart_gpu";
                _log.LogWarning("[ChoppyStreamCorrection] Choppy stream detected for job {JobId}, executing action: {Action}", jobId, action);

                if (ct.IsCancellationRequested)
                {
                    _log.LogInformation("[ChoppyStreamCorrection] Cancelled before executing action for job {JobId} (job already ended)", jobId);
                    _choppyStreamCorrectionInProgress = false;
                    return;
                }

                if (action == "nothing" || action == "none")
                {
                    _log.LogInformation("[ChoppyStreamCorrection] Action is 'nothing', no correction will be performed");
                    _choppyStreamCorrectionInProgress = false;
                    return;
                }

                if (action == "restart_ffmpeg")
                {
                    _log.LogInformation("[ChoppyStreamCorrection] Restarting FFmpeg process for job {JobId}", jobId);
                    
                    await StopAsync();
                    await Task.Delay(2000);

                    if (ct.IsCancellationRequested)
                    {
                        _log.LogInformation("[ChoppyStreamCorrection] Cancelled before restarting FFmpeg for job {JobId} (job already ended)", jobId);
                        _choppyStreamCorrectionInProgress = false;
                        return;
                    }
                    
                    var width = cfg?.Width ?? 1920;
                    var height = cfg?.Height ?? 1080;
                    var framerate = cfg?.Framerate ?? 30;
                    
                    var success = await StartAsync(jobId, width, height, framerate, continueStream: true);
                    if (success)
                    {
                        _log.LogInformation("[ChoppyStreamCorrection] FFmpeg restarted successfully for job {JobId}, stream continued", jobId);
                    }
                    else
                    {
                        _log.LogError("[ChoppyStreamCorrection] Failed to restart FFmpeg for job {JobId}", jobId);
                    }
                    
                    _choppyStreamCorrectionInProgress = false;
                    return;
                }

                if (action == "restart_gpu")
                {
                    _log.LogInformation("[ChoppyStreamCorrection] Restarting GPU and FFmpeg process for job {JobId}", jobId);
                    
                    await StopAsync();
                    
                    if (_gpuRestartService != null)
                    {
                        var gpuSuccess = await _gpuRestartService.RestartNvidiaGpuAsync();
                        if (!gpuSuccess)
                        {
                            _log.LogWarning("[ChoppyStreamCorrection] GPU restart failed, will still attempt to restart FFmpeg");
                        }
                    }
                    else
                    {
                        _log.LogWarning("[ChoppyStreamCorrection] GpuRestartService not available, skipping GPU restart");
                    }
                    
                    await Task.Delay(3000);

                    if (ct.IsCancellationRequested)
                    {
                        _log.LogInformation("[ChoppyStreamCorrection] Cancelled before restarting FFmpeg for job {JobId} (job already ended)", jobId);
                        _choppyStreamCorrectionInProgress = false;
                        return;
                    }
                    
                    var width = cfg?.Width ?? 1920;
                    var height = cfg?.Height ?? 1080;
                    var framerate = cfg?.Framerate ?? 30;
                    
                    var success = await StartAsync(jobId, width, height, framerate, continueStream: true);
                    if (success)
                    {
                        _log.LogInformation("[ChoppyStreamCorrection] GPU and FFmpeg restarted successfully for job {JobId}, stream continued", jobId);
                    }
                    else
                    {
                        _log.LogError("[ChoppyStreamCorrection] Failed to restart FFmpeg after GPU restart for job {JobId}", jobId);
                    }
                    
                    _choppyStreamCorrectionInProgress = false;
                    return;
                }

                if (action == "reboot")
                {
                    _log.LogWarning("[ChoppyStreamCorrection] Rebooting system for job {JobId}", jobId);
                    
                    await StopAsync();
                    
                    // Notify server of failure before rebooting
                    if (_jobFailureCallback != null)
                    {
                        try
                        {
                            _log.LogInformation("[ChoppyStreamCorrection] Notifying server of failure before reboot for job {JobId}", jobId);
                            await _jobFailureCallback(jobId, "Choppy stream detected - system rebooting for recovery");
                            await Task.Delay(2000); // Give notification time to flush
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "[ChoppyStreamCorrection] Failed to notify server before reboot for job {JobId}", jobId);
                        }
                    }
                    else
                    {
                        _log.LogWarning("[ChoppyStreamCorrection] No failure callback configured, server will not be notified before reboot");
                    }
                    
                    if (_gpuRestartService != null)
                    {
                        _gpuRestartService.PerformReboot();
                    }
                    else
                    {
                        _log.LogError("[ChoppyStreamCorrection] GpuRestartService not available, cannot reboot");
                    }
                    
                    return;
                }

                _log.LogWarning("[ChoppyStreamCorrection] Unknown action '{Action}', no correction performed", action);
                _choppyStreamCorrectionInProgress = false;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "[ChoppyStreamCorrection] Exception during choppy stream correction for job {JobId}", jobId);
                _choppyStreamCorrectionInProgress = false;
            }
        }

    }
}
