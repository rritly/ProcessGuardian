using System.Threading;
using System.Threading.Tasks;

namespace ProcessGuardian.Core
{
    public interface IProcessGuardianController
    {
        Task StartAsync(CancellationToken token = default);
        Task StopAsync();
        bool IsRunning { get; }
    }
}
