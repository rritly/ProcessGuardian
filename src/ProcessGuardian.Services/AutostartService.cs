using ProcessGuardian.Core;
using System.Reflection;

namespace ProcessGuardian.Services
{
    public sealed class AutostartService : IAutostartService
    {
        private readonly IRunKeyAccessor _runKey;
        private readonly Func<string?> _processPathProvider;
        private readonly string _valueName = AppIdentity.Product;

        public AutostartService(IRunKeyAccessor? runKey = null, Func<string?>? processPathProvider = null)
        {
            _runKey = runKey ?? new WindowsRunKeyAccessor();
            _processPathProvider = processPathProvider ?? GetDefaultProcessPath;
        }

        private static string? GetDefaultProcessPath()
        {
            // Prefer Environment.ProcessPath (net8+), fall back to entry assembly location
            return Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location;
        }

        public async Task EnableAsync(bool enable)
        {
            try
            {
                if (enable)
                {
                    var path = _processPathProvider();
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        // cannot determine a reliable path; be conservative and do nothing
                        return;
                    }

                    var quoted = path.Contains(' ') ? $"\"{path}\"" : path;
                    var command = $"{quoted} --background";
                    _runKey.SetValue(_valueName, command);
                }
                else
                {
                    _runKey.DeleteValue(_valueName);
                }
            }
            catch
            {
                // Swallow exceptions; services are expected to be conservative and not throw for registry issues
            }

            await Task.CompletedTask;
        }

        public bool IsEnabled()
        {
            try
            {
                var val = _runKey.GetValue(_valueName);
                if (string.IsNullOrWhiteSpace(val)) return false;
                if (!val.Contains("--background")) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
