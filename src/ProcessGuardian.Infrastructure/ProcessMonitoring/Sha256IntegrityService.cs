using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Infrastructure.ProcessMonitoring;

public sealed class Sha256IntegrityService : ITargetIntegrityService
{
    private readonly ILogger<Sha256IntegrityService> _logger;

    public Sha256IntegrityService(ILogger<Sha256IntegrityService> logger)
    {
        _logger = logger;
    }

    public async Task<string?> ComputeHashAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            await using var stream = File.OpenRead(filePath);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return Convert.ToHexString(hash);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogError(ex, "Failed to hash target file {Path}", filePath);
            return null;
        }
    }
}
