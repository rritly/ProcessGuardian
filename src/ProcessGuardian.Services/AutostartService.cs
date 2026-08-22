using ProcessGuardian.Core;
using System.Reflection;

namespace ProcessGuardian.Services
{
    public sealed class AutostartService : IAutostartService
    {
        private readonly IRunKeyAccessor _runKey;
        private readonly Func<string?> _processPathProvider;
        private readonly IRingLogger? _logger;
        private readonly string _valueName = AppIdentity.Product;

        public AutostartService(IRunKeyAccessor? runKey = null, Func<string?>? processPathProvider = null, IRingLogger? logger = null)
        {
            _runKey = runKey ?? new WindowsRunKeyAccessor();
            _processPathProvider = processPathProvider ?? GetDefaultProcessPath;
            _logger = logger;
        }

        private static string? GetDefaultProcessPath()
        {
            // Prefer Environment.ProcessPath (net8+), fall back to entry assembly location
            return Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
        }

        private static string BuildCommandForPath(string path)
        {
            var quoted = path.Contains(' ') ? $"\"{path}\"" : path;
            return $"{quoted} --background";
        }

        public async Task EnableAsync(bool enable)
        {
            if (enable)
            {
                try
                {
                    var path = _processPathProvider();
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        _logger?.Log("AutostartService: Unable to determine process path; skipping enable.");
                        return;
                    }

                    var command = BuildCommandForPath(path);
                    _runKey.SetValue(_valueName, command);
                }
                catch (Exception ex)
                {
                    _logger?.LogError("AutostartService: Failed to write Run key.", ex);
                    // Do not rethrow; keep non-throwing behavior
                }
            }
            else
            {
                try
                {
                    _runKey.DeleteValue(_valueName);
                }
                catch (Exception ex)
                {
                    _logger?.LogError("AutostartService: Failed to delete Run key.", ex);
                }
            }

            await Task.CompletedTask;
        }

        public bool IsEnabled()
        {
            try
            {
                var val = _runKey.GetValue(_valueName);
                if (string.IsNullOrWhiteSpace(val)) return false;

                var path = _processPathProvider();
                if (string.IsNullOrWhiteSpace(path))
                {
                    // cannot reliably compare without a known process path
                    return false;
                }

                var expected = BuildCommandForPath(path);
                return string.Equals(val.Trim(), expected.Trim(), System.StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger?.LogError("AutostartService: Failed to read Run key.", ex);
                return false;
            }
        }
    }
}
