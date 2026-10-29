namespace ProcessGuardian.Domain.Entities;

/// <summary>
/// Immutable identity of a monitoring session. Path and pin are fixed for the whole
/// session: a target change is only effective in the stopped state, and a stale UI
/// save can never retarget a running session. Numeric knobs are not part of the
/// session identity — the controller re-reads those from the latest committed
/// snapshot at the top of each cycle.
/// </summary>
public sealed record SessionSnapshot(
    string TargetProcessPath,
    string TargetNameWithoutExtension,
    string WorkingDirectory,
    string TargetHashSha256);
