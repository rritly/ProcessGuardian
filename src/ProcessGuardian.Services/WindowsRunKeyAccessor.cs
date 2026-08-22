using Microsoft.Win32;

namespace ProcessGuardian.Services
{
    public sealed class WindowsRunKeyAccessor : IRunKeyAccessor
    {
        private const string RunKeyPath = @"Software\\Microsoft\\Windows\\CurrentVersion\\Run";

public string? GetValue(string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(name) as string;
        }

public void SetValue(string name, string value)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            key.SetValue(name, value, RegistryValueKind.String);
        }

public void DeleteValue(string name)
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
    }
}
