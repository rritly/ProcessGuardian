namespace ProcessGuardian.Domain.Interfaces;

/// <summary>
/// Outcome of an autostart enable/disable operation. <c>Succeeded</c> reports whether the
/// requested state change was verified to have taken effect (actual state == requested
/// state) — never merely that a command exited cleanly or that no exception was thrown.
/// <c>ErrorMessage</c> carries the diagnostic when the operation failed. <c>ActualState</c>
/// is the best-known post-operation registration state read via
/// <see cref="IAutostartService.IsEnabledAsync"/>, or <c>null</c> when it could not be
/// determined. The caller (the controller) must treat a non-success as an error to
/// surface and must not clear a pending recovery notice on a failed action.
/// </summary>
public readonly record struct AutostartResult(bool Succeeded, string? ErrorMessage, bool? ActualState)
{
    public static AutostartResult Success(bool actualState) => new(true, null, actualState);

    public static AutostartResult Failure(string error, bool? actualState = null) => new(false, error, actualState);
}
