using System.IO;
using System.Security.AccessControl;
using ProcessGuardian.Infrastructure.Configuration;
using ProcessGuardian.Infrastructure.Security;

namespace ProcessGuardian.Infrastructure.Tests;

/// <summary>
/// Fabricated-protected-file-system fake: drives the elevated quarantine/recreate decision
/// logic with scripted classifications and collisions while doing real directory/file I/O
/// in a scratch tree. Live Win32 security (owner/DACL/reparse) is not exercised here — that
/// is the manual elevated checklist — only the store's protocol logic.
/// </summary>
internal sealed class FakeProtectedFileSystem : IProtectedFileSystem
{
    private readonly Dictionary<string, ProtectionClassification> _classifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _createdDirs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _createdFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collisionDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Classification for a pre-existing (not fake-created) plain object; the
    /// default models a foreign/weak owner that must be quarantined.</summary>
    public ProtectionClassification DefaultForExisting { get; set; } = ProtectionClassification.Untrusted;

    public bool ParentBootstrapRefuses { get; set; }
    public bool FailNextCreateDirectoryExclusive { get; set; }
    public bool FailNextCreateFileProtected { get; set; }

    public IEnumerable<string> CreatedDirectories => _createdDirs;

    public void SetClassification(string path, ProtectionClassification classification)
        => _classifications[Path.GetFullPath(path)] = classification;

    public void MakeDirectoryCollide(string path) => _collisionDirs.Add(Path.GetFullPath(path));

    public void EnsureParentContainer(string parentPath)
    {
        if (ParentBootstrapRefuses)
            throw new ConfigurationUnavailableException("test: parent container bootstrap refused");
    }

    public ProtectionClassification Classify(string path, bool isDirectory)
    {
        var full = Path.GetFullPath(path);
        if (_classifications.TryGetValue(full, out var c))
            return c;
        if (_createdFiles.Contains(full) || _createdDirs.Contains(full))
            return ProtectionClassification.Trusted;
        return Directory.Exists(full) || File.Exists(full) ? DefaultForExisting : ProtectionClassification.Untrusted;
    }

    public bool TryCreateDirectoryExclusive(string path, DirectorySecurity security, out bool alreadyExists)
    {
        var full = Path.GetFullPath(path);
        if (FailNextCreateDirectoryExclusive)
        {
            FailNextCreateDirectoryExclusive = false;
            throw new IOException("scripted directory create failure");
        }
        if (_collisionDirs.Contains(full))
        {
            // Collision semantics: NOT created, caller must isolate and retry. Untouched by a
            // single attempt so the bounded-retry refusal path is reproducible.
            alreadyExists = true;
            return false;
        }
        Directory.CreateDirectory(full);
        _createdDirs.Add(full);
        alreadyExists = false;
        return true;
    }

    public void CreateFileProtected(string path, FileSecurity security, byte[] content)
    {
        var full = Path.GetFullPath(path);
        if (FailNextCreateFileProtected)
        {
            FailNextCreateFileProtected = false;
            throw new IOException("scripted file create failure");
        }
        if (File.Exists(full))
            throw new IOException("file already exists (scripted create collision)");
        File.WriteAllBytes(full, content);
        _createdFiles.Add(full);
    }
}
