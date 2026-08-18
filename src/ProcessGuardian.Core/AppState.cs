namespace ProcessGuardian.Core
{
    public sealed class AppState
    {
        // Minimal state container used by UI and controller
        public bool MonitoringActive { get; set; }
        public string? TargetExecutablePath { get; set; }
        public int MaxRestartAttempts { get; set; }
    }
}
