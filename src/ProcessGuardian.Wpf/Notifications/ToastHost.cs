using System.Windows;

namespace ProcessGuardian.Wpf.Notifications;

/// <summary>Manages the overlay window and the toast-card stack. Single instance per process.</summary>
public sealed class ToastHost
{
    private static readonly object _lock = new();
    private static ToastHost? _instance;

    private OverlayWindow? _window;

    public static ToastHost Instance
    {
        get
        {
            lock (_lock)
            {
                return _instance ??= new ToastHost();
            }
        }
    }

    /// <summary>Shows a toast card with the given message. UI thread only.</summary>
    public void ShowCard(string message)
    {
        if (System.Windows.Application.Current?.Dispatcher?.CheckAccess() != true)
        {
            throw new InvalidOperationException("ShowCard must be called on the UI thread.");
        }

        if (_window is null || PresentationSource.FromVisual(_window) is null)
        {
            _window = new OverlayWindow();
            _window.Show();
        }

        // Evict oldest cards if the stack exceeds 4. RemoveOldestCard detaches the card
        // synchronously, so the loop always terminates (deferred dismissal would deadlock).
        while (_window.CardCount >= 4)
        {
            _window.RemoveOldestCard();
        }

        var card = new ToastCard(message);
        _window.AddCard(card);
        card.Show(() => _window?.RemoveCard(card));
    }
}
