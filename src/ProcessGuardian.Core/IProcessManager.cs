using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Core
{
    public interface IProcessManager
    {
        Task<IReadOnlyList<ProcessInfo>> GetProcessesByNameAsync(string processName, CancellationToken ct = default);

        Task<ProcessStartResult> StartProcessAsync(ProcessStartRequest request, CancellationToken ct = default);
    }
}
