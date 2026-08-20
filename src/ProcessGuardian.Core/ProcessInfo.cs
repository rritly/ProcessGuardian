namespace ProcessGuardian.Core
{
    public sealed class ProcessInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; } = string.Empty;
        public string? ExecutablePath { get; set; }
        public bool IsPathAvailable { get; set; }
        public string? PathError { get; set; }
    }
}
