using System.Diagnostics.CodeAnalysis;

namespace ProcessGuardian.Domain.ValueObjects;

public sealed record ProcessTarget(
    string FullPath,
    string NameWithoutExtension,
    string WorkingDirectory
)
{
    // Single validation gate for monitored targets: absolute path, .exe extension,
    // local drive only (no UNC shares or device paths), and the file must exist.
    // The orchestrator launches nothing that fails this check.
    public static bool TryCreate(string fullPath, [NotNullWhen(true)] out ProcessTarget? target, [NotNullWhen(false)] out string? error)
    {
        target = null;
        error = null;

        if (string.IsNullOrWhiteSpace(fullPath))
        {
            error = "path is empty";
            return false;
        }

        if (!Path.IsPathFullyQualified(fullPath))
        {
            error = "path is not absolute";
            return false;
        }

        if (!Path.GetExtension(fullPath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "target is not an .exe file";
            return false;
        }

        // Only roots like "X:\" qualify: this rejects UNC ("\\server\share") and
        // device ("\\?\C:\...") paths, keeping the target on a local drive.
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var isLocalDrive = root.Length == 3
            && char.IsAsciiLetter(root[0])
            && root[1] == ':'
            && root[2] == Path.DirectorySeparatorChar;
        if (!isLocalDrive)
        {
            error = "path is not on a local drive";
            return false;
        }

        if (!File.Exists(fullPath))
        {
            error = "file does not exist";
            return false;
        }

        target = new ProcessTarget(
            fullPath,
            Path.GetFileNameWithoutExtension(fullPath),
            Path.GetDirectoryName(fullPath) ?? string.Empty);

        return true;
    }
}
