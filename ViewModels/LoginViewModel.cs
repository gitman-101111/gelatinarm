using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.Views;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;
using Exception = System.Exception;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     ViewModel for the LoginPage handling user authentication
    /// </summary>
    public partial class LoginViewModel : BaseViewModel
    {
        // Services
        private readonly IAuthenticationService _authService;
        private readonly INavigationService _navigationService;
        private readonly IPreferencesService _preferencesService;

        [ObservableProperty] private bool _canGoBack;

        [ObservableProperty] private string _errorText = string.Empty;

        [ObservableProperty] private bool _isErrorVisible;

        [ObservableProperty] private bool _isLoadingRingActive;

        [ObservableProperty] private bool _isQuickConnectEnabled = true;

        [ObservableProperty] private string _password = string.Empty;

        private string _serverUrl;

        // Observable properties
        [ObservableProperty] private string _username = string.Empty;

        public LoginViewModel(
            IAuthenticationService authService,
            IPreferencesService preferencesService,
            INavigationService navigationService,
            ILogger<LoginViewModel> logger) : base(logger)
        {
            _authService = authService;
            _preferencesService = preferencesService;
            _navigationService = navigationService;
        }

        /// <summary>
        ///     Initialize the ViewModel with navigation parameter
        /// </summary>
        public Task InitializeAsync(object parameter)
        {
            var context = CreateErrorContext("Initialize", ErrorCategory.User);
            FireAndForget(async () =>
            {
                try
                {
                    // Check for server URL
                    _serverUrl = _preferencesService.GetValue<string>("ServerUrl");
                    if (string.IsNullOrEmpty(_serverUrl))
                    {
                        Logger?.LogWarning("No server URL found in preferences during LoginViewModel initialization");
                        _navigationService.Navigate(typeof(ServerSelectionPage));
                        return;
                    }

                    Logger?.LogInformation($"LoginViewModel initialized with server URL: {_serverUrl}");
                    UpdateApiClientBaseUrl(_serverUrl);

                    // Update navigation state
                    CanGoBack = _navigationService.CanGoBack;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });

            return Task.CompletedTask;
        }

        private void UpdateApiClientBaseUrl(string serverUrl)
        {
            var context = CreateErrorContext("UpdateApiClientBaseUrl", ErrorCategory.Configuration);
            FireAndForget(async () =>
            {
                try
                {
                    // Update the server URL through the authentication service
                    _authService.SetServerUrl(serverUrl);
                    Logger?.LogInformation($"Updated API client BaseUrl to: {serverUrl}");
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        // Commands
        [RelayCommand]
        private async Task LoginAsync()
        {
            IsLoadingRingActive = true;
            HideError();

            // Validate input
            if (string.IsNullOrWhiteSpace(Username))
            {
                ShowError("Please enter a username");
                IsLoadingRingActive = false;
                return;
            }

            var context = CreateErrorContext("Login", ErrorCategory.Authentication);
            bool success;
            try
            {
                // Attempt authentication
                var authResult =
                    await _authService.AuthenticateAsync(Username.Trim(), Password, CancellationToken.None);
                if (authResult)
                {
                    // Clear MainViewModel before navigating to ensure no old server data appears
                    ClearMainViewModelCache("before navigating to MainPage");

                    Logger?.LogInformation("LoginViewModel: Login successful, navigating to MainPage");
                    _navigationService.Navigate(typeof(MainPage), "FromLogin");
                    success = true;
                }
                else
                {
                    ShowError("Invalid username or password");
                    success = false;
                }
            }
            catch (Exception ex)
            {
                success = await ErrorHandler.HandleErrorAsync(ex, context, false,
                    false); // We handle user messaging with ShowError
            }

            IsLoadingRingActive = false;
        }

        [RelayCommand]
        private async Task QuickConnectAsync()
        {
            try
            {
                IsLoadingRingActive = true;
                IsQuickConnectEnabled = false;
                HideError();

                // Verify server URL
                if (string.IsNullOrEmpty(_serverUrl))
                {
                    ShowError("No server configured. Please go back and select a server first.");
                    return;
                }

                UpdateApiClientBaseUrl(_serverUrl);

                Logger?.LogInformation("Initiating Quick Connect...");
                var quickConnectResult = await _authService.InitiateQuickConnectAsync(CancellationToken.None);

                if (quickConnectResult != null && !string.IsNullOrEmpty(quickConnectResult.Code))
                {
                    Logger?.LogInformation(
                        $"Quick Connect initiated successfully with code: {quickConnectResult.Code}");

                    var parameters = new QuickConnectInstructionsParameters
                    {
                        Code = quickConnectResult.Code,
                        ServerUrl = _serverUrl,
                        Secret = quickConnectResult.Secret
                    };

                    _navigationService.Navigate(typeof(QuickConnectInstructionsPage), parameters);
                }
                else
                {
                    Logger?.LogWarning("Quick Connect initiation returned null or empty code");
                    ShowError(
                        "Quick Connect is not available. Please ensure:\n• Quick Connect is enabled on your server (Dashboard > General)\n• Your server supports Quick Connect (version 10.7.0+)");
                }
            }
            catch (Exception ex)
            {
                // Use ErrorHandlingService for logging but keep custom user messages
                var context = CreateErrorContext("QuickConnect", ErrorCategory.Authentication);
                if (ErrorHandler != null)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger.LogError(ex, "Error during quick connect");
                }

                // Show specific error messages based on exception type
                var errorMessage = ex switch
                {
                    ApiException apiEx => apiEx.ResponseStatusCode switch
                    {
                        400 => "Bad request. Please check your server configuration.",
                        401 =>
                            "Server requires authentication. Quick Connect may not be enabled or configured properly.",
                        403 =>
                            "Quick Connect is not enabled on this server. Please ask your server admin to enable it in Dashboard > General > Quick Connect.",
                        404 =>
                            "Quick Connect endpoint not found. Your server may not support Quick Connect or it's disabled.",
                        500 => "Server error. Please try again later.",
                        _ =>
                            $"Server returned status {apiEx.ResponseStatusCode}. Quick Connect may not be available on this server."
                    },
                    HttpRequestException =>
                        "Could not connect to server. Please check your server URL and network connection.",
                    _ => $"Failed to initiate Quick Connect: {ex.Message}"
                };

                ShowError(errorMessage);
            }
            finally
            {
                IsLoadingRingActive = false;
                IsQuickConnectEnabled = true;
            }
        }

        [RelayCommand]
        private void GoBack()
        {
            if (_navigationService.CanGoBack)
            {
                _navigationService.GoBack();
            }
        }

        // Helper methods
        private void ShowError(string message)
        {
            ErrorText = message;
            IsErrorVisible = true;
        }

        private void HideError()
        {
            ErrorText = string.Empty;
            IsErrorVisible = false;
        }
    }
}
