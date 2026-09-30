using System.Windows;
using System.Windows.Interop;

namespace ProcessGuardian.Wpf.Notifications;

public partial class OverlayWindow : Window
{
    public OverlayWindow()
    {
        InitializeComponent();

        // Position at the bottom-right of the primary monitor's work area (above taskbar).
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 9;
        Top = area.Bottom - Height - 9;
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        // Apply extended window styles so the overlay window does not appear in Alt-Tab
        // and does not steal focus from the foreground application.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    public void AddCard(ToastCard card)
    {
        CardStack.Children.Add(card);
    }

    public void RemoveCard(ToastCard card)
    {
        CardStack.Children.Remove(card);
        if (CardStack.Children.Count == 0)
        {
            Close();
        }
    }

    public int CardCount => CardStack.Children.Count;

    public void RemoveOldestCard()
    {
        if (CardStack.Children.Count == 0) return;
        if (CardStack.Children[0] is ToastCard oldest)
        {
            oldest.Dismiss();
        }
        else
        {
            CardStack.Children.RemoveAt(0);
        }
    }

    private static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    }
}
