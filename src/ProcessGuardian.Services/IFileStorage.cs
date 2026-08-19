using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Services
{
    public interface IFileStorage
    {
        Task EnsureFolderExistsAsync(CancellationToken ct = default);
        Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default);
        Task<string?> ReadAllTextAsync(string relativePath, CancellationToken ct = default);
        Task WriteAllTextAtomicAsync(string relativePath, string content, CancellationToken ct = default);
    }
}
