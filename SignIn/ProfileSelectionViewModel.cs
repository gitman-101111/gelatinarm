using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Home;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Navigation;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.SignIn
{
    /// <summary>
    ///     A tile in the profile picker: either a saved user profile or the "Add user" action.
    /// </summary>
    public partial class ProfileTile : ObservableObject
    {
        public UserProfile Profile { get; set; }
        public string DisplayName { get; set; }

        // PersonPicture shows the initials only while this is null; a picture that failed to
        // load leaves the circle empty, so a failed load clears it
        [ObservableProperty] private ImageSource _avatar;

        // Shown only when the saved profiles span more than one server
        public string ServerName { get; set; }

        public bool IsAddTile => Profile == null;
        public Visibility AvatarVisibility => IsAddTile ? Visibility.Collapsed : Visibility.Visible;
        public Visibility AddIconVisibility => IsAddTile ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ServerNameVisibility => string.IsNullOrEmpty(ServerName) ? Visibility.Collapsed : Visibility.Visible;
    }

    public partial class ProfileSelectionViewModel : BaseViewModel, IPageViewModel
    {
        private readonly IAuthenticationService _authService;
        private readonly IAvatarCache _avatarCache;
        private readonly INavigationService _navigationService;

        [ObservableProperty] private bool _isSwitching;

        public ProfileSelectionViewModel(
            IAuthenticationService authService,
            IAvatarCache avatarCache,
            INavigationService navigationService,
            ILogger<ProfileSelectionViewModel> logger) : base(logger)
        {
            _authService = authService;
            _avatarCache = avatarCache;
            _navigationService = navigationService;
        }

        public ObservableCollection<ProfileTile> Profiles { get; } = new();

        /// <summary>
        ///     Must run on the UI thread: it creates BitmapImage instances
        /// </summary>
        public void Initialize(object parameter)
        {
            Profiles.Clear();

            // Two "ashik" tiles on two servers need telling apart; one server needs no labels
            var nameServers = _authService.HasMultipleSavedServers;

            foreach (var profile in _authService.GetSavedProfiles())
            {
                var tile = new ProfileTile
                {
                    Profile = profile,
                    DisplayName = profile.Username,
                    ServerName = nameServers
                        ? AuthenticationService.DisplayServerName(profile.ServerName, profile.ServerUrl)
                        : null
                };
                tile.Avatar = CreateAvatarSource(tile);
                Profiles.Add(tile);
            }

            Profiles.Add(new ProfileTile { DisplayName = "Add user" });
        }

        private ImageSource CreateAvatarSource(ProfileTile tile)
        {
            var profile = tile.Profile;
            try
            {
                if (string.IsNullOrEmpty(profile.PrimaryImageTag) || !Guid.TryParse(profile.UserId, out var userGuid))
                {
                    return null; // PersonPicture falls back to initials
                }

                // The copy saved at the last sign-in first: it needs no server at all
                var source = _avatarCache.TryGetCached(profile);
                if (source == null)
                {
                    var url = ImageHelper.BuildUserImageUrl(profile.ServerUrl, userGuid, profile.PrimaryImageTag);
                    if (url == null)
                    {
                        return null;
                    }

                    source = new Uri(url);
                }

                // No server-side resizing on /UserImage - decode at display size instead
                var image = new BitmapImage(source) { DecodePixelWidth = 200 };
                // The profile's server may be unreachable from here (a remote server, a LAN
                // address away from home): initials rather than an empty circle
                image.ImageFailed += (_, args) =>
                {
                    Logger.LogDebug("Avatar for {Username} did not load: {Message}", profile.Username, args.ErrorMessage);
                    tile.Avatar = null;
                };
                return image;
            }
            catch (Exception ex)
            {
                // Initials stand in
                ErrorHandler.HandleError(ex, CreateErrorContext($"CreateAvatarSource:{profile.Username}", ErrorCategory.Media, ErrorSeverity.Warning));
                return null;
            }
        }

        [RelayCommand]
        private async Task SelectProfileAsync(ProfileTile tile)
        {
            if (tile == null || IsSwitching)
            {
                return;
            }

            // Leaving the current user: stop music while their token is still valid
            if (tile.IsAddTile || tile.Profile.UserId != _authService.UserId)
            {
                await StopMusicBeforeUserChangeAsync();
            }

            if (tile.IsAddTile)
            {
                _navigationService.Navigate(typeof(LoginPage));
                return;
            }

            IsSwitching = true;
            var context = CreateErrorContext("SwitchProfile", ErrorCategory.Authentication);
            try
            {
                var switched = await _authService.SwitchToProfileAsync(tile.Profile.UserId, DisposalCts.Token);
                if (switched)
                {
                    _navigationService.Navigate(typeof(MainPage));
                }
                else
                {
                    // No valid stored token - run the normal login flow pre-filled for this user
                    Logger.LogInformation("Profile {ProfileUsername} needs to sign in again", tile.Profile.Username);
                    _navigationService.Navigate(typeof(LoginPage), tile.Profile.Username);
                }
            }
            catch (Exception ex)
            {
                // The picker stays as it was, so without a message the choice looks ignored
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
            finally
            {
                IsSwitching = false;
            }
        }
    }
}
