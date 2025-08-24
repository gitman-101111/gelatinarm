using System;
using System.Threading.Tasks;
using Gelatinarm.Controls;
using Gelatinarm.ViewModels;
using Microsoft.Extensions.Logging;
using Windows.UI.Core;

namespace Gelatinarm.Views
{
    public sealed partial class LoginPage : BasePage
    {
        public LoginPage() : base(typeof(LoginPage))
        {
            InitializeComponent();
        }

        protected override Type ViewModelType => typeof(LoginViewModel);
        public LoginViewModel ViewModel => (LoginViewModel)base.ViewModel;

        protected override async Task InitializePageAsync(object parameter)
        {
            Bindings.Update();

            // Configure Xbox controller support
            ControllerInputHelper.ConfigurePageForController(this, UsernameTextBox, Logger);

            // Configure system back button
            try
            {
                var systemNavigationManager = SystemNavigationManager.GetForCurrentView();
                systemNavigationManager.AppViewBackButtonVisibility = Frame.CanGoBack
                    ? AppViewBackButtonVisibility.Visible
                    : AppViewBackButtonVisibility.Collapsed;
                systemNavigationManager.BackRequested += OnBackRequested;
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Failed to configure system navigation");
            }
            await ViewModel.InitializeAsync(parameter);
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
            if (ViewModel?.CanGoBack == true)
            {
                e.Handled = true;
                ViewModel.GoBackCommand.Execute(null);
            }
        }
    }
}
