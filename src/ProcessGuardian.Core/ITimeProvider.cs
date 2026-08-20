using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Core
{
    public interface ITimeProvider
    {
        Task Delay(TimeSpan delay, CancellationToken ct);
        DateTimeOffset UtcNow { get; }
    }
}
