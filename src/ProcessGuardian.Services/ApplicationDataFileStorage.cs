using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Services
{
    public sealed class ApplicationDataFileStorage : IFileStorage
    {
        private readonly string _folderPath;

        public ApplicationDataFileStorage(string publisher, string product)
        {
            // Direct call to Windows App SDK unpackaged API
            try
            {
                var appData = Microsoft.Windows.Storage.ApplicationData.GetForUnpackaged(publisher, product);
                if (appData == null) throw new InvalidOperationException("ApplicationData.GetForUnpackaged returned null.");
                var storageFolder = appData.LocalFolder;
                if (storageFolder == null) throw new InvalidOperationException("ApplicationData.LocalFolder returned null.");
                var path = storageFolder.Path;
                if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("Could not resolve LocalFolder path from ApplicationData.");

                _folderPath = Path.Combine(path, "ProcessGuardian");
            }
            catch (TypeLoadException ex)
            {
                throw new NotSupportedException("Microsoft.Windows.Storage.ApplicationData is not available in this runtime.", ex);
            }
        }

        public Task EnsureFolderExistsAsync(CancellationToken ct = default)
        {
            Directory.CreateDirectory(_folderPath);
            return Task.CompletedTask;
        }

        public Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
        {
            var p = Path.Combine(_folderPath, relativePath);
            return Task.FromResult(File.Exists(p));
        }

        public async Task<string?> ReadAllTextAsync(string relativePath, CancellationToken ct = default)
        {
            var p = Path.Combine(_folderPath, relativePath);
            if (!File.Exists(p)) return null;
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sr = new StreamReader(fs);
            return await sr.ReadToEndAsync().ConfigureAwait(false);
        }

        public async Task WriteAllTextAtomicAsync(string relativePath, string content, CancellationToken ct = default)
        {
            var p = Path.Combine(_folderPath, relativePath);
            var dir = Path.GetDirectoryName(p) ?? _folderPath;
            Directory.CreateDirectory(dir);

            var tempPath = Path.Combine(dir, $"{Path.GetFileName(p)}.tmp-{Guid.NewGuid():N}");
            try
            {
                await File.WriteAllTextAsync(tempPath, content, ct).ConfigureAwait(false);
                // If destination exists, replace atomically
                if (File.Exists(p))
                {
                    File.Replace(tempPath, p, null);
                }
                else
                {
                    File.Move(tempPath, p);
                }
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                throw;
            }
        }
    }
}
