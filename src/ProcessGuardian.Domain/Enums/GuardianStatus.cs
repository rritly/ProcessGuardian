namespace ProcessGuardian.Domain.Enums;

public enum GuardianStatus
{
    Stopped,      // Gray
    Waiting,      // Yellow/Orange (initial delay)
    Monitoring,   // Green
    Restarting,   // Orange
    Error,        // Red
}
