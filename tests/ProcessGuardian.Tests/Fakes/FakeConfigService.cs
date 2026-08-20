using ProcessGuardian.Core;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Tests.Fakes
{
    internal class FakeConfigService : IConfigService
    {
        private readonly AppSettings _settings;

        public FakeConfigService(AppSettings settings)
        {
            _settings = settings;
        }

        public Task<ConfigLoadResult> LoadOrCreateAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new ConfigLoadResult { Settings = _settings, LoadedFromFile = false, Validation = ValidationResult.Valid() });
        }

        public Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        public ValidationResult Validate(AppSettings settings) => ValidationResult.Valid();

        public AppSettings GetDefaults() => new AppSettings();
    }
}
