namespace Recast.WindowsRecorder.Models
{
    public record VncStartReq
    {
        public string? Controller { get; init; }
        public int? Port { get; init; }
    }
    public record LaunchReq
    {
        public string? Controller { get; init; }
        public string? Url { get; init; }
        public string? Mode { get; init; }
        public bool? Keep_open_on_error { get; init; }
    }
    public record StopReq
    {
        public string? Controller { get; init; }
    }
}
