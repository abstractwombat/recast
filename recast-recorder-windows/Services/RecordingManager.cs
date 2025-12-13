using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using Recast.WindowsRecorder.Config;
using System.Threading;


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

        private readonly IOptions<RecorderOptions>? _options;
        private volatile bool _finalizing;

        public RecordingManager(ILogger<RecordingManager> log, IOptions<RecorderOptions>? options = null)
        {
            _log = log;
            _options = options;
            LiveRoot = Path.Combine(AppContext.BaseDirectory, "hls");
            Directory.CreateDirectory(LiveRoot);
        }

        public bool IsReady => CurrentDir != null && File.Exists(Path.Combine(CurrentDir!, "stream.m3u8")) && SegmentCount > 0;

        public async Task<bool> StartAsync(int jobId, int width = 1920, int height = 1080, int framerate = 30)
        {
            await StopAsync();
            var cfg = _options?.Value;
            width = cfg?.Width ?? width;
            height = cfg?.Height ?? height;
            framerate = cfg?.Framerate ?? framerate;
            Framerate = framerate;
            CurrentJobId = jobId;
            CurrentDir = Path.Combine(LiveRoot, $"job_{jobId}");
            Directory.CreateDirectory(CurrentDir);
            var playlist = Path.Combine(CurrentDir, "stream.m3u8");
            try { File.Delete(playlist); } catch { }
            SegmentCount = 0;

            string ffmpeg = _options?.Value?.FfmpegPath
                             ?? Environment.GetEnvironmentVariable("FFMPEG")
                             ?? "ffmpeg";

            // Prefer ddagrab (Desktop Duplication), fallback to gdigrab. Capture full desktop, scale/pad to output.
            var commonArgs = "-y ";
            var segTmpl = Path.Combine(CurrentDir, "seg%05d.ts");
            var vf = $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}";
            var forceCfr = (cfg?.ForceCfr == true);
            var vPreset = string.IsNullOrWhiteSpace(cfg?.VideoPreset) ? "veryfast" : cfg!.VideoPreset!;
            var vCrf = cfg?.VideoCrf ?? 23;
            var vProfile = string.IsNullOrWhiteSpace(cfg?.VideoProfile) ? "main" : cfg!.VideoProfile!;
            var vPixFmt = string.IsNullOrWhiteSpace(cfg?.VideoPixFmt) ? "yuv420p" : cfg!.VideoPixFmt!;
            var gopMult = cfg?.GopMult ?? 2;
            var hlsTime = cfg?.HlsTime ?? 2;
            var aRate = cfg?.AudioSampleRate ?? 48000;
            var aBr = cfg?.AudioBitrateK ?? 128;
            var aCh = cfg?.AudioChannels ?? 2;
            // Force CFR output with explicit rate to prevent frame duplication stutter
            var vsyncArg = $"-fps_mode cfr -r {framerate}";
            var vCodec = string.IsNullOrWhiteSpace(cfg?.VideoEncoder) ? "libx264" : cfg!.VideoEncoder!;
            var probe = "-probesize 100M -analyzeduration 5M";
            string outArgs;
            if (vCodec.IndexOf("nvenc", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var hwPreset = cfg?.HwPreset;
                var hwRc = cfg?.HwRc?.Trim().ToLowerInvariant();
                int? vBit = cfg?.VideoBitrateK;
                int? vMax = cfg?.VideoMaxrateK;
                int? vBuf = cfg?.VideoBufsizeK;
                int? qp = cfg?.NvencQp;
                int? cq = cfg?.NvencCq;
                string vRateArgs;
                if (hwRc == "cqp" || hwRc == "constqp")
                {
                    vRateArgs = qp.HasValue ? ("-rc constqp -qp " + qp.Value) : "-rc vbr_hq";
                }
                else if (hwRc == "cq")
                {
                    vRateArgs = cq.HasValue ? ("-rc vbr_hq -cq " + cq.Value) : (vBit.HasValue ? ($"-rc vbr_hq -b:v {vBit.Value}k" + (vMax.HasValue ? $" -maxrate {vMax.Value}k" : $" -maxrate {vBit.Value}k") + (vBuf.HasValue ? $" -bufsize {vBuf.Value}k" : "")) : "-rc vbr_hq");
                }
                else if (hwRc == "cbr")
                {
                    if (vBit.HasValue)
                    {
                        var buf = vBuf.HasValue ? vBuf.Value : vBit.Value * 2;
                        vRateArgs = $"-rc cbr -b:v {vBit.Value}k -maxrate {vBit.Value}k -bufsize {buf}k";
                    }
                    else vRateArgs = "-rc cbr";
                }
                else
                {
                    vRateArgs = vBit.HasValue ? ($"-rc vbr_hq -b:v {vBit.Value}k" + (vMax.HasValue ? $" -maxrate {vMax.Value}k" : "") + (vBuf.HasValue ? $" -bufsize {vBuf.Value}k" : "")) : "-rc vbr_hq";
                }
                var presetArg = !string.IsNullOrWhiteSpace(hwPreset) ? (" -preset " + hwPreset) : string.Empty;
                var vArgs = "-c:v " + vCodec + presetArg + " -pix_fmt " + vPixFmt + " -profile:v " + vProfile + " " + vRateArgs + " -g " + (framerate * Math.Max(1, gopMult));
                outArgs = vsyncArg + " -vf \"" + vf + "\" " + vArgs +
                          " -c:a aac -ar " + aRate + " -b:a " + aBr + "k -ac " + aCh + " -af aresample=async=1:min_hard_comp=0.1:first_pts=0" +
                          " -hls_time " + hlsTime + " -hls_list_size 0 -hls_flags append_list+omit_endlist -hls_playlist_type event " +
                          " -hls_segment_filename \"" + segTmpl + "\" -f hls \"" + playlist + "\"";
            }
            else
            {
                outArgs = vsyncArg + " -vf \"" + vf + "\" -c:v libx264 -pix_fmt " + vPixFmt + " -profile:v " + vProfile + " -preset " + vPreset + " -crf " + vCrf + " -g " + (framerate * Math.Max(1, gopMult)) +
                          " -c:a aac -ar " + aRate + " -b:a " + aBr + "k -ac " + aCh + " -af aresample=async=1:min_hard_comp=0.1:first_pts=0" +
                          " -hls_time " + hlsTime + " -hls_list_size 0 -hls_flags append_list+omit_endlist -hls_playlist_type event " +
                          " -hls_segment_filename \"" + segTmpl + "\" -f hls \"" + playlist + "\"";
            }
            var tqs = "-thread_queue_size 4096";

            // Probe DirectShow audio devices (log list) and pick common system-mix names
            string? dshowAudio = null;
            try
            {
                var devices = EnumerateDshowAudioDevices(ffmpeg);
                if (devices.Count > 0) _log.LogInformation("dshow audio devices: {List}", string.Join(", ", devices));
                dshowAudio = devices.FirstOrDefault(n => n.Contains("virtual-audio-capturer", StringComparison.OrdinalIgnoreCase))
                              ?? devices.FirstOrDefault(n => n.Contains("Stereo Mix", StringComparison.OrdinalIgnoreCase))
                              ?? devices.FirstOrDefault(n => n.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase))
                              ?? devices.FirstOrDefault(n => n.Contains("System Virtual Line", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(dshowAudio))
                    _log.LogInformation("Selected dshow audio device: {Dev}", dshowAudio);
            }
            catch { }

            var chainAttempts = new List<string>
            {
                // Use gdigrab as default (more reliable)
                $"{tqs} -rtbufsize 512M -f gdigrab -framerate {framerate} -draw_mouse 1 -i desktop -an {outArgs}",
            };

            var audioApi = _options?.Value?.AudioApi?.Trim().ToLowerInvariant();
            var audioDev = _options?.Value?.AudioDevice?.Trim();

            // Audio input args: use wallclock timestamps for dshow to sync with video capture time
            // Audio input: delay audio by ~100ms to compensate for audio arriving ahead of video frames
            var dshowAudioArgs = $"-itsoffset 0.1 {tqs} -rtbufsize 256M -f dshow -audio_buffer_size 50 -use_wallclock_as_timestamps 1";

            if (!string.IsNullOrWhiteSpace(audioDev))
            {
                if (string.IsNullOrEmpty(audioApi) || audioApi == "dshow")
                {
                    // Insert gdigrab with configured audio device at front
                    chainAttempts.Insert(0, $"{tqs} -rtbufsize 512M -f gdigrab -framerate {framerate} -draw_mouse 1 -i desktop {dshowAudioArgs} -i audio=\"{audioDev}\" {outArgs}");
                    _log.LogInformation("Using configured dshow audio device: {Dev}", audioDev);
                }
            }

            if (!string.IsNullOrWhiteSpace(dshowAudio))
            {
                // Also try an auto-picked dshow system-mix device
                chainAttempts.Insert(0, $"{tqs} -rtbufsize 512M -f gdigrab -framerate {framerate} -draw_mouse 1 -i desktop {dshowAudioArgs} -i audio=\"{dshowAudio}\" {outArgs}");
            }

            foreach (var tail in chainAttempts)
            {
                var args = commonArgs + tail;
                _log.LogInformation("Starting ffmpeg: {Args}", args);
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
                    _proc = null;
                }
                if (_proc != null)
                {
                    var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                    try { Directory.CreateDirectory(logDir); } catch { }
                    var ffmpegLogPath = Path.Combine(logDir, $"ffmpeg-job-{(CurrentJobId ?? jobId)}-{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
                    StreamWriter? ffLog = null;
                    try { ffLog = new StreamWriter(new FileStream(ffmpegLogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; } catch { }
                    object logLock = new object();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            string? line;
                            while ((_proc != null) && !_proc.HasExited && (line = await _proc.StandardError.ReadLineAsync()) != null)
                            {
                                try { if (ffLog != null) { lock (logLock) { ffLog.WriteLine(line); } } } catch { }
                                if (line.Contains("Opening 'stream.m3u8' for writing") || line.Contains("hls muxer"))
                                    LiveStart = DateTimeOffset.UtcNow;
                                if (line.Contains("#EXTINF"))
                                {
                                    // Some builds echo segment logs to stderr; mark as ready hint
                                    if (SegmentCount == 0) LiveStart = DateTimeOffset.UtcNow;
                                }
                            }
                            try { ffLog?.Dispose(); } catch { }
                        }
                        catch { }
                    });
                    // Wait up to ~15s for playlist and at least one segment (ddagrab may need longer to initialize)
                    var sw = Stopwatch.StartNew();
                    while (sw.Elapsed < TimeSpan.FromSeconds(15))
                    {
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
                        }
                        catch { }
                        await Task.Delay(500);
                    }
                    var procExited = _proc?.HasExited ?? true;
                    var procExitCode = procExited ? (_proc?.ExitCode ?? -1) : -1;
                    _log.LogWarning("HLS not ready after timeout. playlistExists={Exists} segments={Segs} procExited={Exited} exitCode={Code} - trying next fallback", 
                        File.Exists(playlist), SegmentCount, procExited, procExitCode);
                    try { _proc?.Kill(true); } catch { }
                    _proc = null;
                }
            }
            return false;
        }

        public async Task<bool> StopAsync()
        {
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
                string ffmpeg = _options?.Value?.FfmpegPath
                                 ?? Environment.GetEnvironmentVariable("FFMPEG")
                                 ?? "ffmpeg";
                var cfg = _options?.Value;
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
                        var argsConcat = $"-y -nostdin -f concat -safe 0 -i \"{listPath}\" -c copy -bsf:a aac_adtstoasc -movflags +faststart \"{outPath}\"";
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
                                        if (stdoutLines.Count < 10) stdoutLines.Add(line);
                                    }
                                }
                                catch { }
                            });

                            var pollCts = new CancellationTokenSource();
                            var logInterval = Math.Max(1, (_options?.Value?.FinalizeLogIntervalSeconds ?? 5));
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
                                            _log.LogInformation("Finalize progress: {Pct:F1}% bytes={W}/{T}MB segs={D}/{Tot} eta~{ETA}s", pct, (int)(sz / (1024*1024)), (int)(Math.Max(1,totalInBytes) / (1024*1024)), done, tot, (int)etaSec);
                                        }
                                    }
                                    catch { }
                                    await Task.Delay(1000);
                                }
                            });

                            var hardCap = TimeSpan.FromMinutes(_options?.Value?.FinalizeHardCapMinutes ?? 45);
                            var stallCap = TimeSpan.FromMinutes(_options?.Value?.FinalizeStallCapMinutes ?? 2);
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
                                    return outPath;
                                }
                                else
                                {
                                    _log.LogWarning("Concat finalize failed or output too small. exit={Exit} size={Size} stderr={Err}", pConcat.ExitCode, sizeC, string.Join(" | ", stderrLines));
                                }
                            }
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


    }
}
