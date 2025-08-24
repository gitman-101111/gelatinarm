using System;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     Handles playback-related settings including quality, audio, subtitles, and playback behavior
    /// </summary>
    public class PlaybackSettingsViewModel : BaseViewModel
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

        private readonly IMediaOptimizationService _mediaOptimizationService;
        protected readonly IPreferencesService PreferencesService;

        // Settings state
        private bool _hasUnsavedChanges;
        private string _validationError;

        private bool _allowAudioStreamCopy;

        // Playback settings
        private bool _autoPlayNextEpisode;
        private bool _autoSkipIntros;
        private int _controlsHideDelay;
        // Quality and format settings
        private bool _enableDirectPlay;
        private bool _pauseOnFocusLoss;
        private string _videoStretchMode;
        // Audio enhancement settings
        private bool _isNightModeEnabled;

        public PlaybackSettingsViewModel(
            ILogger<PlaybackSettingsViewModel> logger,
            IPreferencesService preferencesService,
            IMediaOptimizationService mediaOptimizationService) : base(logger)
        {
            PreferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
            _mediaOptimizationService = mediaOptimizationService ??
                                        throw new ArgumentNullException(nameof(mediaOptimizationService));

        }

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
            ValidationError = null;
            await LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
            HasUnsavedChanges = false;
        }

        protected override async Task RefreshDataCoreAsync()
        {
            // Refresh just reloads settings
            await LoadSettingsAsync(CancellationToken.None).ConfigureAwait(false);
            HasUnsavedChanges = false;
        }

        protected override Task ClearDataCoreAsync()
        {
            ValidationError = null;
            HasUnsavedChanges = false;
            return Task.CompletedTask;
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
            AsyncHelper.FireAndForget(() => ResetToDefaultsAsync(), Logger, GetType());
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
            // Load all preferences from AppPreferences
            var appPrefs = await PreferencesService.GetAppPreferencesAsync().ConfigureAwait(false);

            await RunOnUIThreadAsync(() =>
            {
                // Playback settings
                _autoPlayNextEpisode = appPrefs.AutoPlayNextEpisode;
                _pauseOnFocusLoss = appPrefs.PauseOnFocusLoss;
                _autoSkipIntros = appPrefs.AutoSkipIntroEnabled;
                _controlsHideDelay = appPrefs.ControlsHideDelay;
                _enableDirectPlay = appPrefs.EnableDirectPlay;
                _allowAudioStreamCopy = appPrefs.AllowAudioStreamCopy;
                _videoStretchMode = appPrefs.VideoStretchMode;

                // Audio enhancement settings - these may not exist in AppPreferences yet
                // so we'll use default values for now
                _isNightModeEnabled = false;  // Default to false

                // Notify all properties changed
                OnPropertyChanged(nameof(AutoPlayNextEpisode));
                OnPropertyChanged(nameof(PauseOnFocusLoss));
                OnPropertyChanged(nameof(AutoSkipIntros));
                OnPropertyChanged(nameof(ControlsHideDelay));
                OnPropertyChanged(nameof(EnableDirectPlay));
                OnPropertyChanged(nameof(AllowAudioStreamCopy));
                OnPropertyChanged(nameof(VideoStretchMode));
                OnPropertyChanged(nameof(IsNightModeEnabled));
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
            // Reset to default values - these should match AppPreferences defaults
            AutoPlayNextEpisode = true;
            PauseOnFocusLoss = true;
            AutoSkipIntros = false;
            ControlsHideDelay = 3;
            EnableDirectPlay = true;
            AllowAudioStreamCopy = false;
            VideoStretchMode = "Uniform";
            IsNightModeEnabled = false;

            await Task.CompletedTask;
        }

        /// <summary>
        ///     Validates the current settings
        /// </summary>
        protected Task<ValidationResult> ValidateSettingsAsync()
        {
            // Validate ControlsHideDelay
            if (ControlsHideDelay < 1 || ControlsHideDelay > 10)
            {
                return Task.FromResult(new ValidationResult
                {
                    IsValid = false,
                    ErrorMessage = "Controls hide delay must be between 1 and 10 seconds"
                });
            }

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

        #region Playback Settings Properties

        public bool AutoPlayNextEpisode
        {
            get => _autoPlayNextEpisode;
            set
            {
                if (SetSettingProperty(ref _autoPlayNextEpisode, value))
                {
                    AsyncHelper.FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.AutoPlayNextEpisode = value,
                            nameof(AutoPlayNextEpisode)), Logger, GetType());
                }
            }
        }

        public bool PauseOnFocusLoss
        {
            get => _pauseOnFocusLoss;
            set
            {
                if (SetSettingProperty(ref _pauseOnFocusLoss, value))
                {
                    AsyncHelper.FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.PauseOnFocusLoss = value,
                            nameof(PauseOnFocusLoss)), Logger, GetType());
                }
            }
        }

        public bool AutoSkipIntros
        {
            get => _autoSkipIntros;
            set
            {
                if (SetSettingProperty(ref _autoSkipIntros, value))
                {
                    AsyncHelper.FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.AutoSkipIntroEnabled = value,
                            nameof(AutoSkipIntros)), Logger, GetType());
                }
            }
        }

        public int ControlsHideDelay
        {
            get => _controlsHideDelay;
            set
            {
                if (SetSettingProperty(ref _controlsHideDelay, value))
                {
                    AsyncHelper.FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.ControlsHideDelay = value,
                            nameof(ControlsHideDelay)), Logger, GetType());
                }
            }
        }

        public bool EnableDirectPlay
        {
            get => _enableDirectPlay;
            set
            {
                if (SetSettingProperty(ref _enableDirectPlay, value))
                {
                    AsyncHelper.FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.EnableDirectPlay = value,
                            nameof(EnableDirectPlay)), Logger, GetType());
                    // Media optimization service doesn't have InvalidateRecommendations method                    Logger.LogInformation("Direct play setting changed to {Value}", value);
                }
            }
        }

        public bool AllowAudioStreamCopy
        {
            get => _allowAudioStreamCopy;
            set
            {
                if (SetSettingProperty(ref _allowAudioStreamCopy, value))
                {
                    AsyncHelper.FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.AllowAudioStreamCopy = value,
                            nameof(AllowAudioStreamCopy)), Logger, GetType());
                    // Media optimization service doesn't have InvalidateRecommendations method                    Logger.LogInformation("Audio stream copy setting changed to {Value}", value);
                }
            }
        }


        public string VideoStretchMode
        {
            get => _videoStretchMode;
            set
            {
                if (SetSettingProperty(ref _videoStretchMode, value))
                {
                    AsyncHelper.FireAndForget(
                        () => UpdateAppPreferenceAsync(prefs => prefs.VideoStretchMode = value,
                            nameof(VideoStretchMode)), Logger, GetType());
                }
            }
        }

        public bool IsNightModeEnabled
        {
            get => _isNightModeEnabled;
            set
            {
                if (SetSettingProperty(ref _isNightModeEnabled, value))
                {
                    // Since AppPreferences doesn't have this property yet, just log it
                    Logger.LogInformation("Night mode setting changed to {Value}", value);

                    // Call MediaOptimizationService if it has night mode methods
                    _mediaOptimizationService?.SetNightMode(value);
                }
            }
        }

        #endregion
    }
}