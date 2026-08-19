using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Core
{
    public sealed class ConfigLoadResult
    {
        public AppSettings Settings { get; set; } = new AppSettings();
        public bool LoadedFromFile { get; set; }
        public ValidationResult Validation { get; set; } = new ValidationResult();
        public string? ErrorMessage { get; set; }
    }

    public interface IConfigService
    {
        Task<ConfigLoadResult> LoadOrCreateAsync(CancellationToken ct = default);
        Task SaveAsync(AppSettings settings, CancellationToken ct = default);
        ValidationResult Validate(AppSettings settings);
        AppSettings GetDefaults();
    }
}
