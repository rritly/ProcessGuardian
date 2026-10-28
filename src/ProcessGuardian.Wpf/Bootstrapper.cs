using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProcessGuardian.Application.Services;
using ProcessGuardian.Domain.Interfaces;
using ProcessGuardian.Infrastructure.Autostart;
using ProcessGuardian.Infrastructure.Configuration;
using ProcessGuardian.Infrastructure.Logging;
using ProcessGuardian.Infrastructure.ProcessMonitoring;
using ProcessGuardian.Infrastructure.Recovery;
using ProcessGuardian.Infrastructure.Status;
using ProcessGuardian.Infrastructure.Tray;
using ProcessGuardian.Wpf.Notifications;
using ProcessGuardian.Wpf.ViewModels;
using ProcessGuardian.Wpf.Views;
using Serilog;
using Serilog.Events;

namespace ProcessGuardian.Wpf;

public static class Bootstrapper
{
    public static void Configure(IServiceCollection services)
    {
        // Configuration
#if ELEVATED_EDITION
        // Crash-safe recovery journal for the quarantine-and-recreate protocol. The store's
        // public constructor takes an optional journal; it is registered only in the elevated
        // edition (the regular user-scoped store does not run the recovery protocol).
        services.AddSingleton<IElevatedRecoveryJournal, RegistryRecoveryJournal>();
#endif
        services.AddSingleton<IConfigurationStore, JsonConfigurationStore>();

        // Create the sink before AddLogging so it can be registered for DI.
        var inMemory = new InMemoryLogSink();
        services.AddSingleton(inMemory);

        // Logging
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(new LoggerConfiguration()
                .MinimumLevel.Debug()
                // Suppress generic host startup noise ("Hosting environment", "Press Ctrl+C")
                // while still allowing Error/Warning from Microsoft.* through.
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
                .WriteTo.Sink(inMemory)
                .CreateLogger(), dispose: true);
        });

        // Infrastructure
        services.AddSingleton<IProcessMonitor, WindowsProcessMonitor>();
        services.AddSingleton<ITargetIntegrityService, Sha256IntegrityService>();
        services.AddSingleton<INotificationService, OverlayNotificationService>();
#if ELEVATED_EDITION
        // HKCU Run cannot auto-elevate; the admin edition uses a scheduled task.
        services.AddSingleton<IAutostartService, ScheduledTaskAutostartService>();
#else
        services.AddSingleton<IAutostartService, RegistryAutostartService>();
#endif
        services.AddSingleton<ITrayService, TrayIconService>();
        services.AddSingleton<IStatusReporter, StatusReporter>();

        // Application
        services.AddSingleton<GuardianOrchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<GuardianOrchestrator>());

        // Presentation
        services.AddSingleton<MainViewModel>();
        // Transient: a closed WPF Window can never be re-shown, so re-opening after
        // a real close must resolve a fresh instance.
        services.AddTransient<MainWindow>();
    }
}
