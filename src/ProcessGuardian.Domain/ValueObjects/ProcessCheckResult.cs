namespace ProcessGuardian.Domain.ValueObjects;

public sealed record ProcessCheckResult(ProcessOutcome Outcome, int? ProcessId = null);

public enum ProcessOutcome
{
    Running,
    NotRunning,

    // Enumeration failed: the process state could not be determined. The
    // orchestrator must skip the cycle instead of triggering a restart that
    // could duplicate a still-running target.
    Unknown
}
