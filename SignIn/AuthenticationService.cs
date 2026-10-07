using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Security.Credentials;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.Shared.Server;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;

namespace Gelatinarm.SignIn
{
    public interface IAuthenticationService
    {
        string ServerUrl { get; }
        string AccessToken { get; }
        string UserId { get; }
        bool IsAuthenticated { get; }

        /// <summary>
        ///     The signed-in user or the server has changed (sign-in as someone else, profile switch,
        ///     server switch, sign-out). Whatever holds a user's data outside this service empties it.
        /// </summary>
        event EventHandler UserChanged;

        Task<bool> AuthenticateAsync(string username, string password, CancellationToken cancellationToken);
        void SetServerUrl(string serverUrl);
        void Logout();
        Task<QuickConnectResult> InitiateQuickConnectAsync(CancellationToken cancellationToken);
        Task<bool> CheckQuickConnectStatusAsync(string secret, CancellationToken cancellationToken);
        Task<bool> RestoreLastSessionAsync();

        /// <summary>
        ///     Users who have signed in on this device, alphabetical by username.
        /// </summary>
        IReadOnlyList<UserProfile> GetSavedProfiles();

        /// <summary>
        ///     Two or more users have signed in on this device: startup shows the profile picker,
        ///     and the home and settings pages offer Switch User.
        /// </summary>
        bool HasMultipleSavedProfiles { get; }

        /// <summary>
        ///     Switches the session to a saved profile using its stored token.
        ///     Returns false when no valid token is stored (caller routes to login).
        /// </summary>
        Task<bool> SwitchToProfileAsync(string userId, CancellationToken cancellationToken);
    }

    public class AuthenticationService : BaseService, IAuthenticationService
    {
        private const string ResourceName = BrandingConstants.AppName;
        private readonly ICacheManagerService _cacheManagerService;
        private readonly IPreferencesService _preferencesService;
        private readonly JellyfinSdkSettings _sdkSettings;
        private readonly JellyfinApiClient _apiClient;
        private readonly object _profilesLock = new();
        private List<UserProfile> _savedProfiles;

        public AuthenticationService(
            ILogger<AuthenticationService> logger,
            IPreferencesService preferencesService,
            ICacheManagerService cacheManagerService,
            JellyfinSdkSettings sdkSettings,
            JellyfinApiClient apiClient) : base(logger)
        {
            _preferencesService = preferencesService;
            _cacheManagerService = cacheManagerService;
            _sdkSettings = sdkSettings;
            _apiClient = apiClient;

            LoadStoredCredentials();
        }

        public string ServerUrl { get; private set; }

        public string AccessToken { get; private set; }

        public string UserId { get; private set; }

        // Kept for the saved profile and the next launch's preferences; nothing outside reads it
        private string Username { get; set; }

        public bool IsAuthenticated => !string.IsNullOrEmpty(AccessToken);

        public event EventHandler UserChanged;

        public async Task<bool> AuthenticateAsync(string username, string password,
            CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(ServerUrl) &&
                !await NetworkHelper.CheckNetworkAsync(ErrorHandler).ConfigureAwait(false))
            {
                return false;
            }

            Logger.LogInformation("Attempting authentication for user '{Username}' at server: {ServerUrl}", username, ServerUrl);
            var authRequest = new AuthenticateUserByName
            {
                Username = username,
                Pw = password ?? string.Empty
            };

            return await SignInAsync("Authenticate", username,
                ct => _apiClient.Users.AuthenticateByName.PostAsync(authRequest, null, ct),
                cancellationToken).ConfigureAwait(false);
        }

        private Task<bool> AuthenticateWithQuickConnectAsync(string secret,
            CancellationToken cancellationToken)
        {
            var authRequest = new QuickConnectDto { Secret = secret };
            return SignInAsync("QuickConnectAuthenticate", null,
                ct => _apiClient.Users.AuthenticateWithQuickConnect.PostAsync(authRequest, null, ct),
                cancellationToken);
        }

        /// <summary>
        ///     What every sign-in does once the request is known: send it (with retries), adopt the
        ///     returned token and user, save the profile, and clear per-user caches on a user change.
        ///     <paramref name="username" /> is the typed name, or null to take it from the response.
        /// </summary>
        private async Task<bool> SignInAsync(string operation, string username,
            Func<CancellationToken, Task<AuthenticationResult>> request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(ServerUrl))
            {
                Logger.LogError("Server URL not set");
                return false;
            }

            var context = CreateErrorContext(operation, ErrorCategory.Authentication);
            var previousUserId = UserId;
            try
            {
                UpdateSdkSettings();

                var authResponse = await RetryAsync(() => request(cancellationToken),
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                if (authResponse != null && !string.IsNullOrEmpty(authResponse.AccessToken))
                {
                    AccessToken = authResponse.AccessToken;
                    UserId = authResponse.User?.Id?.ToString();
                    Username = username ?? authResponse.User?.Name;

                    UpdateSdkSettings();

                    StoreCredentials(authResponse.User);
                    ClearCachesIfUserChanged(previousUserId);

                    Logger.LogInformation("{Operation} successful for user: {Username}", operation, Username);
                    return true;
                }

                Logger.LogError("{Operation} failed - no access token received", operation);
                return false;
            }
            catch (Exception ex)
            {
                // The calling view model shows the message
                return await ErrorHandler.HandleErrorAsync(ex, context, defaultValue: false);
            }
        }

        public void SetServerUrl(string serverUrl)
        {
            Logger.LogDebug("Setting server URL to: {ServerUrl}", serverUrl);

            if (!string.IsNullOrEmpty(ServerUrl) && ServerUrl != serverUrl)
            {
                Logger.LogInformation("Switching servers from {ServerUrl} to {ServerUrl2}", ServerUrl, serverUrl);
                ClearUserCaches("when switching servers");
            }

            ServerUrl = serverUrl;
            _preferencesService.SetValue(PreferenceConstants.ServerUrl, serverUrl);

            UpdateSdkSettings();
        }

        public async Task<QuickConnectResult> InitiateQuickConnectAsync(CancellationToken cancellationToken)
        {
            if (!await NetworkHelper.CheckNetworkAsync(ErrorHandler).ConfigureAwait(false))
            {
                return null;
            }

            Logger.LogInformation("Initiating Quick Connect with server URL: {ServerUrl}", ServerUrl);

            if (string.IsNullOrEmpty(ServerUrl))
            {
                throw new InvalidOperationException("Server URL must be set before initiating Quick Connect");
            }

            var context = CreateErrorContext("InitiateQuickConnect", ErrorCategory.Authentication);
            try
            {
                UpdateSdkSettings();

                var response = await RetryAsync(
                    () => _apiClient.QuickConnect.Initiate.PostAsync(null, cancellationToken),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                if (response != null)
                {
                    Logger.LogInformation("Quick Connect initiated successfully. Code: {ResponseCode}", response.Code);
                    return new QuickConnectResult { Code = response.Code, Secret = response.Secret };
                }

                Logger.LogWarning("Quick Connect response was null");
                return null;
            }
            catch (Exception ex)
            {
                // The login page explains a failed start inline (Quick Connect off, or an old server)
                return await ErrorHandler.HandleErrorAsync<QuickConnectResult>(ex, context, null);
            }
        }

        // A failure reaches the poll, which reports it and keeps polling
        public async Task<bool> CheckQuickConnectStatusAsync(string secret,
            CancellationToken cancellationToken)
        {
            var response = await RetryAsync(
                () => _apiClient.QuickConnect.Connect.GetAsync(config => config.QueryParameters.Secret = secret, cancellationToken),
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);

            if (response?.Authenticated == true)
            {
                Logger.LogInformation("Quick Connect authenticated");

                // The response indicates authentication is complete, but we need to
                // authenticate with the secret to get the access token
                var authSuccess = await AuthenticateWithQuickConnectAsync(secret, cancellationToken)
                    .ConfigureAwait(false);
                if (!authSuccess)
                {
                    Logger.LogError("Quick Connect indicated authenticated but token retrieval failed");
                }

                return authSuccess;
            }

            return false;
        }

        private async Task<bool> ValidateTokenAsync(CancellationToken cancellationToken = default)
        {
            if (!await NetworkHelper.CheckNetworkAsync(ErrorHandler).ConfigureAwait(false))
            {
                return false;
            }

            try
            {
                var user = await RetryAsync(
                    () => _apiClient.Users.Me.GetAsync(null, cancellationToken),
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                return user != null;
            }
            catch (ApiException apiEx) when (apiEx.ResponseStatusCode == 401)
            {
                Logger.LogWarning(apiEx, "Token validation failed with 401 - clearing invalid credentials");
                ClearInvalidCredentials();
                return false;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex,
                    CreateErrorContext("ValidateToken", ErrorCategory.Authentication, ErrorSeverity.Warning), defaultValue: false);
            }
        }

        public IReadOnlyList<UserProfile> GetSavedProfiles()
        {
            lock (_profilesLock)
            {
                return GetSavedProfilesUnsafe()
                    .OrderBy(p => p.Username, StringComparer.CurrentCultureIgnoreCase).ToList();
            }
        }

        public bool HasMultipleSavedProfiles => GetSavedProfiles().Count >= 2;

        public async Task<bool> SwitchToProfileAsync(string userId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(userId))
            {
                Logger.LogWarning("SwitchToProfileAsync called with empty userId");
                return false;
            }

            if (userId == UserId && IsAuthenticated)
            {
                Logger.LogDebug("Already signed in as user {UserId} - nothing to switch", userId);
                return true;
            }

            UserProfile profile;
            lock (_profilesLock)
            {
                profile = GetSavedProfilesUnsafe().FirstOrDefault(p => p.UserId == userId);
            }

            if (profile == null)
            {
                Logger.LogWarning("No saved profile found for user {UserId}", userId);
                return false;
            }

            var token = TryGetTokenFromVault(userId);
            if (string.IsNullOrEmpty(token))
            {
                Logger.LogInformation("No stored token for user {ProfileUsername} - login required", profile.Username);
                return false;
            }

            Logger.LogInformation("Switching to profile: {ProfileUsername}", profile.Username);

            ServerUrl = profile.ServerUrl;
            AccessToken = token;
            UserId = profile.UserId;
            Username = profile.Username;
            UpdateSdkSettings();
            PersistActiveUser();

            ClearUserCaches("when switching profiles");

            // Only a confirmed 401 clears AccessToken (via ClearInvalidCredentials); a transient
            // network failure leaves it intact
            await ValidateTokenAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(AccessToken))
            {
                Logger.LogWarning("Stored token for {ProfileUsername} was rejected - login required", profile.Username);
                return false;
            }

            return true;
        }

        public Task<bool> RestoreLastSessionAsync()
        {
            // ValidateTokenAsync handles its own failures: false, and a 401 clears the token
            return string.IsNullOrEmpty(AccessToken)
                ? Task.FromResult(false)
                : ValidateTokenAsync();
        }

        private void UpdateSdkSettings()
        {
            if (!string.IsNullOrEmpty(ServerUrl))
            {
                Logger.LogDebug(
                    "Updating SDK settings - Server: {ServerUrl}, Has Token: {HasToken}", ServerUrl, !string.IsNullOrEmpty(AccessToken));
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

                if (!string.IsNullOrEmpty(UserId))
                {
                    _preferencesService.SetActiveUserScope(UserId);

                    AccessToken = TryGetTokenFromVault(UserId);
                    if (string.IsNullOrEmpty(AccessToken) && !string.IsNullOrEmpty(Username))
                    {
                        // One-time upgrade path: earlier versions keyed the vault by
                        // username - move the entry to the user-ID key
                        AccessToken = TryGetTokenFromVault(Username);
                        if (!string.IsNullOrEmpty(AccessToken))
                        {
                            RemoveVaultEntry(Username);
                            SaveTokenToVault();
                            Logger.LogInformation("Migrated stored token to user-ID key for: {Username}", Username);
                        }
                    }

                    if (!string.IsNullOrEmpty(ServerUrl) && !string.IsNullOrEmpty(AccessToken))
                    {
                        UpdateSdkSettings();

                        // Self-heal the saved-profiles list: installs from before
                        // multi-user have credentials but no profile entry
                        UpsertSavedProfile(null);

                        Logger.LogInformation("Loaded stored credentials successfully for user: {Username}", Username);
                    }
                }
                else
                {
                    Logger.LogDebug("No user ID found in preferences - no credentials to load");
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("LoadStoredCredentials", ErrorCategory.Authentication, ErrorSeverity.Warning));
            }
        }

        /// <summary>
        ///     Vault entries are keyed by user ID so profiles never collide; null when none is stored
        /// </summary>
        private string TryGetTokenFromVault(string userId)
        {
            try
            {
                var vault = new PasswordVault();
                var credential = vault.Retrieve(ResourceName, userId);
                credential.RetrievePassword();
                return credential.Password;
            }
            catch (Exception ex)
            {
                // No credential stored - normal for first launch, after sign-out, or a revoked token
                Logger.LogDebug(ex, "No credentials found in PasswordVault for user: {UserId}", userId);
                return null;
            }
        }

        /// <summary>
        ///     Clears cached data when authentication lands on a different user than the
        ///     previously active one (adding a user without an intervening logout).
        /// </summary>
        private void ClearCachesIfUserChanged(string previousUserId)
        {
            if (!string.IsNullOrEmpty(previousUserId) && previousUserId != UserId)
            {
                ClearUserCaches("after signing in as a different user");
            }
        }

        /// <summary>
        ///     Drops everything cached for the previous user or server: the API cache here, and
        ///     through UserChanged whatever else keeps a user's data (the home screen keeps its
        ///     sections between visits). Every change of user or server passes through this
        ///     service, so this is the one place it happens.
        /// </summary>
        private void ClearUserCaches(string reason)
        {
            _cacheManagerService.Clear();
            UserChanged?.Invoke(this, EventArgs.Empty);
            Logger.LogDebug("Cleared cached data {Reason}", reason);
        }

        /// <summary>
        ///     The token goes to the PasswordVault only, never to preferences
        /// </summary>
        private void SaveTokenToVault()
        {
            try
            {
                RemoveVaultEntry(UserId);
                new PasswordVault().Add(new PasswordCredential(ResourceName, UserId, AccessToken));
                Logger.LogDebug("Access token stored securely in PasswordVault");
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("SaveTokenToVault", ErrorCategory.Authentication));
            }
        }

        private void RemoveVaultEntry(string userId)
        {
            try
            {
                var vault = new PasswordVault();
                var credential = vault.Retrieve(ResourceName, userId);
                vault.Remove(credential);
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "No vault entry to remove for user: {UserId}", userId);
            }
        }

        /// <summary>
        ///     Callers must hold _profilesLock.
        /// </summary>
        private List<UserProfile> GetSavedProfilesUnsafe()
        {
            _savedProfiles ??= _preferencesService.GetValue<List<UserProfile>>(PreferenceConstants.SavedProfiles)
                               ?? new List<UserProfile>();
            return _savedProfiles;
        }

        /// <summary>
        ///     Callers must hold _profilesLock.
        /// </summary>
        private void SaveProfilesUnsafe()
        {
            _preferencesService.SetValue(PreferenceConstants.SavedProfiles, _savedProfiles);
        }

        public void Logout()
        {
            // Remove only the active user's stored credentials and profile. Other saved
            // profiles on this device stay intact.
            var activeUserId = UserId;
            if (!string.IsNullOrEmpty(activeUserId))
            {
                RemoveVaultEntry(activeUserId);

                lock (_profilesLock)
                {
                    var profiles = GetSavedProfilesUnsafe();
                    profiles.RemoveAll(p => p.UserId == activeUserId);
                    SaveProfilesUnsafe();
                }

                _preferencesService.RemoveUserScopedValues(activeUserId);
            }

            _preferencesService.RemoveValue(PreferenceConstants.UserId);
            _preferencesService.RemoveValue(PreferenceConstants.UserName);
            _preferencesService.SetActiveUserScope(null);

            // Only forget the server when no other profiles remain on it
            var hasRemainingProfiles = GetSavedProfiles().Count > 0;
            if (!hasRemainingProfiles)
            {
                _preferencesService.RemoveValue(PreferenceConstants.ServerUrl);
            }

            var oldServerUrl = ServerUrl;
            if (!hasRemainingProfiles)
            {
                ServerUrl = null;
            }

            AccessToken = null;
            UserId = null;
            Username = null;

            // Clear SDK settings. When no profiles remain, use an invalid URL to prevent
            // a localhost default - "about:blank" ensures no network requests can succeed.
            // With profiles remaining, keep the server URL so the next switch is seamless.
            if (!hasRemainingProfiles)
            {
                _sdkSettings.SetServerUrl("about:blank");
            }

            _sdkSettings.SetAccessToken(null);

            ClearUserCaches("on sign-out");

            Logger.LogInformation("User logged out successfully. Previous server: {OldServerUrl}", oldServerUrl ?? "none");
        }

        /// <summary>
        ///     Makes the current user the one restored at the next launch, and scopes local
        ///     settings to them.
        /// </summary>
        private void PersistActiveUser()
        {
            _preferencesService.SetValue(PreferenceConstants.ServerUrl, ServerUrl);
            _preferencesService.SetValue(PreferenceConstants.UserId, UserId);
            _preferencesService.SetValue(PreferenceConstants.UserName, Username);
            _preferencesService.SetActiveUserScope(UserId);
        }

        private void StoreCredentials(UserDto user)
        {
            try
            {
                PersistActiveUser();

                SaveTokenToVault();
                UpsertSavedProfile(user);
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("StoreCredentials", ErrorCategory.Authentication));
            }
        }

        private void UpsertSavedProfile(UserDto user)
        {
            if (string.IsNullOrEmpty(UserId))
            {
                return;
            }

            lock (_profilesLock)
            {
                var profiles = GetSavedProfilesUnsafe();
                var profile = profiles.FirstOrDefault(p => p.UserId == UserId);
                if (profile == null)
                {
                    profile = new UserProfile { UserId = UserId };
                    profiles.Add(profile);
                }

                profile.ServerUrl = ServerUrl;
                profile.Username = Username;
                profile.PrimaryImageTag = user?.PrimaryImageTag ?? profile.PrimaryImageTag;
                SaveProfilesUnsafe();
            }
        }

        private void ClearInvalidCredentials()
        {
            // Remove only the active user's rejected token; other saved profiles keep theirs.
            // The profile entry stays in the saved list so the picker can route to login.
            if (!string.IsNullOrEmpty(UserId))
            {
                RemoveVaultEntry(UserId);
            }

            AccessToken = null;

            _sdkSettings.SetAccessToken(null);

            Logger.LogInformation("Invalid credentials cleared");
        }
    }
}
