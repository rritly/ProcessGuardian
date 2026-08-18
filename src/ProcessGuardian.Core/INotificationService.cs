using System.Threading.Tasks;

namespace ProcessGuardian.Core
{
    public interface INotificationService
    {
        Task ShowNotificationAsync(string title, string message);
    }
}
