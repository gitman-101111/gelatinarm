using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Home;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Navigation;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.SignIn
{
    public partial class LoginViewModel : BaseViewModel, IPageViewModel
    {
        private readonly IAuthenticationService _authService;
        private readonly INavigationService _navigationService;

        // Shown under the form when set (NullableToVisibilityConverter)
        [ObservableProperty] private string _errorText;

        [ObservableProperty] private bool _isSigningIn;

        [ObservableProperty] private bool _isQuickConnectEnabled = true;

        [ObservableProperty] private string _password = string.Empty;

        [ObservableProperty] private string _username = string.Empty;

        public LoginViewModel(
            IAuthenticationService authService,
            INavigationService navigationService,
            ILogger<LoginViewModel> logger) : base(logger)
        {
            _authService = authService;
            _navigationService = navigationService;
        }

        public void Initialize(object parameter)
        {
            // Pre-fill the username when re-authenticating a saved profile from the picker
            if (parameter is string presetUsername && !string.IsNullOrEmpty(presetUsername))
            {
                Username = presetUsername;
            }

            // The server chosen on the server page, or the saved profile's
            if (string.IsNullOrEmpty(_authService.ServerUrl))
            {
                Logger.LogWarning("No server set - back to server selection");
                _navigationService.Navigate(typeof(ServerSelectionPage));
            }
        }

        [RelayCommand]
        private async Task LoginAsync()
        {
            try
            {
                IsSigningIn = true;
                HideError();

                if (string.IsNullOrWhiteSpace(Username))
                {
                    ShowError("Please enter a username");
                    return;
                }

                var authResult =
                    await _authService.AuthenticateAsync(Username.Trim(), Password, CancellationToken.None);
                if (authResult)
                {
                    _navigationService.Navigate(typeof(MainPage));
                }
                else
                {
                    ShowError("Invalid username or password");
                }
            }
            catch (Exception ex)
            {
                // Logged by the handler, shown inline on the page rather than as a dialog
                var context = CreateErrorContext("Login", ErrorCategory.Authentication);
                await ErrorHandler.HandleErrorAsync(ex, context, false);
                ShowError(ErrorHandler.GetUserMessage(ex, context));
            }
            finally
            {
                IsSigningIn = false;
            }
        }

        [RelayCommand]
        private async Task QuickConnectAsync()
        {
            try
            {
                IsSigningIn = true;
                IsQuickConnectEnabled = false;
                HideError();

                var serverUrl = _authService.ServerUrl;
                if (string.IsNullOrEmpty(serverUrl))
                {
                    ShowError("No server configured. Please go back and select a server first.");
                    return;
                }

                var quickConnectResult = await _authService.InitiateQuickConnectAsync(CancellationToken.None);

                if (quickConnectResult != null && !string.IsNullOrEmpty(quickConnectResult.Code))
                {
                    var parameters = new QuickConnectInstructionsParameters
                    {
                        Code = quickConnectResult.Code,
                        ServerUrl = serverUrl,
                        Secret = quickConnectResult.Secret
                    };

                    _navigationService.Navigate(typeof(QuickConnectInstructionsPage), parameters);
                }
                else
                {
                    Logger.LogWarning("Quick Connect initiation returned null or empty code");
                    ShowError(
                        "Quick Connect is not available. Please ensure:\n• Quick Connect is enabled on your server (Dashboard > General)\n• Your server supports Quick Connect (version 10.7.0+)");
                }
            }
            catch (Exception ex)
            {
                // Logged by the handler, shown inline on the page rather than as a dialog
                var context = CreateErrorContext("QuickConnect", ErrorCategory.Authentication);
                await ErrorHandler.HandleErrorAsync(ex, context, false);
                ShowError(ErrorHandler.GetUserMessage(ex, context));
            }
            finally
            {
                IsSigningIn = false;
                IsQuickConnectEnabled = true;
            }
        }

        private void ShowError(string message)
        {
            ErrorText = message;
        }

        private void HideError()
        {
            ErrorText = null;
        }
    }
}
