using System.Security.AccessControl;
using System.Text;
using ProcessGuardian.Infrastructure.Security;

namespace ProcessGuardian.Infrastructure.Configuration;

/// <summary>Production <see cref="IProtectedFileSystem"/> backed by <see cref="ElevatedSecurity"/>.</summary>
internal sealed class Win32ProtectedFileSystem : IProtectedFileSystem
{
    public void EnsureParentContainer(string parentPath) => ElevatedSecurity.EnsureParentContainer(parentPath);

    public ProtectionClassification Classify(string path, bool isDirectory) =>
        ElevatedSecurity.ClassifyPath(path, isDirectory);

    public bool TryCreateDirectoryExclusive(string path, DirectorySecurity security, out bool alreadyExists) =>
        ElevatedSecurity.TryCreateDirectoryExclusive(path, security, out alreadyExists);

    public void CreateFileProtected(string path, FileSecurity security, byte[] content)
    {
        using var stream = ElevatedSecurity.OpenCreateProtectedFile(path, security);
        stream.Write(content, 0, content.Length);
    }
}
