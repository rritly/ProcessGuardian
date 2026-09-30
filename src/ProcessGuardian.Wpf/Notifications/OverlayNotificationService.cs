using Microsoft.Extensions.Logging;
using ProcessGuardian.Domain.Interfaces;

namespace ProcessGuardian.Wpf.Notifications;

public sealed class OverlayNotificationService : INotificationService
{
    private readonly ILogger<OverlayNotificationService> _logger;

    public OverlayNotificationService(ILogger<OverlayNotificationService> logger)
    {
        _logger = logger;
    }

    public Task ShowAsync(string title, string message, CancellationToken ct = default)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _logger.LogWarning("Cannot show overlay notification: no WPF dispatcher available");
            return Task.CompletedTask;
        }

        try
        {
            dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ToastHost.Instance.ShowCard(title);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to show overlay notification card for '{Title}'", title);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to marshal overlay notification to UI thread for '{Title}'", title);
        }

        return Task.CompletedTask;
    }
}
