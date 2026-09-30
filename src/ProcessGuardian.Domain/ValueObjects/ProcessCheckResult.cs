namespace ProcessGuardian.Domain.ValueObjects;

public sealed record ProcessCheckResult(ProcessOutcome Outcome, int? ProcessId = null);

public enum ProcessOutcome
{
    Running,
    NotRunning
}
