using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Application.Services;

/// <summary>
/// Outcome of <c>CommitSettingsAsync</c>. The two guarantees are deliberately separated so an
/// autostart failure can never be masked by a successful settings save: <c>Saved</c> reports
/// whether the committed settings were durably persisted, and <c>Autostart</c> reports the
/// verified outcome of the requested autostart state change (null when the operation was not
/// attempted). The UI must not present a fully successful commit unless both parts succeeded.
/// </summary>
public sealed record CommitSettingsResult(bool Saved, AutostartResult? Autostart)
{
    public static CommitSettingsResult Persisted(AutostartResult? autostart) => new(true, autostart);

    public static CommitSettingsResult SaveFailed() => new(false, null);
}
