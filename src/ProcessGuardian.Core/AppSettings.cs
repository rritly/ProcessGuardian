using System.Text.Json.Serialization;

namespace ProcessGuardian.Core
{
    public sealed class AppSettings
    {
        // Schema versioning
        public int SchemaVersion { get; set; } = 1;

        // Target process identification
        public string? TargetProcessPath { get; set; }
        public string? TargetProcessName { get; set; }

        // Command-line as single string (per requirements)
        public string? TargetProcessArguments { get; set; }

        // Timing and policy
        public int InitialDelaySeconds { get; set; }
        public int CheckIntervalSeconds { get; set; }
        public int MaxRestartAttempts { get; set; }
        public int RestartDelaySeconds { get; set; }
        public int StartupVerificationTimeoutSeconds { get; set; }
        public int FailureCooldownSeconds { get; set; }

        // Logging
        public bool EnableLogging { get; set; }
        public int LogBufferSize { get; set; }

        // Autostart and monitoring
        public bool AutostartEnabled { get; set; }
        public bool MonitoringEnabled { get; set; }
    }
}
