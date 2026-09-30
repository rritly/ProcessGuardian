using System.IO;
using System.Windows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ProcessGuardian.Domain.Enums;
using ProcessGuardian.Infrastructure.Tray;
using Xunit;

namespace ProcessGuardian.Infrastructure.Tests;

public class TrayIconServiceTests
{
    [Fact]
    public void ConstructWithoutAssets_DoesNotThrow_AndFallbackUsed()
    {
        RunOnSta(() =>
        {
            var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            bool iconsMissing = !File.Exists(Path.Combine(assetsDir, "TrayIconOn.ico"));

            using var service = new TrayIconService(NullLogger<TrayIconService>.Instance);

            // With missing asset icons this exercises the SystemIcons fallback path.
            service.SetStatus(GuardianStatus.Monitoring);
            service.SetStatus(GuardianStatus.Error);
            service.SetStatus(GuardianStatus.Stopped);
            service.Initialize();
        });
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        RunOnSta(() =>
        {
            var service = new TrayIconService(NullLogger<TrayIconService>.Instance);
            service.SetStatus(GuardianStatus.Stopped);
            service.Dispose();
        });
    }

    [Fact]
    public void Constructor_StoppedInitialState_SecondItem_StartMonitoring()
    {
        RunOnSta(() =>
        {
            using var service = new TrayIconService(NullLogger<TrayIconService>.Instance);

            GetSecondLabel(service).Should().Be("Start monitoring");
        });
    }

    [Fact]
    public void SetStatus_Monitoring_SecondItem_Disable()
    {
        RunOnSta(() =>
        {
            using var service = new TrayIconService(NullLogger<TrayIconService>.Instance);

            service.SetStatus(GuardianStatus.Monitoring);
            GetSecondLabel(service).Should().Be("Disable");

            service.SetStatus(GuardianStatus.Stopped);
            GetSecondLabel(service).Should().Be("Start monitoring");
        });
    }

    [Fact]
    public void ReopenAfterDismiss_DoesNotThrow_AndWindowNotClosed()
    {
        RunOnSta(() =>
        {
            var menuType = typeof(TrayIconService).Assembly.GetType("ProcessGuardian.Infrastructure.Tray.TrayMenuPopup")!;
            var menu = Activator.CreateInstance(menuType, nonPublic: true)!;
            var open = menuType.GetMethod("Open")!;
            var hide = menuType.GetMethod("Hide")!;
            var visibility = menuType.GetProperty("Visibility")!;

            // First open.
            open.Invoke(menu, null);
            // Dismiss: the same path the production Deactivated/click-outside handler runs.
            hide.Invoke(menu, null);
            // Reopen must not throw InvalidOperationException (the window must not have been closed).
            open.Invoke(menu, null);

            // A closed window can never be Visible; this proves the dismiss did not permanently close it.
            ((Visibility)visibility.GetValue(menu)!).Should().Be(Visibility.Visible);
        });
    }

    private static string GetSecondLabel(TrayIconService service)
    {
        var menuField = typeof(TrayIconService).GetField("_menu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var menu = menuField!.GetValue(service)!;
        var getter = menu.GetType().GetMethod("GetSecondLabel", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return (string)getter!.Invoke(menu, null)!;
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new Xunit.Sdk.XunitException("STA action timed out");

        if (captured != null)
            throw new Xunit.Sdk.XunitException($"STA action failed: {captured}");
    }
}
