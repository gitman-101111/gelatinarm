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
    public class ProfileTile
    {
        public UserProfile Profile { get; set; }
        public string DisplayName { get; set; }
        public ImageSource Avatar { get; set; }

        public bool IsAddTile => Profile == null;
        public Visibility AvatarVisibility => IsAddTile ? Visibility.Collapsed : Visibility.Visible;
        public Visibility AddIconVisibility => IsAddTile ? Visibility.Visible : Visibility.Collapsed;
    }

    public partial class ProfileSelectionViewModel : BaseViewModel, IPageViewModel
    {
        private readonly IAuthenticationService _authService;
        private readonly INavigationService _navigationService;

        [ObservableProperty] private bool _isSwitching;

        public ProfileSelectionViewModel(
            IAuthenticationService authService,
            INavigationService navigationService,
            ILogger<ProfileSelectionViewModel> logger) : base(logger)
        {
            _authService = authService;
            _navigationService = navigationService;
        }

        public ObservableCollection<ProfileTile> Profiles { get; } = new();

        /// <summary>
        ///     Must run on the UI thread: it creates BitmapImage instances
        /// </summary>
        public void Initialize(object parameter)
        {
            Profiles.Clear();

            foreach (var profile in _authService.GetSavedProfiles())
            {
                Profiles.Add(new ProfileTile
                {
                    Profile = profile,
                    DisplayName = profile.Username,
                    Avatar = CreateAvatarSource(profile)
                });
            }

            Profiles.Add(new ProfileTile { DisplayName = "Add user" });
        }

        private ImageSource CreateAvatarSource(UserProfile profile)
        {
            try
            {
                if (string.IsNullOrEmpty(profile.PrimaryImageTag) || !Guid.TryParse(profile.UserId, out var userGuid))
                {
                    return null; // PersonPicture falls back to initials
                }

                var url = ImageHelper.BuildUserImageUrl(userGuid, profile.PrimaryImageTag);
                // No server-side resizing on /UserImage - decode at display size instead
                return url != null ? new BitmapImage(new Uri(url)) { DecodePixelWidth = 200 } : null;
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
