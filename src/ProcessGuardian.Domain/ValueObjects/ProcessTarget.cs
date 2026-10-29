using System.Diagnostics.CodeAnalysis;

namespace ProcessGuardian.Domain.ValueObjects;

public sealed record ProcessTarget(
    string FullPath,
    string NameWithoutExtension,
    string WorkingDirectory
)
{
    // Path-FORM validation for monitored targets (E): absolute path, .exe extension,
    // local drive only (no UNC shares or device paths), normalized via Path.GetFullPath.
    // Existence/readability is deliberately NOT part of this deterministic, no-I/O check —
    // it is evaluated at runtime by the integrity service (the ability to open/hash the file
    // is the authoritative check). The orchestrator launches nothing that fails this check.
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

        // Normalize ".", ".." and separators only (E). This is a pure string operation — no
        // symlink/reparse resolution (that stays out of scope for the target EXE).
        string canonical;
        try
        {
            canonical = Path.GetFullPath(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "path is not valid";
            return false;
        }

        if (!Path.GetExtension(canonical).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = "target is not an .exe file";
            return false;
        }

        // Only roots like "X:\" qualify: this rejects UNC ("\\server\share") and
        // device ("\\?\C:\...") paths, keeping the target on a local drive.
        var root = Path.GetPathRoot(canonical) ?? string.Empty;
        var isLocalDrive = root.Length == 3
            && char.IsAsciiLetter(root[0])
            && root[1] == ':'
            && root[2] == Path.DirectorySeparatorChar;
        if (!isLocalDrive)
        {
            error = "path is not on a local drive";
            return false;
        }

        target = new ProcessTarget(
            canonical,
            Path.GetFileNameWithoutExtension(canonical),
            Path.GetDirectoryName(canonical) ?? string.Empty);

        return true;
    }
}
