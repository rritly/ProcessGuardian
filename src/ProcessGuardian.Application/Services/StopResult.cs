namespace ProcessGuardian.Application.Services;

/// <summary>
/// Result of a user-originated Stop/Exit. The two guarantees are deliberately
/// separated: <c>RuntimeStopped</c> means the guardian session actually ended and no
/// launch can occur after the returned Stop; <c>SettingsSaved</c> means the desired
/// <c>enabled=false</c> was durably persisted. A controller never reports
/// <c>RuntimeStopped=false</c> for a completed Stop, and the UI must not label a Stop
/// as fully successful when <c>SettingsSaved=false</c>.
/// </summary>
public sealed record StopResult(bool RuntimeStopped, bool SettingsSaved, Exception? PersistenceError)
{
    public static StopResult Succeeded() => new(true, true, null);

    public static StopResult RuntimeStopped_SaveFailed(Exception error) => new(true, false, error);

    public static StopResult AlreadyStopped() => new(true, true, null);
}
