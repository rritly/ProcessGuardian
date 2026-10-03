namespace ProcessGuardian.Domain.Interfaces;

public interface ITargetIntegrityService
{
    // Returns the SHA-256 of the file as uppercase hex, or null when the file
    // cannot be read (missing, locked, access denied). Never throws.
    Task<string?> ComputeHashAsync(string filePath, CancellationToken ct = default);
}
