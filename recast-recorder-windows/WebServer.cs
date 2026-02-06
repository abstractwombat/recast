using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.StaticFiles;
using Serilog;
using Recast.WindowsRecorder.Config;
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
            builder.Services.AddSingleton(rootServices.GetRequiredService<GpuRestartService>());
            builder.Services.AddSingleton(rootServices.GetRequiredService<RecordingManager>());
            builder.Services.AddSingleton(rootServices.GetRequiredService<RecorderState>());
            builder.Services.AddSingleton(rootServices.GetRequiredService<IOptionsMonitor<RecorderOptions>>());

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
                    var isActive = s != null && s.Mode != "none";
                    map[name] = new
                    {
                        display = "windows",
                        mode = s?.Mode ?? "none",
                        active = isActive,
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

            app.MapGet("/api/config", (IOptionsMonitor<RecorderOptions> options) =>
            {
                var cfg = options.CurrentValue ?? new RecorderOptions();
                return Results.Json(new
                {
                    recording = new
                    {
                        capture_method = cfg.CaptureMethod ?? "gdigrab",
                        screen_width = cfg.Width,
                        screen_height = cfg.Height,
                        framerate = cfg.Framerate,
                        force_cfr = cfg.ForceCfr,
                        audio_api = cfg.AudioApi,
                        audio_device = cfg.AudioDevice,
                        ddagrab_output_idx = cfg.DdagrabOutputIdx ?? 0,
                        ddagrab_draw_mouse = cfg.DdagrabDrawMouse ?? true,
                    },
                    video = new
                    {
                        preset = cfg.VideoPreset,
                        crf = cfg.VideoCrf,
                        profile = cfg.VideoProfile,
                        pix_fmt = cfg.VideoPixFmt,
                        gop_multiplier = cfg.GopMult,
                        hls_time = cfg.HlsTime,
                        video_encoder = cfg.VideoEncoder,
                        hw_preset = cfg.HwPreset,
                        hw_rc = cfg.HwRc,
                        bitrate_kbps = cfg.VideoBitrateK,
                        maxrate_kbps = cfg.VideoMaxrateK,
                        bufsize_kbps = cfg.VideoBufsizeK,
                        nvenc_qp = cfg.NvencQp,
                        nvenc_cq = cfg.NvencCq,
                    },
                    audio = new
                    {
                        bitrate_kbps = cfg.AudioBitrateK,
                        sample_rate = cfg.AudioSampleRate,
                        channels = cfg.AudioChannels,
                    },
                    paths = new
                    {
                        ffmpeg_path = cfg.FfmpegPath,
                        output_directory = cfg.OutputDirectory,
                    },
                    debug = new
                    {
                        ffmpeg_log_level = cfg.FfmpegLogLevel,
                        dda_probe_enabled = cfg.DdaProbeEnabled,
                        dda_probe_interval_seconds = cfg.DdaProbeIntervalSeconds,
                        dda_probe_timeout_seconds = cfg.DdaProbeTimeoutSeconds,
                        dda_probe_restart_on_fail = cfg.DdaProbeRestartOnFail,
                    },
                    finalize = new
                    {
                        hard_cap_minutes = cfg.FinalizeHardCapMinutes,
                        stall_cap_minutes = cfg.FinalizeStallCapMinutes,
                        log_interval_seconds = cfg.FinalizeLogIntervalSeconds,
                    },
                    choppy_stream = new
                    {
                        detection_enabled = cfg.ChoppyStreamDetectionEnabled ?? false,
                        threshold_per_second = cfg.ChoppyStreamThresholdPerSecond ?? 10,
                        correction_action = cfg.ChoppyStreamCorrectionAction ?? "restart_gpu",
                    }
                });
            });

            app.MapPost("/api/config", async (HttpContext ctx, IOptionsMonitor<RecorderOptions> options) =>
            {
                try
                {
                    using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                    var root = doc.RootElement;

                    static void SetIfPresent(JsonObject target, JsonElement src, string srcKey, string dstKey)
                    {
                        if (src.ValueKind != JsonValueKind.Object) return;
                        if (!src.TryGetProperty(srcKey, out var el)) return;
                        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined) return;
                        target[dstKey] = JsonNode.Parse(el.GetRawText());
                    }

                    var settingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                    if (!File.Exists(settingsPath))
                        return Results.Json(new { status = "error", message = "appsettings.json not found" }, statusCode: 500);

                    var json = await File.ReadAllTextAsync(settingsPath);
                    var node = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
                    var recorder = node["Recorder"] as JsonObject ?? new JsonObject();
                    node["Recorder"] = recorder;

                    if (root.TryGetProperty("recording", out var recording))
                    {
                        SetIfPresent(recorder, recording, "capture_method", "CaptureMethod");
                        SetIfPresent(recorder, recording, "screen_width", "Width");
                        SetIfPresent(recorder, recording, "screen_height", "Height");
                        SetIfPresent(recorder, recording, "framerate", "Framerate");
                        SetIfPresent(recorder, recording, "force_cfr", "ForceCfr");
                        SetIfPresent(recorder, recording, "audio_api", "AudioApi");
                        SetIfPresent(recorder, recording, "audio_device", "AudioDevice");
                        SetIfPresent(recorder, recording, "ddagrab_output_idx", "DdagrabOutputIdx");
                        SetIfPresent(recorder, recording, "ddagrab_draw_mouse", "DdagrabDrawMouse");
                    }
                    if (root.TryGetProperty("video", out var video))
                    {
                        SetIfPresent(recorder, video, "preset", "VideoPreset");
                        SetIfPresent(recorder, video, "crf", "VideoCrf");
                        SetIfPresent(recorder, video, "profile", "VideoProfile");
                        SetIfPresent(recorder, video, "pix_fmt", "VideoPixFmt");
                        SetIfPresent(recorder, video, "gop_multiplier", "GopMult");
                        SetIfPresent(recorder, video, "hls_time", "HlsTime");
                        SetIfPresent(recorder, video, "video_encoder", "VideoEncoder");
                        SetIfPresent(recorder, video, "hw_preset", "HwPreset");
                        SetIfPresent(recorder, video, "hw_rc", "HwRc");
                        SetIfPresent(recorder, video, "bitrate_kbps", "VideoBitrateK");
                        SetIfPresent(recorder, video, "maxrate_kbps", "VideoMaxrateK");
                        SetIfPresent(recorder, video, "bufsize_kbps", "VideoBufsizeK");
                        SetIfPresent(recorder, video, "nvenc_qp", "NvencQp");
                        SetIfPresent(recorder, video, "nvenc_cq", "NvencCq");
                    }
                    if (root.TryGetProperty("audio", out var audio))
                    {
                        SetIfPresent(recorder, audio, "bitrate_kbps", "AudioBitrateK");
                        SetIfPresent(recorder, audio, "sample_rate", "AudioSampleRate");
                        SetIfPresent(recorder, audio, "channels", "AudioChannels");
                    }
                    if (root.TryGetProperty("paths", out var paths))
                    {
                        SetIfPresent(recorder, paths, "ffmpeg_path", "FfmpegPath");
                        SetIfPresent(recorder, paths, "output_directory", "OutputDirectory");
                    }
                    if (root.TryGetProperty("debug", out var debug))
                    {
                        SetIfPresent(recorder, debug, "ffmpeg_log_level", "FfmpegLogLevel");
                        SetIfPresent(recorder, debug, "dda_probe_enabled", "DdaProbeEnabled");
                        SetIfPresent(recorder, debug, "dda_probe_interval_seconds", "DdaProbeIntervalSeconds");
                        SetIfPresent(recorder, debug, "dda_probe_timeout_seconds", "DdaProbeTimeoutSeconds");
                        SetIfPresent(recorder, debug, "dda_probe_restart_on_fail", "DdaProbeRestartOnFail");
                    }
                    if (root.TryGetProperty("finalize", out var finalize))
                    {
                        SetIfPresent(recorder, finalize, "hard_cap_minutes", "FinalizeHardCapMinutes");
                        SetIfPresent(recorder, finalize, "stall_cap_minutes", "FinalizeStallCapMinutes");
                        SetIfPresent(recorder, finalize, "log_interval_seconds", "FinalizeLogIntervalSeconds");
                    }
                    if (root.TryGetProperty("choppy_stream", out var choppyStream))
                    {
                        SetIfPresent(recorder, choppyStream, "detection_enabled", "ChoppyStreamDetectionEnabled");
                        SetIfPresent(recorder, choppyStream, "threshold_per_second", "ChoppyStreamThresholdPerSecond");
                        SetIfPresent(recorder, choppyStream, "correction_action", "ChoppyStreamCorrectionAction");
                    }

                    var tmp = settingsPath + ".tmp";
                    await File.WriteAllTextAsync(tmp, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    File.Copy(tmp, settingsPath, overwrite: true);
                    try { File.Delete(tmp); } catch { }

                    // Options will refresh via reloadOnChange; return the current snapshot (may lag briefly)
                    var cfg = options.CurrentValue ?? new RecorderOptions();
                    return Results.Json(new { status = "success", message = "Config saved", recorder = cfg.RecorderId });
                }
                catch (Exception ex)
                {
                    return Results.Json(new { status = "error", message = ex.Message }, statusCode: 400);
                }
            });

            app.MapGet("/api/test_recording/status", (RecorderState state, RecordingManager rec) =>
            {
                string? liveUrl = null;
                if (state.IsRecording && state.TestJobId.HasValue)
                {
                    liveUrl = $"/live?job_id={state.TestJobId.Value}";
                }
                return Results.Json(new
                {
                    active = state.IsRecording,
                    elapsed_seconds = state.RecordingElapsedSeconds,
                    job_id = state.TestJobId,
                    live_url = liveUrl,
                    hls_ready = rec.IsReady
                });
            });

            app.MapPost("/api/test_recording/start", async (HttpContext ctx, RecorderState state, RecordingManager rec, SessionManager sm, IOptionsMonitor<RecorderOptions> options) =>
            {
                try
                {
                    var payload = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object>>() ?? new();
                    var controller = payload.ContainsKey("controller") ? payload["controller"]?.ToString() : "default";

                    if (state.IsRecording)
                        return Results.Json(new { status = "error", message = "Recording already in progress" }, statusCode: 400);

                    var session = sm.Get(controller ?? "default");
                    if (session == null)
                        return Results.Json(new { status = "error", message = "No active session" }, statusCode: 400);

                    state.StartRecording();
                    var jobId = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000);
                    state.TestJobId = jobId;
                    var cfg = options.CurrentValue ?? new RecorderOptions();
                    var width = cfg.Width ?? state.ScreenWidth;
                    var height = cfg.Height ?? state.ScreenHeight;
                    var fr = cfg.Framerate ?? state.Framerate;
                    var ok = await rec.StartAsync(jobId, width, height, fr);
                    if (!ok)
                    {
                        state.StopRecording();
                        state.TestJobId = null;
                        return Results.Json(new { status = "error", message = "ffmpeg failed to start" }, statusCode: 500);
                    }

                    // Build live stream URL
                    var liveUrl = $"/live?job_id={jobId}";
                    return Results.Json(new { status = "success", message = "Recording started", job_id = jobId, live_url = liveUrl });
                }
                catch (Exception ex)
                {
                    return Results.Json(new { status = "error", message = ex.Message }, statusCode: 500);
                }
            });

            app.MapPost("/api/test_recording/stop", async (HttpContext ctx, RecorderState state, RecordingManager rec, SessionManager sm) =>
            {
                try
                {
                    if (!state.IsRecording)
                        return Results.Json(new { status = "error", message = "No recording in progress" }, statusCode: 400);

                    var payload = await ctx.Request.ReadFromJsonAsync<Dictionary<string, object>>() ?? new();
                    var stopBrowser = payload.ContainsKey("stop_browser") && payload["stop_browser"]?.ToString() == "true";

                    var elapsed = state.StopRecording();
                    state.TestJobId = null;
                    await rec.StopAsync();

                    // Optionally stop the browser session
                    if (stopBrowser)
                    {
                        await sm.StopAllAsync();
                    }

                    return Results.Json(new { status = "success", elapsed_seconds = elapsed, browser_stopped = stopBrowser });
                }
                catch (Exception ex)
                {
                    return Results.Json(new { status = "error", message = ex.Message }, statusCode: 500);
                }
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
