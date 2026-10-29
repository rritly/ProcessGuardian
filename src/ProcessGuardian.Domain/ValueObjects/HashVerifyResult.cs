namespace ProcessGuardian.Domain.ValueObjects;

/// <summary>
/// Discriminated outcome of the pre-launch integrity verification (K/E/F). A verified
/// launch proceeds; <c>Mismatch</c> is the only fatal hash condition (a successfully-read
/// file whose hash differs from the recorded pin); <c>Missing</c>/<c>ReadError</c> are
/// non-fatal degraded conditions that skip the launch without spending budget; and
/// <c>Cancelled</c> terminates nothing and publishes nothing.
/// </summary>
public enum HashVerifyResult
{
    /// <summary>Pin matched (or an empty-pin compatibility skip applied) — launch may proceed.</summary>
    Verified,

    /// <summary>The file was successfully read but its hash differs from the recorded pin (fatal).</summary>
    Mismatch,

    /// <summary>The open yielded a clean "no file"; non-fatal, no launch, no budget.</summary>
    Missing,

    /// <summary>Opening/hashing failed for a permission/IO reason; non-fatal, no launch, no budget.</summary>
    ReadError,

    /// <summary>The session/stop cancelled the verification; terminates nothing, publishes nothing.</summary>
    Cancelled,
}
