using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.Shared.Server;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.SignIn
{
    public partial class ServerSelectionViewModel : BaseViewModel, IPageViewModel
    {
        private readonly IAuthenticationService _authService;
        private readonly IDialogService _dialogService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly INavigationService _navigationService;
        private readonly IPreferencesService _preferencesService;

        [ObservableProperty] private bool _allowUntrustedCertificates;

        [ObservableProperty] private bool _isConnecting;

        [ObservableProperty] private string _serverUrl = string.Empty;

        public ServerSelectionViewModel(
            IAuthenticationService authService,
            IPreferencesService preferencesService,
            INavigationService navigationService,
            IDialogService dialogService,
            IHttpClientFactory httpClientFactory,
            ILogger<ServerSelectionViewModel> logger) : base(logger)
        {
            _authService = authService;
            _preferencesService = preferencesService;
            _navigationService = navigationService;
            _dialogService = dialogService;
            _httpClientFactory = httpClientFactory;
        }

        public void Initialize(object parameter)
        {
            // A known server (a rejected token, a profile with none) is offered again: Connect goes
            // straight on to sign-in, and the address can still be changed first
            if (string.IsNullOrEmpty(ServerUrl) && !string.IsNullOrEmpty(_authService.ServerUrl))
            {
                ServerUrl = _authService.ServerUrl;
            }

            // Untrusted certificates stay refused when the setting cannot be read
            ErrorHandler.Run(CreateErrorContext("LoadSettings", ErrorCategory.Configuration), () =>
            {
                AllowUntrustedCertificates =
                    _preferencesService.GetValue<bool>(PreferenceConstants.IgnoreCertificateErrors);
                Logger.LogDebug(
                    "Loaded {PreferenceConstantsIgnoreCertificateErrors}: {AllowUntrustedCertificates}", PreferenceConstants.IgnoreCertificateErrors, AllowUntrustedCertificates);
            });
        }

        [RelayCommand]
        private async Task ConnectAsync()
        {
            var context = CreateErrorContext("ConnectToServer", ErrorCategory.Network);
            try
            {
                IsConnecting = true;

                var input = ServerUrl.Trim();
                if (string.IsNullOrEmpty(input))
                {
                    await ShowErrorAsync("Please enter a server URL", "Connection Error");
                    return;
                }

                // Without a scheme, HTTPS is tried first and HTTP after it
                var hasScheme = input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                input.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                var trimmedUrl = hasScheme ? input : "https://" + input;
                var isAvailable = await TestConnectionAsync(trimmedUrl);

                if (!isAvailable && !hasScheme)
                {
                    Logger.LogWarning("HTTPS connection to {TrimmedUrl} failed. Trying HTTP.", trimmedUrl);
                    trimmedUrl = "http://" + input;
                    isAvailable = await TestConnectionAsync(trimmedUrl);
                }

                if (!isAvailable)
                {
                    await ShowErrorAsync(
                        "Could not connect to server. Please check:\n" +
                        "• The server URL is correct (try including the port, e.g., :8096)\n" +
                        "• The server is accessible from this network\n" +
                        "• If using HTTPS with a self-signed certificate, enable 'Allow Untrusted Certificates'\n" +
                        "• Try using the server's IP address instead of hostname",
                        "Connection Error");
                    return;
                }

                _authService.SetServerUrl(trimmedUrl);
                Logger.LogInformation("Successfully connected to server: {TrimmedUrl}", trimmedUrl);

                _navigationService.Navigate(typeof(LoginPage));
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
            finally
            {
                IsConnecting = false;
            }
        }

        partial void OnAllowUntrustedCertificatesChanged(bool value)
        {
            ErrorHandler.Run(CreateErrorContext("SaveAllowUntrustedCertificates", ErrorCategory.Configuration), () =>
            {
                _preferencesService.SetValue(PreferenceConstants.IgnoreCertificateErrors, value);
                Logger.LogInformation("{PreferenceConstantsIgnoreCertificateErrors} set to {Value}", PreferenceConstants.IgnoreCertificateErrors, value);
            });
        }

        private async Task<bool> TestConnectionAsync(string serverUrl)
        {
            var context = CreateErrorContext("TestConnection", ErrorCategory.Network);
            try
            {
                var testUrl = $"{serverUrl.TrimEnd('/')}/System/Info/Public";
                Logger.LogDebug("Testing connection to {TestUrl} (untrusted certificates allowed: {AllowUntrustedCertificates})", testUrl, AllowUntrustedCertificates);

                // The named client carries the certificate handling App.xaml.cs configures from the setting
                var httpClient = _httpClientFactory.CreateClient(SystemConstants.JellyfinHttpClientName);
                using var response = await httpClient.GetAsync(testUrl, CancellationToken.None);

                Logger.LogDebug(
                    "Connection test response: StatusCode={ResponseStatusCode}, IsSuccess={ResponseIsSuccessStatusCode}", response.StatusCode, response.IsSuccessStatusCode);

                if (!response.IsSuccessStatusCode)
                {
                    Logger.LogWarning(
                        "Connection test failed. Status: {ResponseStatusCode}, Reason: {ResponseReasonPhrase}", response.StatusCode, response.ReasonPhrase);
                }

                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                // Logged with its inner exception; the caller shows one message for every failure
                return await ErrorHandler.HandleErrorAsync(ex, context, defaultValue: false);
            }
        }

        private async Task ShowErrorAsync(string message, string title)
        {
            IsConnecting = false;
            await _dialogService.ShowMessageAsync(title, message);
        }
    }
}
