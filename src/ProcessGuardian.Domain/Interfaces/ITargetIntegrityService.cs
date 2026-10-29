using ProcessGuardian.Domain.ValueObjects;

namespace ProcessGuardian.Domain.Interfaces;

public interface ITargetIntegrityService
{
    // SHA-256 of the file as uppercase hex in Hash (Computed), or a distinct
    // Missing/ReadError outcome. Distinguishing a clean "no file" from a permission/IO
    // failure fixes the old nullable-hash ambiguity (K/E). Never throws except for
    // cancellation, which propagates as OperationCanceledException.
    Task<TargetHashResult> ComputeHashAsync(string filePath, CancellationToken ct = default);
}
