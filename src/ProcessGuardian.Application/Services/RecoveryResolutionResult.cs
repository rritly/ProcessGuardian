namespace ProcessGuardian.Application.Services;

/// <summary>
/// Outcome of an explicit recovery resolution (option A "keep autostart" or option B
/// "remove autostart"). <c>Resolved</c> is true only after every required operation for
/// the chosen option succeeded and was verified and the pending reset notice was cleared;
/// otherwise <c>Resolved</c> is false and <c>ErrorMessage</c> carries a user-visible
/// diagnostic, with the notice left pending and the action retryable.
/// </summary>
public sealed record RecoveryResolutionResult(bool Resolved, string? ErrorMessage)
{
    public static RecoveryResolutionResult Succeeded() => new(true, null);

    public static RecoveryResolutionResult Failed(string error) => new(false, error);
}
