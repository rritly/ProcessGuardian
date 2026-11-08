using System.Security.AccessControl;
using ProcessGuardian.Infrastructure.Security;

namespace ProcessGuardian.Infrastructure.Autostart;

/// <summary>
/// Security boundary seam for staging the elevated Task Scheduler definition XML
/// (R-AS-XML-IMPORT-SECURITY). The definition imported by <c>schtasks.exe /Create /XML</c>
/// runs with <c>RunLevel=HighestAvailable</c>, so it must never be left in an
/// interactive-user-writable directory: staging happens inside the already protected
/// elevated data root, the file is created exclusively with its born-protected security
/// descriptor supplied at creation, and every gate is fail closed. Production wraps
/// <see cref="ElevatedSecurity"/>; CI tests fabricate the store so no live elevated token
/// or ACL is required — the live-ACL behavior stays a manual elevated checklist.
/// </summary>
internal interface IProtectedTaskXmlStagingStore
{
    /// <summary>True when the directory exists and is not a reparse point; existence is
    /// decided here, the owner/DACL trust classification by <see cref="Classify"/>.</summary>
    bool ExistsAndIsNotReparse(string directoryPath);

    /// <summary>Classifies an existing object (owner/DACL/reparse); <see
    /// cref="ProtectionClassification.UnsafeLocation"/> when unreadable or a reparse.</summary>
    ProtectionClassification Classify(string path, bool isDirectory);

    /// <summary>
    /// Creates a file exclusively with the supplied protected descriptor passed at creation
    /// (model (a): no window in which the file exists under the creator's default ACL) and
    /// writes the bytes. On a pre-existing path this is a collision: an exception is thrown
    /// and the existing file is never overwritten, deleted, or modified. <paramref name="created"/>
    /// is set as soon as the file exists — before the write completes — so the caller can always
    /// clean up exactly the file its own invocation created, even when the write fails mid-way.
    /// </summary>
    void CreateExclusiveProtected(string path, FileSecurity security, byte[] content, out bool created);

    /// <summary>Deletes the staging file; only ever called for a file created by the current
    /// invocation.</summary>
    void Delete(string path);
}

/// <summary>Production <see cref="IProtectedTaskXmlStagingStore"/> backed by <see cref="ElevatedSecurity"/>.</summary>
internal sealed class Win32TaskXmlStagingStore : IProtectedTaskXmlStagingStore
{
    public bool ExistsAndIsNotReparse(string directoryPath) =>
        Directory.Exists(directoryPath) && !ElevatedSecurity.HasReparsePoint(directoryPath);

    public ProtectionClassification Classify(string path, bool isDirectory) =>
        ElevatedSecurity.ClassifyPath(path, isDirectory);

    public void CreateExclusiveProtected(string path, FileSecurity security, byte[] content, out bool created)
    {
        created = false;
        using var stream = ElevatedSecurity.OpenCreateProtectedFile(path, security);
        created = true; // the file now exists under the protected descriptor and belongs to this invocation
        stream.Write(content, 0, content.Length);
    }

    public void Delete(string path) => File.Delete(path);
}
