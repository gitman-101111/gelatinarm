using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.SignIn;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Settings
{
    public partial class ServerSettingsViewModel : SettingsViewModelBase
    {
        private readonly IAuthenticationService _authService;
        private readonly IDialogService _dialogService;
        private readonly INavigationService _navigationService;

        // Shown until the saved preferences load: the default they carry, stated once
        private int _connectionTimeout = new AppPreferences().ConnectionTimeout;

        public ServerSettingsViewModel(
            ILogger<ServerSettingsViewModel> logger,
            IPreferencesService preferencesService,
            IAuthenticationService authService,
            INavigationService navigationService,
            IDialogService dialogService) : base(logger, preferencesService)
        {
            _authService = authService;
            _navigationService = navigationService;
            _dialogService = dialogService;
        }

        /// <summary>
        ///     Toggles the Switch User / Add User buttons
        /// </summary>
        public bool HasMultipleProfiles => _authService.HasMultipleSavedProfiles;

        protected override async Task LoadSettingsAsync(CancellationToken cancellationToken)
        {
            var appPrefs = await PreferencesService.GetAppPreferencesAsync().ConfigureAwait(false);

            await RunOnUIThreadAsync(() =>
            {
                _connectionTimeout = appPrefs.ConnectionTimeout;

                OnPropertyChanged(nameof(ConnectionTimeout));
                OnPropertyChanged(nameof(HasMultipleProfiles));
            });
        }

        [RelayCommand]
        private void SwitchUser()
        {
            _navigationService.Navigate(typeof(ProfileSelectionPage));
        }

        [RelayCommand]
        private async Task AddUserAsync()
        {
            await StopMusicBeforeUserChangeAsync();
            _navigationService.Navigate(typeof(LoginPage));
        }

        [RelayCommand]
        private async Task SignOutAsync()
        {
            try
            {
                Logger.LogDebug("Starting logout process");

                // Stop music first, while this user's token is still valid
                await StopMusicBeforeUserChangeAsync();

                // Logout removes only the active user's credentials and profile. Other saved
                // profiles on this device stay intact
                _authService.Logout();

                var hasRemainingProfiles = _authService.GetSavedProfiles().Count > 0;

                await RunOnUIThreadAsync(() =>
                {
                    // With profiles remaining, go to the picker; otherwise start over
                    // at server selection
                    _navigationService.Navigate(hasRemainingProfiles
                        ? typeof(ProfileSelectionPage)
                        : typeof(ServerSelectionPage));

                    // The pages behind belong to the user who just signed out
                    _navigationService.ClearBackStack();
                });

                // When the last profile signs out, clear all app data from disk storage
                // (settings, caches, everything). With profiles remaining, their settings
                // and the saved profile list must survive, so only the caches above go.
                if (!hasRemainingProfiles)
                {
                    await ApplicationData.Current.ClearAsync().AsTask().ConfigureAwait(false);
                }

                Logger.LogDebug("Logout completed successfully");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("SignOut", ErrorCategory.Authentication), false);

                await _dialogService.ShowMessageAsync("Sign Out Failed",
                    "An error occurred during sign out. Please try again.");
            }
        }

        public int ConnectionTimeout
        {
            get => _connectionTimeout;
            set => SetAndSave(ref _connectionTimeout, value, (prefs, v) => prefs.ConnectionTimeout = v);
        }
    }
}
