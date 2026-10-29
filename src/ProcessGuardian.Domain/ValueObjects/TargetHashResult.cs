namespace ProcessGuardian.Domain.ValueObjects;

/// <summary>
/// How a target-file hash read resolved (K). The distinction between <c>Missing</c> and
/// <c>ReadError</c> fixes the old nullable-hash ambiguity: a clean "no file" is a distinct
/// outcome from a permission/IO failure, and neither is a hash mismatch. Cancellation is
/// not encoded here — <c>OperationCanceledException</c> propagates as cancellation.
/// </summary>
public enum TargetHashStatus
{
    /// <summary>The file was successfully read and hashed; <see cref="TargetHashResult.Hash"/> is set.</summary>
    Computed,

    /// <summary>The open yielded a clean "no file" result (file or directory not found).</summary>
    Missing,

    /// <summary>Opening/hashing failed for any other reason (permission, IO, unsupported).</summary>
    ReadError,
}

/// <summary>Outcome of a target-file hash read, with the hash only when <see cref="Computed"/>.</summary>
public sealed record TargetHashResult(TargetHashStatus Status, string? Hash)
{
    public static TargetHashResult Computed(string hash) => new(TargetHashStatus.Computed, hash);

    public static TargetHashResult Missing() => new(TargetHashStatus.Missing, null);

    public static TargetHashResult ReadError() => new(TargetHashStatus.ReadError, null);

    public bool IsComputed => Status == TargetHashStatus.Computed;
}
