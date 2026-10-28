namespace ProcessGuardian.Infrastructure.Recovery;

/// <summary>
/// Persistent recovery/notice state for the elevated edition. Production uses the
/// real HKLM-backed <see cref="RegistryRecoveryJournal"/>; CI tests use an in-memory
/// fake so "crash at each step" of quarantine-and-recreate can be forced without an
/// elevated runner.
///
/// The journal records recovery progress and notice acknowledgement only. It never
/// makes a config trusted, never suppresses the per-launch owner/DACL/reparse checks,
/// and never authorizes a target launch. A failure to read or persist a transition
/// must cause the caller to refuse the affected recovery (never touch the config, and
/// never silently lose the notice).
/// </summary>
public interface IElevatedRecoveryJournal
{
    /// <summary>Reads the current journal state; <see cref="RecoveryState.None"/> when absent.</summary>
    Task<RecoveryState> ReadAsync(CancellationToken ct = default);

    /// <summary>Writes a transition; <see cref="RecoveryState.None"/> clears/removes the value.</summary>
    Task WriteAsync(RecoveryState state, CancellationToken ct = default);
}
