using ProcessGuardian.Core;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Services
{
    public class SystemTimeProvider : ITimeProvider
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

        public Task Delay(TimeSpan delay, CancellationToken ct)
        {
            return Task.Delay(delay, ct);
        }
    }
}
