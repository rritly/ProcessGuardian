using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ProcessGuardian.Core;

namespace ProcessGuardian.Services
{
    public sealed class ConfigService : IConfigService
    {
        private const string SettingsFileName = "settings.json";
        private readonly IFileStorage _storage;
        // Use deterministic identity constants
        private readonly string _publisher = AppIdentity.Publisher;
        private readonly string _product = AppIdentity.Product;

        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        public ConfigService(IFileStorage? storage = null)
        {
            if (storage != null)
            {
                _storage = storage;
            }
            else
            {
                // create ApplicationDataFileStorage which will throw if ApplicationData is unavailable
                _storage = new ApplicationDataFileStorage(_publisher, _product);
            }
        }

        public AppSettings GetDefaults()
        {
            // Defaults per docs/ProcessGuardian_TZ.md
            return new AppSettings
            {
                SchemaVersion = 1,
                TargetProcessPath = null,
                TargetProcessName = null,
                TargetProcessArguments = null,
                InitialDelaySeconds = 40,
                CheckIntervalSeconds = 20,
                MaxRestartAttempts = 4,
                RestartDelaySeconds = 3,
                StartupVerificationTimeoutSeconds = 10,
                FailureCooldownSeconds = 90,
                EnableLogging = true,
                LogBufferSize = 500,
                AutostartEnabled = true,
                MonitoringEnabled = false
            };
        }

        public ValidationResult Validate(AppSettings settings)
        {
            return AppSettingsValidator.Validate(settings);
        }

        public async Task<ConfigLoadResult> LoadOrCreateAsync(CancellationToken ct = default)
        {
            var result = new ConfigLoadResult();
            try
            {
                await _storage.EnsureFolderExistsAsync(ct).ConfigureAwait(false);
                var exists = await _storage.FileExistsAsync(SettingsFileName, ct).ConfigureAwait(false);
                if (!exists)
                {
                    result.Settings = GetDefaults();
                    result.LoadedFromFile = false;
                    result.Validation = Validate(result.Settings);
                    return result;
                }

                var content = await _storage.ReadAllTextAsync(SettingsFileName, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(content))
                {
                    result.Settings = GetDefaults();
                    result.LoadedFromFile = false;
                    result.Validation = Validate(result.Settings);
                    result.ErrorMessage = "settings file empty";
                    return result;
                }

                AppSettings? settings = null;
                try
                {
                    settings = JsonSerializer.Deserialize<AppSettings>(content, _jsonOptions);
                }
                catch (JsonException je)
                {
                    result.Settings = GetDefaults();
                    result.LoadedFromFile = false;
                    result.Validation = Validate(result.Settings);
                    result.ErrorMessage = "corrupt JSON: " + je.Message;
                    return result;
                }

                if (settings == null)
                {
                    result.Settings = GetDefaults();
                    result.LoadedFromFile = false;
                    result.Validation = Validate(result.Settings);
                    result.ErrorMessage = "deserialized settings are null";
                    return result;
                }

                // Schema handling
                if (settings.SchemaVersion != 1)
                {
                    result.Settings = GetDefaults();
                    result.LoadedFromFile = false;
                    result.Validation = Validate(result.Settings);
                    result.ErrorMessage = $"unsupported schema version: {settings.SchemaVersion}";
                    return result;
                }

                result.Settings = settings;
                result.LoadedFromFile = true;
                result.Validation = Validate(settings);
                return result;
            }
            catch (Exception ex)
            {
                result.Settings = GetDefaults();
                result.LoadedFromFile = false;
                result.Validation = Validate(result.Settings);
                result.ErrorMessage = ex.Message;
                return result;
            }
        }

        public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
        {
            var v = Validate(settings);
            if (!v.IsValid)
            {
                throw new ArgumentException("Settings validation failed: " + string.Join("; ", v.Errors));
            }

            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            await _storage.EnsureFolderExistsAsync(ct).ConfigureAwait(false);
            await _storage.WriteAllTextAtomicAsync(SettingsFileName, json, ct).ConfigureAwait(false);
        }
    }
}
