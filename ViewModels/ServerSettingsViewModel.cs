using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.Views;
using Jellyfin.Sdk;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     Handles server-related settings including connection, authentication, and Quick Connect
    /// </summary>
    public class ServerSettingsViewModel : BaseViewModel
    {
        #region Helper Classes

        /// <summary>
        ///     Represents the result of a validation operation
        /// </summary>
        protected class ValidationResult
        {
            public bool IsValid { get; set; }
            public string ErrorMessage { get; set; }
        }

        #endregion

        private readonly JellyfinApiClient _apiClient;
        private readonly IAuthenticationService _authService;
        private readonly IDialogService _dialogService;
        private readonly INavigationService _navigationService;
        protected readonly IPreferencesService PreferencesService;

        // Settings state
        private bool _hasUnsavedChanges = false;
        private string _validationError;

        // Settings properties
        private bool _allowSelfSignedCertificates = false;
        private int _connectionTimeout = 30;

        // Server properties
        private string _serverUrl;
        private string _username;

        public ServerSettingsViewModel(
            ILogger<ServerSettingsViewModel> logger,
            JellyfinApiClient apiClient,
            IPreferencesService preferencesService,
            IAuthenticationService authService,
            INavigationService navigationService,
            IDialogService dialogService) : base(logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
            PreferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService)); SignOutCommand = new AsyncRelayCommand(SignOutAsync);
        }

        // Commands
        public IAsyncRelayCommand SignOutCommand { get; }

        #region Properties

        public bool HasUnsavedChanges
        {
            get => _hasUnsavedChanges;
            protected set => SetProperty(ref _hasUnsavedChanges, value);
        }

        public string ValidationError
        {
            get => _validationError;
            protected set => SetProperty(ref _validationError, value);
        }

        #endregion

        #region Public Methods

        /// <summary>
        ///     Initializes the settings view model by loading current settings
        /// </summary>
        public virtual async Task InitializeAsync()
        {
            // Use the standardized LoadDataAsync pattern
            await LoadDataAsync(true);
        }

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            await RunOnUIThreadAsync(() => ValidationError = null);
            await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            await RunOnUIThreadAsync(() => HasUnsavedChanges = false);
        }

        protected override async Task RefreshDataCoreAsync()
        {
            // Refresh just reloads settings
            await LoadSettingsAsync(CancellationToken.None).ConfigureAwait(false);
            await RunOnUIThreadAsync(() => HasUnsavedChanges = false);
        }

        protected override async Task ClearDataCoreAsync()
        {
            await RunOnUIThreadAsync(() =>
            {
                ValidationError = null;
                HasUnsavedChanges = false;
            });
        }

        /// <summary>
        ///     Saves the current settings
        /// </summary>
        public virtual async Task<bool> SaveSettingsAsync()
        {
            var context = CreateErrorContext("SaveSettingsAsync", ErrorCategory.Configuration);
            try
            {
                IsLoading = true;
                ValidationError = null;

                // Validate settings before saving
                var validationResult = await ValidateSettingsAsync().ConfigureAwait(false);
                if (!validationResult.IsValid)
                {
                    ValidationError = validationResult.ErrorMessage;
                    return false;
                }

                // Save settings
                await SaveSettingsInternalAsync().ConfigureAwait(false);
                HasUnsavedChanges = false;

                Logger.LogInformation("Settings saved successfully");
                return true;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
                ValidationError = "Failed to save settings. Please try again.";
                return false;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        ///     Resets settings to their default values - async version for consistency
        /// </summary>
        public virtual async Task ResetToDefaultsAsync()
        {
            var context = CreateErrorContext("ResetToDefaultsAsync", ErrorCategory.Configuration);
            try
            {
                IsLoading = true;
                ValidationError = null;

                await ResetToDefaultsInternalAsync().ConfigureAwait(false);
                HasUnsavedChanges = true;

                Logger.LogInformation("Settings reset to defaults");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
                ValidationError = "Failed to reset settings. Please try again.";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        ///     Synchronous reset method called by parent SettingsViewModel
        /// </summary>
        public void ResetToDefaults()
        {
            FireAndForget(() => ResetToDefaultsAsync());
        }

        /// <summary>
        ///     Refresh settings
        /// </summary>
        public override async Task RefreshAsync()
        {
            await base.RefreshAsync();
        }

        #endregion

        #region Protected Methods

        /// <summary>
        ///     Loads the settings from storage
        /// </summary>
        protected async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
        {
            // Load server settings
            ServerUrl = PreferencesService.GetValue<string>("ServerUrl");
            Username = PreferencesService.GetValue<string>("Username");

            if (string.IsNullOrEmpty(Username))
            {
                var userId = PreferencesService.GetValue<string>("UserId");
                if (!string.IsNullOrEmpty(userId))
                {
                    try
                    {
                        var user = await _apiClient.Users[new Guid(userId)].GetAsync(null, cancellationToken)
                            .ConfigureAwait(false);
                        await RunOnUIThreadAsync(() => { Username = user?.Name ?? "Unknown User"; });
                    }
                    catch (Exception httpEx)
                    {
                        Logger.LogWarning(httpEx, "Failed to get username via HTTP, using fallback.");
                        await RunOnUIThreadAsync(() => { Username = "Quick Connect User"; });
                    }
                }
                else
                {
                    Username = "Not logged in";
                }
            }
            var appPrefs = await PreferencesService.GetAppPreferencesAsync().ConfigureAwait(false);

            await RunOnUIThreadAsync(() =>
            {
                _connectionTimeout = appPrefs.ConnectionTimeout;
                _allowSelfSignedCertificates = appPrefs.IgnoreCertificateErrors;

                // Notify all properties changed
                OnPropertyChanged(nameof(ConnectionTimeout));
                OnPropertyChanged(nameof(AllowSelfSignedCertificates));
            });
        }

        /// <summary>
        ///     Saves the settings to storage
        /// </summary>
        protected async Task SaveSettingsInternalAsync()
        {
            // All settings are saved individually on property change
            // This method could be used to save all settings at once if needed
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Resets settings to their default values
        /// </summary>
        protected async Task ResetToDefaultsInternalAsync()
        {
            ConnectionTimeout = SystemConstants.DEFAULT_TIMEOUT_SECONDS;
            AllowSelfSignedCertificates = true;
            await Task.CompletedTask;
        }

        /// <summary>
        ///     Validates the current settings
        /// </summary>
        protected Task<ValidationResult> ValidateSettingsAsync()
        {
            // Default implementation - no validation
            return Task.FromResult(new ValidationResult { IsValid = true });
        }

        /// <summary>
        ///     Marks that settings have been changed
        /// </summary>
        protected void MarkAsChanged()
        {
            HasUnsavedChanges = true;
        }

        /// <summary>
        ///     Helper method to update a setting value and mark as changed
        /// </summary>
        protected bool SetSettingProperty<T>(ref T storage, T value, string propertyName = null)
        {
            if (SetProperty(ref storage, value, propertyName))
            {
                MarkAsChanged();
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Helper method to safely update app preferences
        /// </summary>
        protected async Task UpdateAppPreferenceAsync(Action<AppPreferences> updateAction, string settingName)
        {
            try
            {
                var appPrefs = await PreferencesService.GetAppPreferencesAsync().ConfigureAwait(false);
                updateAction(appPrefs);
                await PreferencesService.UpdateAppPreferencesAsync(appPrefs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to update {settingName} setting");
                throw;
            }
        }

        #endregion

        private async Task SignOutAsync()
        {
            try
            {
                Logger.LogInformation("Starting logout process");

                // Clear all cached data through the central cache manager
                var cacheManager = GetService<ICacheManagerService>();
                cacheManager?.Clear();
                Logger.LogInformation("Cleared all caches through CacheManagerService");

                // Clear image cache
                await ImageHelper.ClearCacheAsync().ConfigureAwait(false);

                // Now logout from the authentication service
                await _authService.LogoutAsync().ConfigureAwait(false);

                // Navigate to server selection page and clear navigation history
                // We need to do this on UI thread and capture frame reference first
                await RunOnUIThreadAsync(() =>
                {
                    try
                    {
                        // Get frame reference while we're on UI thread
                        var frame = Window.Current?.Content as Frame;

                        // Always clear MainViewModel since it's a singleton that persists
                        ClearMainViewModelCache("during logout");

                        // Navigate to server selection page with special flag to prevent back navigation
                        _navigationService.Navigate(typeof(ServerSelectionPage), "AfterLogout");

                        // Clear the entire back stack to prevent going back to authenticated pages
                        if (frame != null)
                        {
                            frame.BackStack.Clear();
                            // Also clear forward stack if it exists
                            frame.ForwardStack.Clear();
                        }
                    }
                    catch (Exception uiEx)
                    {
                        Logger.LogError(uiEx, "Error during UI thread logout operations");
                    }
                });

                // Clear all app data from disk storage (settings, caches, everything)
                // Note: This only clears disk storage, not in-memory data, which is why
                // we manually cleared ViewModels and caches above
                await ApplicationData.Current.ClearAsync().AsTask().ConfigureAwait(false);

                Logger.LogInformation("Logout completed successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Sign out error");

                // Show error using DialogService
                await _dialogService.ShowErrorAsync("Sign Out Failed",
                    "An error occurred during sign out. Please try again.");
            }
        }

        private async Task UpdateConnectionTimeoutAsync(int value)
        {
            await UpdateAppPreferenceAsync(prefs => prefs.ConnectionTimeout = value, "connection timeout");
        }

        private async Task UpdateAllowSelfSignedCertificatesAsync(bool value)
        {
            await UpdateAppPreferenceAsync(prefs => prefs.IgnoreCertificateErrors = value,
                "allow self-signed certificates");
            Logger.LogInformation($"IgnoreCertificateErrors set to {value} from ServerSettingsViewModel.");
        }

        #region Server Properties

        public string ServerUrl
        {
            get => _serverUrl;
            set => SetProperty(ref _serverUrl, value);
        }

        public string Username
        {
            get => _username;
            set => SetProperty(ref _username, value);
        }

        public int ConnectionTimeout
        {
            get => _connectionTimeout;
            set
            {
                if (SetSettingProperty(ref _connectionTimeout, value))
                {
                    FireAndForget(() => UpdateConnectionTimeoutAsync(value));
                }
            }
        }

        public bool AllowSelfSignedCertificates
        {
            get => _allowSelfSignedCertificates;
            set
            {
                if (SetSettingProperty(ref _allowSelfSignedCertificates, value))
                {
                    FireAndForget(() => UpdateAllowSelfSignedCertificatesAsync(value));
                }
            }
        }

        #endregion
    }
}
