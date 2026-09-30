using ProcessGuardian.Domain.Entities;

namespace ProcessGuardian.Domain.Interfaces;

public interface IConfigurationStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
