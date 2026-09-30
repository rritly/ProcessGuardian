namespace ProcessGuardian.Domain.Interfaces;

public interface IAutostartService
{
    Task<bool> IsEnabledAsync(CancellationToken ct = default);
    Task SetEnabledAsync(bool enabled, CancellationToken ct = default);
}
