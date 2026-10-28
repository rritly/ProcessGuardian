namespace ProcessGuardian.Infrastructure.Recovery;

/// <summary>
/// Persistent recovery/notice bookkeeping for the elevated edition. This is a
/// crash-safe recovery journal and a reset-notice latch — it never makes a config
/// trusted, never suppresses the per-launch owner/DACL/reparse checks, and never
/// authorizes a target launch. Absent state means complete.
/// </summary>
public enum RecoveryState
{
    /// <summary>No pending state; the recovery journal is empty (value absent). Complete.</summary>
    None = 0,

    /// <summary>
    /// A quarantine-and-recreate is in progress: a decision to treat an existing config
    /// as untrusted has been persisted, but the recovery has not yet converged. While
    /// present the store must not load/parse/hash/migrate the current settings; it
    /// resumes quarantine-and-recreate on the next launch.
    /// </summary>
    RecoveryInProgress = 1,

    /// <summary>
    /// Fresh protected defaults were written after a quarantine-and-recreate, but the
    /// user has not yet resolved the reset notice. Survives restart until resolution.
    /// </summary>
    ResetNoticePending = 2,
}
