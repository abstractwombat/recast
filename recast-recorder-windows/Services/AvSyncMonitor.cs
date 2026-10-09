using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Recast.WindowsRecorder.Services
{
    /// <summary>
    /// Measures audio/video skew in a media file by comparing the first packet
    /// timestamps of each stream via ffprobe. Audio and video share the same
    /// MPEG-TS clock inside a segment, so the difference between the lowest
    /// video pts and lowest audio pts approximates the inter-stream offset.
    /// Positive skew means audio leads video (audio plays earlier than it should).
    /// </summary>
    public class AvSyncMonitor
    {
        public class Measurement
        {
            public bool HasAudio;
            public bool HasVideo;
            public double? SkewSeconds;
        }

        private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

        private readonly ILogger _log;
        public string FfprobePath { get; }

        public AvSyncMonitor(ILogger log, string ffmpegPath)
        {
            _log = log;
            FfprobePath = ResolveFfprobePath(ffmpegPath);
        }

        public static string ResolveFfprobePath(string ffmpegPath)
        {
            try
            {
                var name = Path.GetFileName(ffmpegPath);
                var dir = Path.GetDirectoryName(ffmpegPath) ?? "";
                var probeName = "ffprobe" + Path.GetExtension(name);
                var candidate = string.IsNullOrEmpty(dir) ? probeName : Path.Combine(dir, probeName);
                if (string.IsNullOrEmpty(dir) || File.Exists(candidate)) return candidate;
            }
            catch { }
            return "ffprobe";
        }

        public async Task<Measurement?> MeasureAsync(string mediaPath, CancellationToken ct)
        {
            var args = "-v error -show_entries stream=index,codec_type:packet=stream_index,pts_time -of json \"" + mediaPath + "\"";
            Process? proc = null;
            try
            {
                proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = FfprobePath,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    }
                };
                if (!proc.Start()) return null;

                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ProbeTimeout);
                await proc.WaitForExitAsync(timeout.Token);

                var stdout = await stdoutTask;
                if (proc.ExitCode != 0)
                {
                    _log.LogDebug("[AvSync] ffprobe failed for {Path}: exit={Exit} stderr={Err}",
                        mediaPath, proc.ExitCode, (await stderrTask).Trim());
                    return null;
                }
                return Parse(stdout);
            }
            catch (OperationCanceledException)
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
                _log.LogDebug("[AvSync] ffprobe timed out for {Path}", mediaPath);
                return null;
            }
            catch (Exception ex)
            {
                try { if (proc != null && !proc.HasExited) proc.Kill(true); } catch { }
                _log.LogDebug(ex, "[AvSync] ffprobe failed for {Path}", mediaPath);
                return null;
            }
            finally
            {
                try { proc?.Dispose(); } catch { }
            }
        }

        private static Measurement? Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var indexToType = new Dictionary<int, string>();
                if (root.TryGetProperty("streams", out var streams))
                {
                    foreach (var s in streams.EnumerateArray())
                    {
                        if (!s.TryGetProperty("index", out var idxEl) || !s.TryGetProperty("codec_type", out var typeEl))
                            continue;
                        var type = typeEl.GetString();
                        if (!string.IsNullOrEmpty(type))
                            indexToType[idxEl.GetInt32()] = type!;
                    }
                }

                double? audioMin = null, videoMin = null;
                if (root.TryGetProperty("packets", out var packets))
                {
                    foreach (var p in packets.EnumerateArray())
                    {
                        if (!p.TryGetProperty("stream_index", out var siEl)) continue;
                        if (!indexToType.TryGetValue(siEl.GetInt32(), out var type)) continue;
                        if (!TryGetDouble(p, "pts_time", out var pts)) continue;

                        if (type == "video")
                            videoMin = videoMin.HasValue ? Math.Min(videoMin.Value, pts) : pts;
                        else if (type == "audio")
                            audioMin = audioMin.HasValue ? Math.Min(audioMin.Value, pts) : pts;
                    }
                }

                var m = new Measurement
                {
                    HasAudio = audioMin.HasValue,
                    HasVideo = videoMin.HasValue,
                };
                if (audioMin.HasValue && videoMin.HasValue)
                    m.SkewSeconds = videoMin.Value - audioMin.Value;
                return m;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryGetDouble(JsonElement el, string prop, out double value)
        {
            value = 0;
            if (!el.TryGetProperty(prop, out var v)) return false;
            if (v.ValueKind == JsonValueKind.Number)
            {
                value = v.GetDouble();
                return true;
            }
            if (v.ValueKind == JsonValueKind.String)
                return double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            return false;
        }
    }
}
