namespace ProcessGuardian.Domain.Interfaces;

public interface INotificationService
{
    Task ShowAsync(string title, string message, CancellationToken ct = default);
}
