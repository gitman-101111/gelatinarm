using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Helpers;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     Main settings view model that coordinates the three specialized settings view models
    /// </summary>
    public class SettingsViewModel : BaseViewModel
    {
        private readonly MainViewModel _mainViewModel;
        private readonly IUnifiedDeviceService _unifiedDeviceService;

        // UI Properties
        private volatile bool _isInitialized = false;
        private double _textSize = 14.0;

        public SettingsViewModel(
            IUnifiedDeviceService unifiedDeviceService,
            JellyfinApiClient apiClient,
            IAuthenticationService authService,
            IPreferencesService preferencesService,
            IMediaOptimizationService mediaOptimizationService,
            ISystemMonitorService systemMonitorService,
            INavigationService navigationService,
            IDialogService dialogService,
            MainViewModel mainViewModel,
            ILogger<SettingsViewModel> logger,
            ILogger<ServerSettingsViewModel> serverLogger,
            ILogger<PlaybackSettingsViewModel> playbackLogger) : base(logger)
        {
            _unifiedDeviceService =
                unifiedDeviceService ?? throw new ArgumentNullException(nameof(unifiedDeviceService));
            _mainViewModel = mainViewModel ?? throw new ArgumentNullException(nameof(mainViewModel)); ServerSettings = new ServerSettingsViewModel(
                serverLogger,
                apiClient,
                preferencesService,
                authService,
                navigationService,
                dialogService);

            PlaybackSettings = new PlaybackSettingsViewModel(
                playbackLogger,
                preferencesService,
                mediaOptimizationService); ResetSettingsCommand = new RelayCommand(ResetSettings);

            // Call InitializeAsync without await from constructor for async initialization
            FireAndForget(() => InitializeAsync());
        }

        // Child ViewModels
        public ServerSettingsViewModel ServerSettings { get; }
        public PlaybackSettingsViewModel PlaybackSettings { get; }

        // Commands
        public ICommand ResetSettingsCommand { get; }

        #region UI Properties

        public double TextSize
        {
            get => _textSize;
            set => SetProperty(ref _textSize, value);
        }

        #endregion

        public async Task InitializeAsync()
        {
            if (_isInitialized)
            {
                return;
            }

            // Use the standardized LoadDataAsync pattern
            await LoadDataAsync(true);
            _isInitialized = true;
        }

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            var tasks = new[]
            {
                ServerSettings.InitializeAsync(),
                PlaybackSettings.InitializeAsync()
            };

            await Task.WhenAll(tasks).ConfigureAwait(false);

            var preferencesService = GetService<IPreferencesService>();
            if (preferencesService != null)
            {
                var appPrefs = await preferencesService.GetAppPreferencesAsync().ConfigureAwait(false);

                await RunOnUIThreadAsync(() =>
                {
                    TextSize = appPrefs.TextSize;
                });
            }
        }

        protected override async Task RefreshDataCoreAsync()
        {
            var tasks = new[]
            {
                ServerSettings.RefreshAsync(),
                PlaybackSettings.RefreshAsync()
            };

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        protected override async Task ClearDataCoreAsync()
        {
            await RunOnUIThreadAsync(() => _isInitialized = false);
        }

        private void ResetSettings()
        {
            ServerSettings.ResetToDefaults();
            PlaybackSettings.ResetToDefaults();
            TextSize = 14.0;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Dispose child ViewModels if needed
            }

            base.Dispose(disposing);
        }
    }
}
