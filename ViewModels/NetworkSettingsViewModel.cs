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
    ///     Handles network-related settings and status display
    /// </summary>
    public class NetworkSettingsViewModel : BaseViewModel
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

        private readonly ISystemMonitorService _networkMonitor;
        protected readonly IPreferencesService PreferencesService;

        // Settings state
        private bool _hasUnsavedChanges = false;
        private string _validationError;

        private NetworkMetrics _currentMetrics;
        private string _latency;
        private string _networkName;

        // Network status properties
        private string _networkStatus;
        private int _signalStrength = 0;
        private string _transferRate;

        public NetworkSettingsViewModel(
            ILogger<NetworkSettingsViewModel> logger,
            IPreferencesService preferencesService,
            ISystemMonitorService networkMonitor) : base(logger)
        {
            PreferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
            _networkMonitor = networkMonitor ?? throw new ArgumentNullException(nameof(networkMonitor));
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
            await RunOnUIThreadAsync(() => HasUnsavedChanges = false);
        }

        protected override async Task RefreshDataCoreAsync()
        {
            // Refresh just reloads settings
            await LoadSettingsAsync(CancellationToken.None).ConfigureAwait(false);
            await RunOnUIThreadAsync(() => HasUnsavedChanges = false);
        }

        protected override Task ClearDataCoreAsync()
        {
            ValidationError = null;
            HasUnsavedChanges = false;

            // Unsubscribe from network events
            if (_networkMonitor != null)
            {
                _networkMonitor.NetworkMetricsUpdated -= OnNetworkMetricsUpdated;
            }

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
            // Subscribe to network changes
            if (_networkMonitor != null)
            {
                _networkMonitor.NetworkMetricsUpdated += OnNetworkMetricsUpdated;
            }

            // Get initial network status
            await UpdateNetworkMetrics().ConfigureAwait(false);
        }

        /// <summary>
        ///     Saves the settings to storage
        /// </summary>
        protected Task SaveSettingsInternalAsync()
        {
            // Network settings are read-only status displays, nothing to save
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Resets settings to their default values
        /// </summary>
        protected Task ResetToDefaultsInternalAsync()
        {
            // Network settings are read-only status displays, nothing to reset
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Validates the current settings
        /// </summary>
        protected Task<ValidationResult> ValidateSettingsAsync()
        {
            // Network settings are read-only status displays, nothing to validate
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

        private void OnNetworkMetricsUpdated(object sender, NetworkMetrics metrics)
        {
            CurrentMetrics = metrics;
            _ = UpdateNetworkMetrics();
        }

        private async Task UpdateNetworkMetrics()
        {
            try
            {
                var metrics = _networkMonitor.NetworkMetrics;
                if (metrics != null)
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        NetworkStatus = GetNetworkStatusString(metrics.IsConnected, metrics.ConnectionType);
                        NetworkName = metrics.NetworkName ?? "Unknown";
                        SignalStrength = metrics.SignalStrength;
                        TransferRate = FormatTransferRate((long)metrics.TransferRate);
                        Latency = metrics.Latency > 0 ? $"{metrics.Latency:F0} ms" : "N/A";
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to update network metrics display");
            }
        }

        private string GetNetworkStatusString(bool isConnected, ConnectionType connectionType)
        {
            if (!isConnected)
            {
                return "Disconnected";
            }

            return connectionType switch
            {
                ConnectionType.Ethernet => "Connected (Ethernet)",
                ConnectionType.WiFi => "Connected (Wi-Fi)",
                ConnectionType.Cellular => "Connected (Cellular)",
                ConnectionType.Unknown => "Connected",
                ConnectionType.None => "No Connection",
                _ => "Unknown"
            };
        }

        private string FormatTransferRate(long bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
            {
                return "N/A";
            }

            const int unit = 1024;
            if (bytesPerSecond < unit)
            {
                return $"{bytesPerSecond} B/s";
            }

            var exp = (int)(Math.Log(bytesPerSecond) / Math.Log(unit));
            var size = bytesPerSecond / Math.Pow(unit, exp);
            var suffix = exp switch
            {
                1 => "KB/s",
                2 => "MB/s",
                3 => "GB/s",
                _ => "B/s"
            };

            return $"{size:F1} {suffix}";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Unsubscribe from network events
                if (_networkMonitor != null)
                {
                    _networkMonitor.NetworkMetricsUpdated -= OnNetworkMetricsUpdated;
                }
            }

            base.Dispose(disposing);
        }

        #region Network Status Properties

        public NetworkMetrics CurrentMetrics
        {
            get => _currentMetrics;
            private set => SetProperty(ref _currentMetrics, value);
        }

        public string NetworkStatus
        {
            get => _networkStatus;
            private set => SetProperty(ref _networkStatus, value);
        }

        public string NetworkName
        {
            get => _networkName;
            private set => SetProperty(ref _networkName, value);
        }

        public int SignalStrength
        {
            get => _signalStrength;
            private set => SetProperty(ref _signalStrength, value);
        }

        public string TransferRate
        {
            get => _transferRate;
            private set => SetProperty(ref _transferRate, value);
        }

        public string Latency
        {
            get => _latency;
            private set => SetProperty(ref _latency, value);
        }

        #endregion
    }
}