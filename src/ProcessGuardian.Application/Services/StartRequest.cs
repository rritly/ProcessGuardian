using ProcessGuardian.Domain.Entities;

namespace ProcessGuardian.Application.Services;

/// <summary>
/// A Start request from the UI. Carries the committed (non-draft) target path the
/// user selected and the committed UI-editable settings. The controller computes and
/// stores the canonical path and the target pin itself (K); it never accepts a pin or
/// an enabled flag supplied by the ViewModel.
/// </summary>
public sealed record StartRequest(CommittedSettingsSnapshot Settings, string TargetProcessPath);

/// <summary>Outcome of a Start request.</summary>
public sealed record StartResult(bool Started, string? Reason);
