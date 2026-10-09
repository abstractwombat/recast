namespace Recast.WindowsRecorder.Config
{
    public class RecorderOptions
    {
        // Recorder identity
        public string? ManagementServerUrl { get; set; }
        public string? RecorderId { get; set; }
        public string? RecorderHostname { get; set; }
        public int? PollInterval { get; set; }
        public int? HeartbeatInterval { get; set; }
        public int? VncPort { get; set; }
        public string? FfmpegPath { get; set; }

        // Audio settings
        public string? AudioApi { get; set; }
        public string? AudioDevice { get; set; }
        public int? AudioBitrateK { get; set; }
        public int? AudioSampleRate { get; set; }
        public int? AudioChannels { get; set; }
        // Static audio input delay (seconds) applied via -itsoffset to compensate for
        // audio capture starting earlier than video capture. Used as the initial guess;
        // replaced by a measured value when live A/V sync calibration corrects it.
        public double? AudioInputOffsetSeconds { get; set; }

        // A/V sync detection and correction
        public bool? AvSyncDetectionEnabled { get; set; }
        public bool? AvSyncLiveCorrectionEnabled { get; set; }
        public bool? AvSyncFinalizeCorrectionEnabled { get; set; }
        public double? AvSyncThresholdSeconds { get; set; }
        public double? AvSyncMaxCorrectionSeconds { get; set; }

        // Recording/capture settings
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? Framerate { get; set; }
        public int? VideoThreadQueueSize { get; set; }
        public int? AudioThreadQueueSize { get; set; }

        // Capture method: "gdigrab" (default) or "ddagrab" (Desktop Duplication API, requires lavfi)
        public string? CaptureMethod { get; set; }
        // ddagrab-specific options
        public int? DdagrabOutputIdx { get; set; }  // Monitor index (0 = primary)
        public bool? DdagrabDrawMouse { get; set; } // Draw mouse cursor (default true)

        // Video encoding settings (software - libx264)
        public string? VideoPreset { get; set; }
        public int? VideoCrf { get; set; }
        public string? VideoProfile { get; set; }
        public string? VideoPixFmt { get; set; }
        public int? VideoThreads { get; set; }
        public int? VideoBitrateK { get; set; }
        public int? VideoMaxrateK { get; set; }
        public int? VideoBufsizeK { get; set; }

        // GOP and HLS settings
        public int? GopMult { get; set; }
        public int? GopSeconds { get; set; }
        public int? HlsTime { get; set; }
        public int? HlsListSize { get; set; }
        public string? HlsFlags { get; set; }
        public string? HlsPlaylistType { get; set; }

        // Frame rate mode
        public string? FpsMode { get; set; }
        public bool? ForceCfr { get; set; }

        // Hardware acceleration
        public string? HwAccel { get; set; }
        public string? VideoEncoder { get; set; }

        // NVENC-specific options (used when HwAccel == "nvenc" or VideoEncoder contains "nvenc")
        public string? NvencPreset { get; set; }
        public string? NvencRc { get; set; }
        public string? NvencTune { get; set; }
        public int? NvencCq { get; set; }
        public string? NvencProfile { get; set; }
        public int? NvencBframes { get; set; }
        public int? NvencLookahead { get; set; }
        public int? NvencSpatialAq { get; set; }
        public int? NvencTemporalAq { get; set; }
        public int? NvencAqStrength { get; set; }
        public int? NvencQp { get; set; }

        // Legacy alias for NvencPreset
        public string? HwPreset { get; set; }
        // Legacy alias for NvencRc
        public string? HwRc { get; set; }

        // Paths
        public string? OutputDirectory { get; set; }

        // Finalize settings
        public int? FinalizeHardCapMinutes { get; set; }
        public int? FinalizeStallCapMinutes { get; set; }
        public int? FinalizeLogIntervalSeconds { get; set; }

        public int? StartRetryAttempts { get; set; }
        public int? StartRetryDelaySeconds { get; set; }
        public int? StartRetryMaxDelaySeconds { get; set; }
        public int? StartHlsReadyTimeoutSeconds { get; set; }
        
        // FFmpeg log level: quiet, panic, fatal, error, warning, info, verbose, debug, trace
        public string? FfmpegLogLevel { get; set; }

        // Recorder (application) log level: verbose, debug, information, warning, error, fatal
        public string? RecorderLogLevel { get; set; }

        // Idle ddagrab probe settings
        public bool? DdaProbeEnabled { get; set; }
        public int? DdaProbeIntervalSeconds { get; set; }
        public int? DdaProbeTimeoutSeconds { get; set; }
        public bool? DdaProbeRestartOnFail { get; set; }

        // Choppy stream detection and correction settings
        public bool? ChoppyStreamDetectionEnabled { get; set; }
        public int? ChoppyStreamThresholdPerSecond { get; set; }
        public string? ChoppyStreamCorrectionAction { get; set; }
        public int? ChoppyStreamGracePeriodSeconds { get; set; }
    }
}
