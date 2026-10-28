using System.Security.AccessControl;
using ProcessGuardian.Infrastructure.Security;

namespace ProcessGuardian.Infrastructure.Configuration;

/// <summary>
/// Seam isolating the Win32 security operations (create-exclusive with a trusted-owner
/// descriptor, born-hardened file creation, owner/DACL/reparse classification, and the
/// parent-container bootstrap) so the elevated quarantine/recreate protocol is
/// unit-testable in CI through a fake that fabricates security descriptors and
/// simulated collisions. Production uses <see cref="Win32ProtectedFileSystem"/>; the
/// live-ACL behavior is validated by the manual elevated checklist.
/// </summary>
internal interface IProtectedFileSystem
{
    /// <summary>Bootstraps/verifies the ProgramData parent container; throws
    /// <c>ConfigurationUnavailableException</c> on refusal.</summary>
    void EnsureParentContainer(string parentPath);

    /// <summary>Classifies an existing object (owner/DACL/reparse); <see
    /// cref="ProtectionClassification.UnsafeLocation"/> when unreadable or a reparse.</summary>
    ProtectionClassification Classify(string path, bool isDirectory);

    /// <summary>Create-exclusive directory with the descriptor supplied at creation; true on
    /// creation, <c>alreadyExists=true</c> on a collision (never a silent success).</summary>
    bool TryCreateDirectoryExclusive(string path, DirectorySecurity security, out bool alreadyExists);

    /// <summary>Creates a file with an explicit protected descriptor (model (a)) and writes
    /// the bytes; fails on an existing path.</summary>
    void CreateFileProtected(string path, FileSecurity security, byte[] content);
}
