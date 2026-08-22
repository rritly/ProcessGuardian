using Microsoft.Win32;

namespace ProcessGuardian.Services
{
    internal sealed class WindowsRunKeyAccessor : IRunKeyAccessor
    {
        private const string RunKeyPath = @"Software\\Microsoft\\Windows\\CurrentVersion\\Run";

        public string? GetValue(string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(name) as string;
            }
            catch
            {
                return null;
            }
        }

        public void SetValue(string name, string value)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key.SetValue(name, value, RegistryValueKind.String);
            }
            catch
            {
                // swallow exceptions to avoid elevating errors to callers
            }
        }

        public void DeleteValue(string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null) return;
                var names = key.GetValueNames();
                foreach (var n in names)
                {
                    if (string.Equals(n, name, System.StringComparison.OrdinalIgnoreCase))
                    {
                        key.DeleteValue(n);
                        break;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }
    }
}
