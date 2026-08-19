namespace ProcessGuardian.Core
{
    public sealed class AppState
    {
        // Persisted settings
        public AppSettings Settings { get; set; } = new AppSettings();

        // Runtime-only
        public GuardianStatus CurrentStatus { get; set; } = GuardianStatus.Stopped;
        public System.DateTimeOffset? LastCheckTime { get; set; }
        public System.DateTimeOffset? LastRestartAttemptTime { get; set; }
        public int CurrentRestartAttempts { get; set; }
        public string? LastErrorMessage { get; set; }
        public bool IsMonitoringActive { get; set; }
    }
}
