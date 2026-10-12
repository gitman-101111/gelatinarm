using System;
using System.Net.Http;
using System.Text.Json;
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

        // What the last successful connection test learned about the server
        private string _connectedServerId;
        private string _connectedServerName;

        // The address answered, but not as a Jellyfin server: a different message from "unreachable"
        private bool _answeredWithoutJellyfin;

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
            // A known server (a rejected token, a profile with none, the active user's when adding
            // a user) is offered again: Connect goes straight on to sign-in, and the address can
            // still be changed first
            if (string.IsNullOrEmpty(ServerUrl) && !string.IsNullOrEmpty(_authService.SignInServerUrl))
            {
                ServerUrl = _authService.SignInServerUrl;
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
                _answeredWithoutJellyfin = false;
                var isAvailable = await TestConnectionAsync(trimmedUrl);

                if (!isAvailable && !hasScheme)
                {
                    Logger.LogWarning("HTTPS connection to {TrimmedUrl} failed. Trying HTTP.", trimmedUrl);
                    trimmedUrl = "http://" + input;
                    isAvailable = await TestConnectionAsync(trimmedUrl);
                }

                if (!isAvailable)
                {
                    // Reached but not Jellyfin: network advice would mislead, so the message is
                    // about what else answers at an address
                    var (title, message) = _answeredWithoutJellyfin
                        ? ("Not a Jellyfin Server",
                            "Something answered at this address, but it is not a Jellyfin server. Please check:\n" +
                            "• Jellyfin may be served under a path - include it (e.g., https://example.com/jellyfin)\n" +
                            "• The port may belong to another service - Jellyfin's default is 8096 (8920 for HTTPS)\n" +
                            "• A reverse proxy or web host may serve a different site at this name\n" +
                            "• A public or hotel Wi-Fi sign-in page may be intercepting the connection")
                        : ("Connection Error",
                            "Could not connect to server. Please check:\n" +
                            "• The server URL is correct (try including the port, e.g., :8096)\n" +
                            "• The server is accessible from this network\n" +
                            "• If using HTTPS with a self-signed certificate, enable 'Allow Untrusted Certificates'\n" +
                            "• Try using the server's IP address instead of hostname");
                    await ShowErrorAsync(message, title);
                    return;
                }

                _authService.SetSignInServer(trimmedUrl, _connectedServerId, _connectedServerName);
                Logger.LogInformation("Successfully connected to server {ServerName}: {TrimmedUrl}", _connectedServerName, trimmedUrl);

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
                    return false;
                }

                return ReadServerIdentity(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
            catch (Exception ex)
            {
                // Logged with its inner exception; the caller shows one message for every failure
                return await ErrorHandler.HandleErrorAsync(ex, context, defaultValue: false);
            }
        }

        /// <summary>
        ///     The public system info names the server and gives its id; the saved profile keeps
        ///     both so the picker can tell servers apart. A 200 alone proves nothing: a web host
        ///     under the wrong path, another service on the port or a Wi-Fi sign-in page all
        ///     answer 200 with HTML, and signing in there fails in confusing ways. Only a JSON
        ///     object carrying a server id counts as Jellyfin.
        /// </summary>
        private bool ReadServerIdentity(string json)
        {
            _connectedServerId = null;
            _connectedServerName = null;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("Id", out var id) || id.ValueKind != JsonValueKind.String ||
                    string.IsNullOrEmpty(id.GetString()))
                {
                    return NotJellyfin("the body has no server id");
                }

                _connectedServerId = id.GetString();
                if (root.TryGetProperty("ServerName", out var name) && name.ValueKind == JsonValueKind.String)
                {
                    _connectedServerName = name.GetString();
                }

                return true;
            }
            catch (JsonException)
            {
                // Expected for an HTML page; the verdict is the message, not the parse error
                return NotJellyfin("the body is not JSON");
            }
        }

        private bool NotJellyfin(string reason)
        {
            Logger.LogWarning("The address answered 200 but not as a Jellyfin server: {Reason}", reason);
            _answeredWithoutJellyfin = true;
            return false;
        }

        private async Task ShowErrorAsync(string message, string title)
        {
            IsConnecting = false;
            await _dialogService.ShowMessageAsync(title, message);
        }
    }
}
