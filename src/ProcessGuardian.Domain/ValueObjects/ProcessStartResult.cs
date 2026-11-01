namespace ProcessGuardian.Domain.ValueObjects;

/// <summary>
/// Discriminated outcome of a process-creation call (<c>Process.Start</c>), replacing the old
/// <c>Task&lt;bool&gt;</c> (R-KE). The cases are mutually exclusive and handled exhaustively by
/// the controller so a cleanly missing file, an unreadable target, a genuine start failure, a
/// successful start, and session cancellation are never conflated.
/// </summary>
public enum ProcessStartStatus
{
    /// <summary>The process was created successfully.</summary>
    Started,

    /// <summary>The target file or directory was cleanly absent at the launch boundary.</summary>
    Missing,

    /// <summary>The target existed but could not be opened/read (access, sharing, IO).</summary>
    ReadError,

    /// <summary>Any other process-creation failure not attributable to a missing/unreadable target.</summary>
    Failed,

    /// <summary>The launch was abandoned because the session was cancelled.</summary>
    Cancelled,
}

/// <summary>Typed outcome of <see cref="IProcessMonitor.TryStart"/>.</summary>
public sealed record ProcessStartResult(ProcessStartStatus Status, string? ErrorMessage = null)
{
    public static ProcessStartResult Started() => new(ProcessStartStatus.Started);
    public static ProcessStartResult Missing() => new(ProcessStartStatus.Missing);
    public static ProcessStartResult ReadError() => new(ProcessStartStatus.ReadError);
    public static ProcessStartResult Failed(string error) => new(ProcessStartStatus.Failed, error);
    public static ProcessStartResult Cancelled() => new(ProcessStartStatus.Cancelled);
}
