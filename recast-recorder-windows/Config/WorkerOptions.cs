namespace Recast.WindowsRecorder.Config
{
    public class RecorderOptions
    {
        public string? ManagementServerUrl { get; set; }
        public string? RecorderId { get; set; }
        public string? RecorderHostname { get; set; }
        public int? VncPort { get; set; }
        public string? FfmpegPath { get; set; }
        public string? AudioApi { get; set; }
        public string? AudioDevice { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
        public int? Framerate { get; set; }
        public string? VideoPreset { get; set; }
        public int? VideoCrf { get; set; }
        public string? VideoProfile { get; set; }
        public string? VideoPixFmt { get; set; }
        public int? GopMult { get; set; }
        public int? HlsTime { get; set; }
        public string? VideoEncoder { get; set; }
        public string? HwPreset { get; set; }
        public string? HwRc { get; set; }
        public int? VideoBitrateK { get; set; }
        public int? VideoMaxrateK { get; set; }
        public int? VideoBufsizeK { get; set; }
        public int? NvencQp { get; set; }
        public int? NvencCq { get; set; }
        public int? AudioBitrateK { get; set; }
        public int? AudioSampleRate { get; set; }
        public int? AudioChannels { get; set; }
        public string? OutputDirectory { get; set; }
        public bool? ForceCfr { get; set; }
        public int? FinalizeHardCapMinutes { get; set; }
        public int? FinalizeStallCapMinutes { get; set; }
        public int? FinalizeLogIntervalSeconds { get; set; }
    }
}
