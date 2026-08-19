using Microsoft.UI.Xaml;
using System;

namespace ProcessGuardian
{
    public partial class App : Application
    {
        private Window? _window;
        public ProcessGuardian.Core.AppState AppState { get; private set; } = new ProcessGuardian.Core.AppState();

        public App()
        {
            InitializeComponent();
        }

        protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            // Composition root: initialize ConfigService and load settings
            try
            {
                var storage = new ProcessGuardian.Services.ApplicationDataFileStorage(ProcessGuardian.Services.AppIdentity.Publisher, ProcessGuardian.Services.AppIdentity.Product);
                var configService = new ProcessGuardian.Services.ConfigService(storage);
                var loadResult = await configService.LoadOrCreateAsync();

                // Initialize AppState with loaded or default settings
                AppState = new ProcessGuardian.Core.AppState();
                AppState.Settings = loadResult.Settings ?? configService.GetDefaults();
                if (!string.IsNullOrEmpty(loadResult.ErrorMessage))
                {
                    AppState.LastErrorMessage = loadResult.ErrorMessage;
                    AppState.CurrentStatus = ProcessGuardian.Core.GuardianStatus.Error;
                }
            }
            catch (Exception ex)
            {
                // Recover: keep defaults and record diagnostics
                AppState = new ProcessGuardian.Core.AppState();
                AppState.LastErrorMessage = ex.Message;
                AppState.CurrentStatus = ProcessGuardian.Core.GuardianStatus.Error;
            }

            _window = new MainWindow();
            _window.Activate();
        }
    }
}
