namespace ProcessGuardian.Core
{
    public sealed class ProcessStartResult
    {
        public bool Started { get; set; }
        public int? ProcessId { get; set; }
        public string? ErrorMessage { get; set; }
    }
}
