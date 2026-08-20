namespace ProcessGuardian.Core
{
    public sealed class ProcessStartRequest
    {
        public string FileName { get; set; } = string.Empty;
        public string? Arguments { get; set; }
        public string? WorkingDirectory { get; set; }
    }
}
