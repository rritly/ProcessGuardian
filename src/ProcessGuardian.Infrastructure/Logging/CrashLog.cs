using ProcessGuardian.Domain;

namespace ProcessGuardian.Infrastructure.Logging;

// Diagnostic log writer for global exception handlers. Files go to %LocalAppData%
// (CWD may be unwritable), size-capped; best-effort and never throws.
public static class CrashLog
{
    private const long MaxBytes = 512 * 1024;

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppEdition.DataDirName);

    public static void Append(string fileName, string message) =>
        Append(LogDirectory, fileName, message, MaxBytes);

    // Internal overload injects directory and size limit so tests can run hermetically.
    internal static void Append(string directory, string fileName, string message, long maxBytes = MaxBytes)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName);
            TryDeleteIfTooLarge(path, maxBytes);
            File.AppendAllText(path, message);
        }
        catch
        {
            // Crash logging is best-effort and must never throw.
        }
    }

    private static void TryDeleteIfTooLarge(string path, long maxBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > maxBytes)
                File.Delete(path);
        }
        catch
        {
            // If deletion fails (file locked), appending still proceeds; the next
            // successful rotation will reclaim space.
        }
    }
}
