using System;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace Gelatinarm.Controls
{
    public sealed partial class PlaybackSettings : BaseControl
    {

        private bool _isInitializing = false;
        private JellyfinApiClient _jellyfinApi;
        private AppPreferences _preferences;
        private IPreferencesService _preferencesService;

        public PlaybackSettings()
        {
            InitializeComponent();
        }

        protected override void OnServicesInitialized(IServiceProvider services)
        {
            // Get additional services needed by this control
            _jellyfinApi = GetService<JellyfinApiClient>();
            _preferencesService = GetService<IPreferencesService>();
        }

        public void Initialize(AppPreferences preferences)
        {
            _isInitializing = true;
            _preferences = preferences;

            // Set control values from preferences
            AutoSkipIntroToggle.IsOn = _preferences.AutoSkipIntroEnabled;
            ControlsHideDelayBox.Value = _preferences.ControlsHideDelay;

            // Set video stretch mode
            VideoStretchModeCombo.SelectedIndex = _preferences.VideoStretchMode == "UniformToFill" ? 1 : 0;

            _isInitializing = false;
        }

        private async void AutoSkipIntroToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (!_isInitializing && _preferences != null && _preferencesService != null)
            {
                var context = CreateErrorContext("AutoSkipIntroToggle");
                try
                {
                    _preferences.AutoSkipIntroEnabled = AutoSkipIntroToggle.IsOn;
                    await _preferencesService.UpdateAppPreferencesAsync(_preferences);
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            }
        }

        private async void ControlsHideDelayBox_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (!_isInitializing && _preferences != null && e.NewValue >= 1 && e.NewValue <= 30 &&
                _preferencesService != null)
            {
                var context = CreateErrorContext("ControlsHideDelayBox");
                try
                {
                    _preferences.ControlsHideDelay = (int)e.NewValue;
                    await _preferencesService.UpdateAppPreferencesAsync(_preferences);
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            }
        }

        private async void VideoStretchModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_isInitializing && _preferences != null && VideoStretchModeCombo.SelectedIndex >= 0 &&
                _preferencesService != null)
            {
                var context = CreateErrorContext("VideoStretchModeCombo");
                try
                {
                    _preferences.VideoStretchMode =
                        VideoStretchModeCombo.SelectedIndex == 1 ? "UniformToFill" : "Uniform";
                    await _preferencesService.UpdateAppPreferencesAsync(_preferences);
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            }
        }


        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_preferences != null && _preferencesService != null)
            {
                var context = CreateErrorContext("SaveButton");
                try
                {
                    // Save current settings as global defaults
                    await _preferencesService.SaveAsDefaultAppPreferencesAsync(_preferences);
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context);
                }
            }
        }

        private async void ServerSettingsLink_Click(object sender, RoutedEventArgs e)
        {
            var context = CreateErrorContext("ServerSettingsLink");
            try
            {
                // Open server settings page in browser
                // The JellyfinApiClient itself doesn't store the BaseUrl directly accessible like the old SDK.
                // The BaseUrl is part of the RequestAdapter's configuration.
                // We should get the currently connected server URL from a service, e.g., UserProfileService or a dedicated ServerInfoService.

                // Assuming UserProfileService stores the current server URL after successful connection:
                var serverUrl = _preferencesService?.GetValue<string>("ServerUrl"); // Or from a more specific service

                if (!string.IsNullOrEmpty(serverUrl))
                {
                    var settingsUrl = $"{serverUrl.TrimEnd('/')}/web/index.html#!/userpreferences.html";
                    await Launcher.LaunchUriAsync(new Uri(settingsUrl));
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }
    }
}
