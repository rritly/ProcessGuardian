using ProcessGuardian.Domain.Enums;

namespace ProcessGuardian.Application.Services;

/// <summary>
/// Application → UI state channel DTO. The controller is the single writer of the
/// published monitoring state; the ViewModel projects it onto <c>GuardianStatus</c>
/// and the <c>IsDegraded</c> binding. <c>SaveFailed</c> is surfaced separately from
/// <c>Status</c>: an Error is published even when a persistence step itself failed.
/// </summary>
public sealed record MonitorState(
    GuardianStatus Status,
    bool IsDegraded,
    string? Reason,
    bool SaveFailed);
