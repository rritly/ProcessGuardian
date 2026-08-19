using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace ProcessGuardian.Tests.Mocks
{
    internal class MockFileStorage : ProcessGuardian.Services.IFileStorage
    {
        private readonly ConcurrentDictionary<string, string> _files = new();
        public bool ThrowOnWrite { get; set; }

        public Task EnsureFolderExistsAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> FileExistsAsync(string relativePath, CancellationToken ct = default)
        {
            return Task.FromResult(_files.ContainsKey(relativePath));
        }

        public Task<string?> ReadAllTextAsync(string relativePath, CancellationToken ct = default)
        {
            _files.TryGetValue(relativePath, out var v);
            return Task.FromResult(v as string);
        }

        public async Task WriteAllTextAtomicAsync(string relativePath, string content, CancellationToken ct = default)
        {
            // simulate small delay and respect cancellation
            await Task.Delay(10, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
            if (ThrowOnWrite) throw new IOException("Simulated write failure");
            _files[relativePath] = content;
        }
    }
}
