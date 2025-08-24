using System;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Jellyfin.Sdk.Generated.QuickConnect.Connect;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;
using Windows.Security.Credentials;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     SDK-based implementation of the authentication service
    /// </summary>
    public class AuthenticationService : BaseService, IAuthenticationService
    {
        private readonly string RESOURCE_NAME = BrandingConstants.APP_NAME;
        private readonly ICacheManagerService _cacheManagerService;
        private readonly IUnifiedDeviceService _deviceInfoService;
        private readonly IPreferencesService _preferencesService;
        private readonly JellyfinSdkSettings _sdkSettings;
        private readonly JellyfinApiClient _apiClient;

        public AuthenticationService(
            ILogger<AuthenticationService> logger,
            IPreferencesService preferencesService,
            IUnifiedDeviceService deviceInfoService,
            ICacheManagerService cacheManagerService,
            JellyfinSdkSettings sdkSettings,
            JellyfinApiClient apiClient) : base(logger)
        {
#if DEBUG
            Logger?.LogDebug("AuthenticationService: Constructor starting");
#endif
            _preferencesService = preferencesService;
            _deviceInfoService = deviceInfoService;
            _cacheManagerService = cacheManagerService;
            _sdkSettings = sdkSettings;
            _apiClient = apiClient;

            // Load stored credentials on initialization
            LoadStoredCredentials();

#if DEBUG
            Logger?.LogDebug("AuthenticationService: Constructor completed");
#endif
        }

        public string ServerUrl { get; private set; }

        public string AccessToken { get; private set; }

        public string UserId { get; private set; }

        public string Username { get; private set; }

        public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken);

        public async Task<bool> AuthenticateAsync(string username, string password,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ServerUrl))
            {
                Logger.LogError("Server URL not set");
                return false;
            }

            // Check network connectivity before attempting authentication
            if (!await NetworkHelper.CheckNetworkAsync(ErrorHandler, Logger))
            {
                return false;
            }

            var context = CreateErrorContext("Authenticate", ErrorCategory.Authentication);
            try
            {
                Logger.LogInformation($"Attempting authentication for user '{username}' at server: {ServerUrl}");

                // Update SDK settings with the new server URL
                UpdateSdkSettings();

                var authRequest = new AuthenticateUserByName
                {
                    Username = username,
                    Pw = password ?? string.Empty // Ensure password is never null
                };

                Logger.LogDebug($"Sending authentication request to: {ServerUrl}/Users/AuthenticateByName");

                var authResponse = await RetryAsync(
                    async () => await _apiClient.Users.AuthenticateByName
                        .PostAsync(authRequest, null, cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                if (authResponse != null && !string.IsNullOrEmpty(authResponse.AccessToken))
                {
                    AccessToken = authResponse.AccessToken;
                    UserId = authResponse.User?.Id?.ToString();
                    Username = username;

                    // Update SDK settings with authentication token
                    UpdateSdkSettings();

                    // Store credentials
                    StoreCredentials();

                    Logger.LogInformation($"Authentication successful for user: {username}");
                    return true;
                }

                Logger.LogError("Authentication failed - no access token received");
                return false;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, false,
                    false); // LoginViewModel handles user messaging
            }
        }

        public async Task<bool> AuthenticateWithQuickConnectAsync(string secret,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(ServerUrl))
            {
                Logger.LogError("Server URL not set");
                return false;
            }

            var context = CreateErrorContext("QuickConnectAuthenticate", ErrorCategory.Authentication);
            try
            {
                // Update SDK settings with the new server URL
                UpdateSdkSettings();

                var authRequest = new QuickConnectDto { Secret = secret };

                var authResponse = await RetryAsync(
                    async () => await _apiClient.Users.AuthenticateWithQuickConnect
                        .PostAsync(authRequest, null, cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                if (authResponse != null && !string.IsNullOrEmpty(authResponse.AccessToken))
                {
                    AccessToken = authResponse.AccessToken;
                    UserId = authResponse.User?.Id?.ToString();
                    Username = authResponse.User?.Name;

                    // Update SDK settings with authentication token
                    UpdateSdkSettings();

                    // Store credentials
                    StoreCredentials();

                    Logger.LogInformation($"Quick Connect authentication successful for user: {Username}");
                    return true;
                }

                Logger.LogError("Quick Connect authentication failed - no access token received");
                return false;
            }
            catch (Exception ex)
            {
                return
                    await ErrorHandler.HandleErrorAsync(ex, context, false,
                        false); // QuickConnectInstructionsViewModel handles user messaging
            }
        }

        public void SetServerUrl(string serverUrl)
        {
            Logger.LogInformation($"Setting server URL to: {serverUrl}");

            // If switching to a different server, clear all caches
            if (!string.IsNullOrEmpty(ServerUrl) && ServerUrl != serverUrl)
            {
                Logger.LogInformation($"Switching servers from {ServerUrl} to {serverUrl} - clearing all caches");

                // Clear all caches through the central cache manager
                _cacheManagerService?.Clear();
                Logger.LogInformation("Cleared all caches when switching servers");

                // Also clear MainViewModel collections since it's a singleton
                var mainViewModel = App.Current.Services.GetService(typeof(MainViewModel)) as MainViewModel;
                if (mainViewModel != null)
                {
                    mainViewModel.ClearCache();
                    Logger.LogInformation("Cleared MainViewModel collections when switching servers");
                }
            }

            ServerUrl = serverUrl;
            _preferencesService.SetValue(PreferenceConstants.ServerUrl, serverUrl);

            try
            {
                UpdateSdkSettings();
                Logger.LogInformation($"Server URL set successfully to: {serverUrl}");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to create API client for server URL: {serverUrl}");
                throw;
            }
        }

        public async Task<QuickConnectResult> InitiateQuickConnectAsync(CancellationToken cancellationToken = default)
        {
            // Check network connectivity before attempting Quick Connect
            if (!await NetworkHelper.CheckNetworkAsync(ErrorHandler, Logger))
            {
                return null;
            }
            Logger.LogInformation($"Initiating Quick Connect with server URL: {ServerUrl}");

            // Ensure we have an API client with the current server URL
            if (string.IsNullOrEmpty(ServerUrl))
            {
                Logger.LogError("Cannot initiate Quick Connect: Server URL is not set");
                throw new InvalidOperationException("Server URL must be set before initiating Quick Connect");
            }

            var context = CreateErrorContext("InitiateQuickConnect", ErrorCategory.Authentication);
            try
            {
                // Update SDK settings with the new server URL
                UpdateSdkSettings();

                Logger.LogDebug($"Sending Quick Connect initiate request to: {ServerUrl}/QuickConnect/Initiate");

                var response = await RetryAsync(
                    async () => await _apiClient.QuickConnect.Initiate.PostAsync(null, cancellationToken)
                        .ConfigureAwait(false),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                if (response != null)
                {
                    Logger.LogInformation($"Quick Connect initiated successfully. Code: {response.Code}");
                    return new QuickConnectResult { Code = response.Code, Secret = response.Secret };
                }

                Logger.LogWarning("Quick Connect response was null");
                return null;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<QuickConnectResult>(ex, context, null,
                    true); // Show user-friendly message for Quick Connect errors
            }
        }

        public async Task<bool> CheckQuickConnectStatusAsync(string secret,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var requestConfig =
                    new Action<RequestConfiguration<
                        ConnectRequestBuilder.ConnectRequestBuilderGetQueryParameters>>(config =>
                    {
                        config.QueryParameters.Secret = secret;
                    });

                var response = await RetryAsync(
                    async () => await _apiClient.QuickConnect.Connect.GetAsync(requestConfig, cancellationToken)
                        .ConfigureAwait(false),
                    Logger,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                if (response?.Authenticated == true)
                {
                    Logger.LogInformation("Quick Connect authenticated");

                    // The response indicates authentication is complete, but we need to 
                    // authenticate with the secret to get the access token
                    var authSuccess = await AuthenticateWithQuickConnectAsync(secret, cancellationToken)
                        .ConfigureAwait(false);
                    if (authSuccess)
                    {
                        Logger.LogInformation("Quick Connect authentication completed successfully");
                    }
                    else
                    {
                        Logger.LogError("Quick Connect indicated authenticated but token retrieval failed");
                    }

                    // Get user info BEFORE storing credentials
                    try
                    {
                        var user = await RetryAsync(
                            async () => await _apiClient.Users.Me.GetAsync(null, cancellationToken)
                                .ConfigureAwait(false),
                            Logger,
                            cancellationToken: cancellationToken
                        ).ConfigureAwait(false);
                        if (user != null)
                        {
                            UserId = user.Id?.ToString();
                            Username = user.Name;
                            Logger.LogInformation($"Quick Connect completed for user: {user.Name} (ID: {UserId})");

                            // Now store all credentials including user info
                            StoreCredentials();
                        }
                        else
                        {
                            Logger.LogError("Failed to get user info after Quick Connect - null response");
                            return false;
                        }
                    }
                    catch (Exception userEx)
                    {
                        Logger.LogError(userEx, "Failed to get user info after Quick Connect");
                        return false;
                    }

                    return authSuccess;
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to check Quick Connect status");
                return false;
            }
        }

        public async Task<bool> ValidateTokenAsync(CancellationToken cancellationToken = default)
        {
            // Check network connectivity before attempting validation
            if (!await NetworkHelper.CheckNetworkAsync(ErrorHandler, Logger))
            {
                return false;
            }

            try
            {
                if (string.IsNullOrEmpty(AccessToken))
                {
                    return false;
                }

                // Try to get current user info to validate token
                var user = await RetryAsync(
                    async () => await _apiClient.Users.Me.GetAsync(null, cancellationToken).ConfigureAwait(false),
                    Logger,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                return user != null;
            }
            catch (ApiException apiEx) when (apiEx.ResponseStatusCode == 401)
            {
                Logger.LogWarning("Token validation failed with 401 - clearing invalid credentials");
                // Clear invalid credentials
                ClearInvalidCredentials();
                return false;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Token validation failed");
                return false;
            }
        }

        public string GetAuthorizationHeader()
        {
            if (string.IsNullOrEmpty(AccessToken))
            {
                return null;
            }

            return $"MediaBrowser Token=\"{AccessToken}\"";
        }

        public Task LogoutAsync(CancellationToken cancellationToken = default)
        {
            // Async version of Logout
            Logout();
            return Task.CompletedTask;
        }

        public async Task<bool> RestoreLastSessionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                // Try to validate existing token
                if (!string.IsNullOrEmpty(AccessToken))
                {
                    return await ValidateTokenAsync(cancellationToken).ConfigureAwait(false);
                }

                return false;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to restore last session");
                return false;
            }
        }

        public void CancelQuickConnect()
        {
            // Cancel any ongoing Quick Connect process
            Logger.LogInformation("Quick Connect cancelled");
        }


        private void UpdateSdkSettings()
        {
            // Update the SDK settings with the current server URL and access token
            if (!string.IsNullOrEmpty(ServerUrl))
            {
                Logger.LogDebug($"Updating SDK settings - Server: {ServerUrl}, Has Token: {!string.IsNullOrEmpty(AccessToken)}");
                _sdkSettings.SetServerUrl(ServerUrl);
                _sdkSettings.SetAccessToken(AccessToken);
            }
        }

        private void LoadStoredCredentials()
        {
            try
            {
                ServerUrl = _preferencesService.GetValue<string>(PreferenceConstants.ServerUrl);
                UserId = _preferencesService.GetValue<string>(PreferenceConstants.UserId);
                Username = _preferencesService.GetValue<string>(PreferenceConstants.UserName);

                // Try to load access token from secure storage
                if (!string.IsNullOrEmpty(Username))
                {
                    try
                    {
                        var vault = new PasswordVault();
                        var credential = vault.Retrieve(RESOURCE_NAME, Username);
                        credential.RetrievePassword();
                        AccessToken = credential.Password;

                        // Update SDK settings with stored credentials
                        if (!string.IsNullOrEmpty(ServerUrl) && !string.IsNullOrEmpty(AccessToken))
                        {
                            UpdateSdkSettings();
                            Logger.LogInformation($"Loaded stored credentials successfully for user: {Username}");
                        }
                    }
                    catch (Exception vaultEx)
                    {
                        // No credentials in vault - this is normal for first launch or after logout
                        Logger.LogDebug(vaultEx, $"No credentials found in PasswordVault for user: {Username}");
                    }
                }
                else
                {
                    Logger.LogDebug("No username found in preferences - no credentials to load");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error loading stored credentials");
            }
        }

        public void Logout()
        {
            try
            {
                // Clear stored credentials
                var vault = new PasswordVault();
                var credentials = vault.FindAllByResource(RESOURCE_NAME);
                foreach (var credential in credentials)
                {
                    vault.Remove(credential);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error clearing stored credentials");
            }

            // Clear all preferences
            _preferencesService.RemoveValue(PreferenceConstants.ServerUrl);
            _preferencesService.RemoveValue(PreferenceConstants.UserId);
            _preferencesService.RemoveValue(PreferenceConstants.UserName);
            _preferencesService.RemoveValue(PreferenceConstants.AccessToken);

            // Clear in-memory values BEFORE updating SDK settings
            var oldServerUrl = ServerUrl;
            ServerUrl = null;
            AccessToken = null;
            UserId = null;
            Username = null;

            // Clear SDK settings - use invalid URL to prevent localhost default
            // Using "about:blank" ensures no network requests can succeed
            if (_sdkSettings != null)
            {
                Logger.LogInformation("Clearing SDK settings on logout");
                _sdkSettings.SetServerUrl("about:blank");
                _sdkSettings.SetAccessToken(null);
            }

            // Clear all cached data
            try
            {
                _cacheManagerService?.Clear();
                Logger.LogInformation("Cache cleared successfully during logout");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error clearing cache during logout");
            }

            Logger.LogInformation($"User logged out successfully. Previous server: {oldServerUrl ?? "none"}");
        }

        private void StoreCredentials()
        {
            try
            {
                // Store non-sensitive data in preferences
                _preferencesService.SetValue(PreferenceConstants.ServerUrl, ServerUrl);
                _preferencesService.SetValue(PreferenceConstants.UserId, UserId);
                _preferencesService.SetValue(PreferenceConstants.UserName, Username);
                // Access token is stored securely in PasswordVault only

                // Update the singleton SDK settings so all services use the new token
                if (_sdkSettings != null)
                {
                    Logger.LogInformation($"Updating SDK settings with new token for server: {ServerUrl}");
                    _sdkSettings.SetServerUrl(ServerUrl);
                    _sdkSettings.SetAccessToken(AccessToken);
                }

                // Store access token securely in PasswordVault
                try
                {
                    var vault = new PasswordVault();
                    // Remove any existing credential for this user first
                    try
                    {
                        var existingCred = vault.Retrieve(RESOURCE_NAME, Username);
                        vault.Remove(existingCred);
                    }
                    catch
                    {
                        // No existing credential, that's fine
                    }

                    // Add the new credential
                    var credential = new PasswordCredential(RESOURCE_NAME, Username, AccessToken);
                    vault.Add(credential);
                    Logger.LogInformation("Access token stored securely in PasswordVault");
                }
                catch (Exception vaultEx)
                {
                    Logger.LogError(vaultEx, "Failed to store access token in PasswordVault");
                }

                Logger.LogInformation("Credentials stored successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to store credentials");
            }
        }


        private void ClearInvalidCredentials()
        {
            try
            {
                // Clear stored credentials from vault
                var vault = new PasswordVault();
                var credentials = vault.FindAllByResource(RESOURCE_NAME);
                foreach (var credential in credentials)
                {
                    vault.Remove(credential);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Error clearing stored credentials from vault");
            }
            _preferencesService.RemoveValue(PreferenceConstants.AccessToken);

            // Clear in-memory values
            AccessToken = null;

            // Clear SDK settings
            if (_sdkSettings != null)
            {
                _sdkSettings.SetAccessToken(null);
            }

            Logger.LogInformation("Invalid credentials cleared");
        }

        // Events
#pragma warning disable CS0067 // The event is never used (required by interface)
        public event EventHandler<QuickConnectResult> QuickConnectCompleted;
        public event EventHandler<string> QuickConnectError;
        public event EventHandler<QuickConnectState> QuickConnectStatusChanged;
#pragma warning restore CS0067
    }
}
