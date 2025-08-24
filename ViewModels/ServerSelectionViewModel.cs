using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.Views;
using Microsoft.Extensions.Logging;
using Windows.System.Profile;
using Exception = System.Exception;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     ViewModel for the ServerSelectionPage handling server connection setup
    /// </summary>
    public partial class ServerSelectionViewModel : BaseViewModel
    {
        private readonly IAuthenticationService _authService;
        private readonly IDialogService _dialogService;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly INavigationService _navigationService;

        // Services
        private readonly IPreferencesService _preferencesService;

        [ObservableProperty] private bool _allowUntrustedCertificates;

        [ObservableProperty] private bool _canGoBack;

        [ObservableProperty] private bool _isLoadingOverlayVisible;

        [ObservableProperty] private bool _isLoadingRingActive;

        [ObservableProperty] private string _loadingText = "Connecting...";

        // Observable properties
        [ObservableProperty] private string _serverUrl = string.Empty;

        public ServerSelectionViewModel(
            IPreferencesService preferencesService,
            IAuthenticationService authService,
            INavigationService navigationService,
            IDialogService dialogService,
            IHttpClientFactory httpClientFactory,
            ILogger<ServerSelectionViewModel> logger) : base(logger)
        {
            _preferencesService = preferencesService;
            _authService = authService;
            _navigationService = navigationService;
            _dialogService = dialogService;
            _httpClientFactory = httpClientFactory;
        }

        /// <summary>
        ///     Initialize the ViewModel
        /// </summary>
        public Task InitializeAsync()
        {
            var context = CreateErrorContext("Initialize", ErrorCategory.Configuration);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    // Load saved settings
                    LoadSettings();

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

        private void LoadSettings()
        {
            var context = CreateErrorContext("LoadSettings", ErrorCategory.Configuration);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    // Load the ignore certificate errors setting
                    AllowUntrustedCertificates =
                        _preferencesService.GetValue<bool>(PreferenceConstants.IgnoreCertificateErrors);
                    Logger?.LogInformation(
                        $"Loaded {PreferenceConstants.IgnoreCertificateErrors}: {AllowUntrustedCertificates}");
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });

            // If error occurred, default to false for security
            if (IsError)
            {
                AllowUntrustedCertificates = false;
            }
        }

        // Commands
        [RelayCommand]
        private async Task ConnectAsync()
        {
            var context = CreateErrorContext("ConnectToServer", ErrorCategory.Network);
            try
            {
                ShowLoading(true);

                var trimmedUrl = ServerUrl?.Trim();
                if (string.IsNullOrEmpty(trimmedUrl))
                {
                    await ShowErrorAsync("Please enter a server URL", "Connection Error");
                    return;
                }

                // Add scheme if not provided
                if (!trimmedUrl.StartsWith("http://") && !trimmedUrl.StartsWith("https://"))
                {
                    // Attempt HTTPS first by default
                    trimmedUrl = "https://" + trimmedUrl;
                    Logger?.LogInformation($"No scheme provided, attempting HTTPS first: {trimmedUrl}");
                }

                var isAvailable = await TestConnectionAsync(trimmedUrl);

                // If HTTPS fails and original input had no scheme, try HTTP as fallback
                if (!isAvailable && !ServerUrl.Trim().Contains("://"))
                {
                    Logger?.LogWarning($"HTTPS connection to {trimmedUrl} failed. Trying HTTP.");
                    trimmedUrl = "http://" + ServerUrl.Trim().Split(new[] { "://" }, StringSplitOptions.None).Last();
                    Logger?.LogInformation($"Attempting HTTP fallback: {trimmedUrl}");
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

                // Save successful URL
                _preferencesService.SetValue(PreferenceConstants.ServerUrl, trimmedUrl);
                Logger?.LogInformation($"Successfully connected to server: {trimmedUrl}");

                // Navigate to login page
                _navigationService.Navigate(typeof(LoginPage));
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
            finally
            {
                ShowLoading(false);
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

        // Property change handlers
        partial void OnAllowUntrustedCertificatesChanged(bool value)
        {
            var context = CreateErrorContext("SaveAllowUntrustedCertificates", ErrorCategory.Configuration);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    _preferencesService.SetValue(PreferenceConstants.IgnoreCertificateErrors, value);
                    Logger?.LogInformation($"{PreferenceConstants.IgnoreCertificateErrors} set to {value}");
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        // Helper methods
        private async Task<bool> TestConnectionAsync(string serverUrl)
        {
            var context = CreateErrorContext("TestConnection", ErrorCategory.Network);
            try
            {
                var testUrl = $"{serverUrl.TrimEnd('/')}/System/Info/Public";
                Logger?.LogInformation($"Testing connection to: {testUrl}");

                // Log network capabilities for debugging
                Logger?.LogDebug($"Running on Xbox: {AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox"}");
                Logger?.LogDebug($"AllowUntrustedCertificates: {AllowUntrustedCertificates}");

                // Use factory-created client - certificate handling is configured in App.xaml.cs based on preferences
                var httpClient = _httpClientFactory.CreateClient("JellyfinClient");
                var response = await httpClient.GetAsync(testUrl, CancellationToken.None);

                Logger?.LogInformation(
                    $"Connection test response: StatusCode={response.StatusCode}, IsSuccess={response.IsSuccessStatusCode}");

                if (!response.IsSuccessStatusCode)
                {
                    Logger?.LogWarning(
                        $"Connection test failed. Status: {response.StatusCode}, Reason: {response.ReasonPhrase}");
                }

                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException httpEx)
            {
                Logger?.LogError($"HTTP Request Exception: {httpEx.Message}");
                if (httpEx.InnerException != null)
                {
                    Logger?.LogError($"Inner Exception: {httpEx.InnerException.Message}");
                }

                return await ErrorHandler.HandleErrorAsync(httpEx, context, false, false);
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, false, false);
            }
        }


        private void ShowLoading(bool show)
        {
            IsLoadingOverlayVisible = show;
            IsLoadingRingActive = show;
        }

        private async Task ShowErrorAsync(string message, string title)
        {
            ShowLoading(false);
            await _dialogService.ShowMessageAsync(message, title);
        }
    }
}
