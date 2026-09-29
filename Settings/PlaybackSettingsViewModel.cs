using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Shared.Preferences;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Settings
{
    public class PlaybackSettingsViewModel : SettingsViewModelBase
    {
        // Shown until the saved preferences load: the defaults they carry, stated once
        private static readonly AppPreferences Defaults = new();

        private bool _allowAudioStreamCopy = Defaults.AllowAudioStreamCopy;
        private bool _audioNormalizationEnabled = Defaults.AudioNormalizationEnabled;
        private bool _autoPlayNextEpisode = Defaults.AutoPlayNextEpisode;
        private bool _autoSkipIntros = Defaults.AutoSkipIntroEnabled;
        private bool _autoSkipOutros = Defaults.AutoSkipOutroEnabled;
        private int _controlsHideDelay = Defaults.ControlsHideDelay;
        private bool _enableDirectPlay = Defaults.EnableDirectPlay;
        private string _maxStreamingBitrateMbps = Defaults.MaxStreamingBitrateMbps.ToString(CultureInfo.InvariantCulture);
        private bool _pauseOnFocusLoss = Defaults.PauseOnFocusLoss;
        private string _videoStretchMode = Defaults.VideoStretchMode;

        public PlaybackSettingsViewModel(
            ILogger<PlaybackSettingsViewModel> logger,
            IPreferencesService preferencesService) : base(logger, preferencesService)
        {
        }

        protected override async Task LoadSettingsAsync(CancellationToken cancellationToken)
        {
            var appPrefs = await PreferencesService.GetAppPreferencesAsync().ConfigureAwait(false);

            await RunOnUIThreadAsync(() =>
            {
                _autoPlayNextEpisode = appPrefs.AutoPlayNextEpisode;
                _pauseOnFocusLoss = appPrefs.PauseOnFocusLoss;
                _autoSkipIntros = appPrefs.AutoSkipIntroEnabled;
                _autoSkipOutros = appPrefs.AutoSkipOutroEnabled;
                _controlsHideDelay = appPrefs.ControlsHideDelay;
                _enableDirectPlay = appPrefs.EnableDirectPlay;
                _maxStreamingBitrateMbps = appPrefs.MaxStreamingBitrateMbps.ToString(CultureInfo.InvariantCulture);
                _allowAudioStreamCopy = appPrefs.AllowAudioStreamCopy;
                _videoStretchMode = appPrefs.VideoStretchMode;
                _audioNormalizationEnabled = appPrefs.AudioNormalizationEnabled;

                OnPropertyChanged(nameof(AutoPlayNextEpisode));
                OnPropertyChanged(nameof(PauseOnFocusLoss));
                OnPropertyChanged(nameof(AutoSkipIntros));
                OnPropertyChanged(nameof(AutoSkipOutros));
                OnPropertyChanged(nameof(ControlsHideDelay));
                OnPropertyChanged(nameof(EnableDirectPlay));
                OnPropertyChanged(nameof(MaxStreamingBitrateMbps));
                OnPropertyChanged(nameof(AllowAudioStreamCopy));
                OnPropertyChanged(nameof(VideoStretchMode));
                OnPropertyChanged(nameof(AudioNormalizationEnabled));
            });
        }

        public bool AutoPlayNextEpisode
        {
            get => _autoPlayNextEpisode;
            set => SetAndSave(ref _autoPlayNextEpisode, value, (prefs, v) => prefs.AutoPlayNextEpisode = v);
        }

        public bool PauseOnFocusLoss
        {
            get => _pauseOnFocusLoss;
            set => SetAndSave(ref _pauseOnFocusLoss, value, (prefs, v) => prefs.PauseOnFocusLoss = v);
        }

        public bool AutoSkipIntros
        {
            get => _autoSkipIntros;
            set => SetAndSave(ref _autoSkipIntros, value, (prefs, v) => prefs.AutoSkipIntroEnabled = v);
        }

        public bool AutoSkipOutros
        {
            get => _autoSkipOutros;
            set => SetAndSave(ref _autoSkipOutros, value, (prefs, v) => prefs.AutoSkipOutroEnabled = v);
        }

        public int ControlsHideDelay
        {
            get => _controlsHideDelay;
            set => SetAndSave(ref _controlsHideDelay, value, (prefs, v) => prefs.ControlsHideDelay = v);
        }

        public bool EnableDirectPlay
        {
            get => _enableDirectPlay;
            set => SetAndSave(ref _enableDirectPlay, value, (prefs, v) => prefs.EnableDirectPlay = v);
        }

        // A string because the ComboBox selects by its items' string Tags.
        public string MaxStreamingBitrateMbps
        {
            get => _maxStreamingBitrateMbps;
            set
            {
                if (value == null || !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var mbps))
                {
                    return;
                }

                if (SetProperty(ref _maxStreamingBitrateMbps, value))
                {
                    FireAndForget(() => UpdateAppPreferenceAsync(prefs => prefs.MaxStreamingBitrateMbps = mbps, mbps));
                }
            }
        }

        public bool AllowAudioStreamCopy
        {
            get => _allowAudioStreamCopy;
            set => SetAndSave(ref _allowAudioStreamCopy, value, (prefs, v) => prefs.AllowAudioStreamCopy = v);
        }

        public string VideoStretchMode
        {
            get => _videoStretchMode;
            set => SetAndSave(ref _videoStretchMode, value, (prefs, v) => prefs.VideoStretchMode = v);
        }

        public bool AudioNormalizationEnabled
        {
            get => _audioNormalizationEnabled;
            set => SetAndSave(ref _audioNormalizationEnabled, value, (prefs, v) => prefs.AudioNormalizationEnabled = v);
        }
    }
}
