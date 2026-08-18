using System.Threading.Tasks;

namespace ProcessGuardian.Core
{
    public interface IAutostartService
    {
        Task EnableAsync(bool enable);
        bool IsEnabled();
    }
}
