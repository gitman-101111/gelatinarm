using System;
using System.Threading.Tasks;
using Gelatinarm.Controls;
using Gelatinarm.ViewModels;
using Microsoft.Extensions.Logging;
using Windows.UI.Core;

namespace Gelatinarm.Views
{
    public sealed partial class ServerSelectionPage : BasePage
    {
        public ServerSelectionPage() : base(typeof(ServerSelectionPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(ServerSelectionViewModel);
        public ServerSelectionViewModel ViewModel => (ServerSelectionViewModel)base.ViewModel;

        protected override async Task InitializePageAsync(object parameter)
        {
            Bindings.Update();

            // Configure Xbox controller support
            ControllerInputHelper.ConfigurePageForController(this, ServerUrlTextBox, Logger);

            // Check if we're coming from logout
            var isAfterLogout = parameter?.ToString() == "AfterLogout";

            // Configure system back button
            try
            {
                var systemNavigationManager = SystemNavigationManager.GetForCurrentView();

                // If coming from logout, hide back button and clear navigation stack
                if (isAfterLogout)
                {
                    systemNavigationManager.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;
                    Frame.BackStack.Clear();
                    Logger?.LogInformation("ServerSelectionPage: Cleared back stack after logout");
                }
                else
                {
                    systemNavigationManager.AppViewBackButtonVisibility = Frame.CanGoBack
                        ? AppViewBackButtonVisibility.Visible
                        : AppViewBackButtonVisibility.Collapsed;
                }

                systemNavigationManager.BackRequested += OnBackRequested;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Failed to configure system navigation");
            }
            await ViewModel.InitializeAsync();
        }

        protected override void CleanupResources()
        {
            // Unsubscribe from back button
            try
            {
                SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Failed to unsubscribe from back button events");
            }

            // Dispose ViewModel
            ViewModel?.Dispose();
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            // Check if we have a valid back stack
            if (!Frame.CanGoBack)
            {
                // No back navigation available (e.g., after logout)
                e.Handled = true;
                Logger?.LogInformation("ServerSelectionPage: Back navigation blocked - no valid back stack");
                return;
            }

            if (ViewModel?.CanGoBack == true)
            {
                e.Handled = true;
                ViewModel.GoBackCommand.Execute(null);
            }
        }
    }
}
