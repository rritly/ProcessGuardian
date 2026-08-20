using ProcessGuardian.Core;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Tests.Fakes
{
    internal class FailingProcessManager : IProcessManager
    {
        public Task<IReadOnlyList<ProcessInfo>> GetProcessesByNameAsync(string processName, CancellationToken ct = default)
        {
            return Task.FromResult((IReadOnlyList<ProcessInfo>)new List<ProcessInfo>());
        }

        public Task<ProcessStartResult> StartProcessAsync(ProcessStartRequest request, CancellationToken ct = default)
        {
            return Task.FromResult(new ProcessStartResult { Started = false, ErrorMessage = "Simulated failure" });
        }
    }
}
