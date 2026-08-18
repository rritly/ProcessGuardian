using System.Threading.Tasks;

namespace ProcessGuardian.Core
{
    public interface IConfigService
    {
        Task SaveAsync(AppState state);
        Task<AppState> LoadAsync();
    }
}
