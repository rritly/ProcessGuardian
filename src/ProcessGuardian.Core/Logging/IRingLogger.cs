using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Core.Logging
{
    public interface IRingLogger
    {
        bool IsEnabled { get; }

        void Log(LogLevel level, string message, Exception? exception = null);

        Task LogAsync(LogLevel level, string message, Exception? exception = null, CancellationToken ct = default);

        Task FlushAsync(CancellationToken ct = default);

        Task ShutdownAsync(CancellationToken ct = default);
    }
}
