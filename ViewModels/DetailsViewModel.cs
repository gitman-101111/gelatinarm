using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.Views;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Media.Imaging;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     Generic view model for detail pages
    /// </summary>
    /// <typeparam name="TItem">The type of item being displayed</typeparam>
    public abstract partial class DetailsViewModel<TItem> : BaseViewModel,
        IInitializableViewModel
        where TItem : BaseItemDto
    {
        // Services
        protected readonly JellyfinApiClient ApiClient;
        protected readonly IImageLoadingService ImageLoadingService;
        protected readonly IMediaPlaybackService MediaPlaybackService;
        protected readonly INavigationService NavigationService;
        protected readonly IUserDataService UserDataService;
        protected readonly IUserProfileService UserProfileService;

        [ObservableProperty] private BitmapImage _backdropImage;

        [ObservableProperty] private bool _canPlay;

        [ObservableProperty] private bool _canResume;

        [ObservableProperty] private double? _communityRating;

        // Observable properties
        [ObservableProperty] private TItem _currentItem;

        [ObservableProperty] private string _director;

        [ObservableProperty] private string _favoriteButtonText = "Favorite";

        [ObservableProperty] private string _genresText;

        [ObservableProperty] private bool _hasBeenPlayed;

        [ObservableProperty] private bool _hasProgress;

        [ObservableProperty] private bool _isFavorite;

        [ObservableProperty] private bool _isWatched;

        [ObservableProperty] private string _overview;

        [ObservableProperty] private string _playButtonText = "Play";

        [ObservableProperty] private double _playedPercentage;

        [ObservableProperty] private BitmapImage _primaryImage;

        [ObservableProperty] private string _progressText;

        [ObservableProperty] private double _progressWidth;

        [ObservableProperty] private string _rating;

        [ObservableProperty] private string _resumeButtonText = "Resume";

        [ObservableProperty] private string _runtime;

        [ObservableProperty] private bool _showProgress;

        [ObservableProperty] private string _title;

        [ObservableProperty] private string _watchedButtonText = "Mark Watched";

        [ObservableProperty] private string _watchedPercentageText;

        [ObservableProperty] private string _writers;

        [ObservableProperty] private string _year;

        protected DetailsViewModel(
            ILogger logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IMediaPlaybackService mediaPlaybackService,
            IUserDataService userDataService) : base(logger)
        {
            ApiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            UserProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
            NavigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            ImageLoadingService = imageLoadingService ?? throw new ArgumentNullException(nameof(imageLoadingService));
            MediaPlaybackService =
                mediaPlaybackService ?? throw new ArgumentNullException(nameof(mediaPlaybackService));
            UserDataService = userDataService ?? throw new ArgumentNullException(nameof(userDataService)); PlayCommand = new AsyncRelayCommand(PlayAsync);
            ResumeCommand = new AsyncRelayCommand(ResumeAsync);
            RestartCommand = new AsyncRelayCommand(RestartAsync);
            ToggleFavoriteCommand = new AsyncRelayCommand(async () =>
            {
                Logger?.LogInformation($"ToggleFavoriteCommand executed. Current IsFavorite: {IsFavorite}");
                await ToggleFavoriteAsync(!IsFavorite);
            });
            ToggleWatchedCommand = new AsyncRelayCommand(async () =>
            {
                Logger?.LogInformation($"ToggleWatchedCommand executed. Current IsWatched: {IsWatched}");
                await ToggleWatchedAsync(!IsWatched);
            });
            RefreshCommand = new AsyncRelayCommand(RefreshAsync);

            // Get user ID
            var context = CreateErrorContext("GetUserId", ErrorCategory.User);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    var userId = UserProfileService.CurrentUserId;
                    if (!string.IsNullOrEmpty(userId) && Guid.TryParse(userId, out var userGuid))
                    {
                        UserIdGuid = userGuid;
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        // Commands
        public IAsyncRelayCommand PlayCommand { get; }
        public IAsyncRelayCommand ResumeCommand { get; }
        public IAsyncRelayCommand RestartCommand { get; }
        public IAsyncRelayCommand ToggleFavoriteCommand { get; }
        public IAsyncRelayCommand ToggleWatchedCommand { get; }
        public IAsyncRelayCommand RefreshCommand { get; }

        protected Guid? UserIdGuid { get; private set; }

        /// <summary>
        ///     Toggle favorite status
        /// </summary>
        public virtual async Task ToggleFavoriteAsync(bool isFavorite)
        {
            Logger?.LogInformation(
                $"ToggleFavoriteAsync called with isFavorite={isFavorite}, CurrentItem.Id={CurrentItem?.Id}, UserIdGuid={UserIdGuid}");

            if (CurrentItem?.Id == null)
            {
                Logger?.LogWarning("Cannot toggle favorite - missing item ID");
                return;
            }

            var context = CreateErrorContext("ToggleFavorite", ErrorCategory.User);

            // Store original value for reversion on error
            var originalValue = IsFavorite;

            try
            {
                // Update local state optimistically
                IsFavorite = isFavorite;
                FavoriteButtonText = isFavorite ? "Unfavorite" : "Favorite";
                OnPropertyChanged(nameof(IsFavorite));
                OnPropertyChanged(nameof(FavoriteButtonText));

                // Use UserDataService to toggle favorite
                var updatedData =
                    await UserDataService.ToggleFavoriteAsync(CurrentItem.Id.Value, isFavorite, UserIdGuid);

                if (updatedData != null)
                {
                    // Update with actual server state
                    IsFavorite = updatedData.IsFavorite ?? false;
                    FavoriteButtonText = IsFavorite ? "Unfavorite" : "Favorite";
                    if (CurrentItem.UserData != null)
                    {
                        CurrentItem.UserData.IsFavorite = updatedData.IsFavorite;
                    }

                    Logger?.LogInformation($"Successfully toggled favorite to {updatedData.IsFavorite}");
                }
            }
            catch (Exception ex)
            {
                // Revert to original value on error
                IsFavorite = originalValue;
                await ErrorHandler.HandleErrorAsync(ex, context, true);
            }
        }

        /// <summary>
        ///     Updates the favorite button text based on the current item's favorite status
        /// </summary>
        protected virtual void UpdateFavoriteButton()
        {
            if (CurrentItem?.UserData != null)
            {
                IsFavorite = CurrentItem.UserData.IsFavorite ?? false;
                FavoriteButtonText = IsFavorite ? "Remove from Favorites" : "Add to Favorites";
            }
        }

        /// <summary>
        ///     Loads the primary image for the current item
        /// </summary>
        protected void LoadPrimaryImage(Action<BitmapImage> setImageAction, string operationName = "LoadImage")
        {
            if (CurrentItem?.Id == null)
            {
                return;
            }

            var context = CreateErrorContext(operationName, ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    string imageTag = null;
                    if (CurrentItem.ImageTags?.AdditionalData?.ContainsKey("Primary") == true)
                    {
                        imageTag = CurrentItem.ImageTags.AdditionalData["Primary"]?.ToString();
                    }

                    var imageUrl = ImageHelper.BuildImageUrl(CurrentItem.Id.Value, "Primary", 400, null, imageTag);
                    if (!string.IsNullOrEmpty(imageUrl))
                    {
                        setImageAction(new BitmapImage(new Uri(imageUrl)));
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        /// <summary>
        ///     Initialize the view model with a parameter
        /// </summary>
        public virtual async Task InitializeAsync(object parameter)
        {
            Logger?.LogInformation(
                $"DetailsViewModel.InitializeAsync called with parameter type: {parameter?.GetType()?.Name ?? "null"}");

            if (parameter == null)
            {
                Logger?.LogWarning("InitializeAsync called with null parameter");
                return;
            }

            var context = CreateErrorContext("InitializeViewModel", ErrorCategory.Media);
            try
            {
                IsLoading = true;
                Logger?.LogInformation("Setting IsLoading = true");

                // Handle different parameter types
                if (parameter is TItem item)
                {
                    Logger?.LogInformation($"Parameter is TItem ({typeof(TItem).Name}), calling LoadItemAsync");
                    await LoadItemAsync(item);
                }
                else if (parameter is BaseItemDto baseItem && baseItem is TItem typedItem)
                {
                    Logger?.LogInformation("Parameter is BaseItemDto convertible to TItem, calling LoadItemAsync");
                    await LoadItemAsync(typedItem);
                }
                else if (parameter is string itemId && Guid.TryParse(itemId, out var guidId))
                {
                    Logger?.LogInformation($"Parameter is string GUID: {guidId}, calling LoadItemByIdAsync");
                    await LoadItemByIdAsync(guidId);
                }
                else
                {
                    Logger?.LogError(
                        $"Invalid parameter type: {parameter.GetType()}. Expected {typeof(TItem).Name}, BaseItemDto, or string GUID");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, true);
            }

            IsLoading = false;
            Logger?.LogInformation("Setting IsLoading = false");
        }

        /// <summary>
        ///     Play the item
        /// </summary>
        public virtual async Task PlayAsync()
        {
            if (CurrentItem == null || MediaPlaybackService == null)
            {
                return;
            }

            var context = CreateErrorContext("PlayMedia", ErrorCategory.Media);
            try
            {
                // If the item can be resumed, use the resume position
                if (CanResume && CurrentItem.UserData?.PlaybackPositionTicks.HasValue == true)
                {
                    await MediaPlaybackService.PlayMediaAsync(CurrentItem, CurrentItem.UserData.PlaybackPositionTicks);
                }
                else
                {
                    await MediaPlaybackService.PlayMediaAsync(CurrentItem);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, true);
            }
        }

        /// <summary>
        ///     Resume playback of the item
        /// </summary>
        public virtual async Task ResumeAsync()
        {
            if (CurrentItem == null || MediaPlaybackService == null)
            {
                return;
            }

            var context = CreateErrorContext("ResumeMedia", ErrorCategory.Media);
            try
            {
                var resumePosition = CurrentItem.UserData?.PlaybackPositionTicks;
                await MediaPlaybackService.PlayMediaAsync(CurrentItem, resumePosition);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, true);
            }
        }

        /// <summary>
        ///     Refresh the current item
        /// </summary>
        public override async Task RefreshAsync()
        {
            if (CurrentItem?.Id == null)
            {
                return;
            }

            await LoadItemByIdAsync(CurrentItem.Id.Value);
        }

        /// <summary>
        ///     Toggle watched status
        /// </summary>
        public virtual async Task ToggleWatchedAsync(bool isWatched)
        {
            Logger?.LogInformation(
                $"ToggleWatchedAsync called with isWatched={isWatched}, CurrentItem.Id={CurrentItem?.Id}, UserIdGuid={UserIdGuid}");

            if (CurrentItem?.Id == null)
            {
                Logger?.LogWarning("Cannot toggle watched - missing item ID");
                return;
            }

            var context = CreateErrorContext("ToggleWatched", ErrorCategory.User);

            // Store original values for reversion on error
            var originalWatched = IsWatched;
            var originalPlayed = HasBeenPlayed;

            try
            {
                // Update local state optimistically
                IsWatched = isWatched;
                HasBeenPlayed = isWatched;
                UpdatePlaybackState();
                OnPropertyChanged(nameof(IsWatched));
                OnPropertyChanged(nameof(WatchedButtonText));

                // Use UserDataService to toggle watched
                var updatedData = await UserDataService.ToggleWatchedAsync(CurrentItem.Id.Value, isWatched, UserIdGuid);

                if (updatedData != null)
                {
                    // Update with actual server state
                    IsWatched = updatedData.Played ?? false;
                    HasBeenPlayed = updatedData.Played ?? false;

                    if (CurrentItem.UserData != null)
                    {
                        CurrentItem.UserData.Played = updatedData.Played;
                        CurrentItem.UserData.PlaybackPositionTicks = updatedData.PlaybackPositionTicks;
                        CurrentItem.UserData.PlayedPercentage = updatedData.PlayedPercentage;
                    }

                    UpdatePlaybackState();
                    Logger?.LogInformation($"Successfully toggled watched to {updatedData.Played}");
                }
            }
            catch (Exception ex)
            {
                // Revert to original values on error
                IsWatched = originalWatched;
                HasBeenPlayed = originalPlayed;
                UpdatePlaybackState();
                await ErrorHandler.HandleErrorAsync(ex, context, true);
            }
        }

        /// <summary>
        ///     Load item from DTO
        /// </summary>
        protected virtual async Task LoadItemAsync(TItem item)
        {
            Logger?.LogInformation($"LoadItemAsync called with item: {item?.Name} (ID: {item?.Id})");

            // Always fetch complete item data to ensure we have all fields
            if (item?.Id != null)
            {
                Logger?.LogInformation("Fetching complete item data from API");
                await LoadItemByIdAsync(item.Id.Value);
            }
            else
            {
                Logger?.LogWarning("Item ID is null, using provided item data as-is");
                CurrentItem = item;
                Logger?.LogInformation("CurrentItem set successfully");

                // Update basic properties
                Title = item.Name;
                Overview = item.Overview;
                Logger?.LogInformation($"Basic properties set - Title: {Title}");

                // Update user data
                if (item.UserData != null)
                {
                    IsFavorite = item.UserData.IsFavorite ?? false;
                    FavoriteButtonText = IsFavorite ? "Unfavorite" : "Favorite";
                    HasBeenPlayed = item.UserData.Played ?? false;
                    IsWatched = HasBeenPlayed;
                    PlayedPercentage = item.UserData.PlayedPercentage ?? 0;
                    CanResume = item.UserData.PlaybackPositionTicks > 0;
                    Logger?.LogInformation(
                        $"User data updated - IsFavorite: {IsFavorite}, IsWatched: {IsWatched}, CanResume: {CanResume}");
                }
                else
                {
                    Logger?.LogInformation("No user data available for item");
                }

                // Update playback state
                UpdatePlaybackState(); await LoadImagesAsync();

                // Load additional data specific to the item type
                await LoadAdditionalDataAsync();

                Logger?.LogInformation($"LoadItemAsync completed. Final PlayButtonText: '{PlayButtonText}'");
            }
        }

        /// <summary>
        ///     Load item by ID from API
        /// </summary>
        protected virtual async Task LoadItemByIdAsync(Guid itemId)
        {
            if (!UserIdGuid.HasValue)
            {
                Logger?.LogError("Cannot load item - no user ID");
                return;
            }

            var response = await ApiClient.Items[itemId].GetAsync(config =>
            {
                config.QueryParameters.UserId = UserIdGuid.Value;
                // Note: The individual item endpoint returns all fields by default, Fields parameter is not supported
            });

            if (response is TItem typedItem)
            {
                CurrentItem = typedItem;
                Logger?.LogInformation("CurrentItem set from API response");

                // Update basic properties
                Title = typedItem.Name;
                Overview = typedItem.Overview;
                Logger?.LogInformation($"Basic properties set - Title: {Title}");

                // Update user data
                if (typedItem.UserData != null)
                {
                    IsFavorite = typedItem.UserData.IsFavorite ?? false;
                    FavoriteButtonText = IsFavorite ? "Unfavorite" : "Favorite";
                    HasBeenPlayed = typedItem.UserData.Played ?? false;
                    IsWatched = HasBeenPlayed;
                    PlayedPercentage = typedItem.UserData.PlayedPercentage ?? 0;
                    CanResume = typedItem.UserData.PlaybackPositionTicks > 0;
                    Logger?.LogInformation(
                        $"User data updated - IsFavorite: {IsFavorite}, IsWatched: {IsWatched}, CanResume: {CanResume}");
                }

                // Update playback state
                UpdatePlaybackState(); await LoadImagesAsync();

                // Load additional data specific to the item type
                await LoadAdditionalDataAsync();

                Logger?.LogInformation("LoadItemByIdAsync completed");
            }
            else
            {
                Logger?.LogError($"Loaded item is not of expected type {typeof(TItem).Name}");
            }
        }

        /// <summary>
        ///     Get required fields for API query
        /// </summary>
        protected virtual ItemFields[] GetRequiredFields()
        {
            return new[]
            {
                ItemFields.Overview, ItemFields.PrimaryImageAspectRatio, ItemFields.MediaSources,
                ItemFields.MediaStreams, ItemFields.People, ItemFields.Studios, ItemFields.Genres
            };
        }

        /// <summary>
        ///     Load images for the item
        /// </summary>
        protected virtual async Task LoadImagesAsync()
        {
            if (CurrentItem?.Id == null)
            {
                return;
            }

            // Load primary image
            if (CurrentItem.ImageTags?.AdditionalData?.ContainsKey(ImageType.Primary.ToString()) == true)
            {
                var imageTag = CurrentItem.ImageTags.AdditionalData[ImageType.Primary.ToString()]?.ToString();
                if (!string.IsNullOrEmpty(imageTag))
                {
                    PrimaryImage = await ImageLoadingService.GetImageAsync(
                        CurrentItem.Id.Value,
                        ImageType.Primary,
                        imageTag) as BitmapImage;
                }
            }

            // Load backdrop image
            if (CurrentItem.BackdropImageTags?.Count > 0)
            {
                BackdropImage = await ImageLoadingService.GetImageAsync(
                    CurrentItem.Id.Value,
                    ImageType.Backdrop,
                    CurrentItem.BackdropImageTags[0]) as BitmapImage;
            }
        }

        /// <summary>
        ///     Load additional data specific to the item type
        /// </summary>
        protected abstract Task LoadAdditionalDataAsync();

        /// <summary>
        ///     Update playback-related state
        /// </summary>
        protected virtual void UpdatePlaybackState()
        {
            CanPlay = CurrentItem != null;

            if (CurrentItem?.UserData != null)
            {
                var hasProgress = CurrentItem.UserData.PlaybackPositionTicks > 0;
                CanResume = hasProgress && !IsWatched;
                HasProgress = hasProgress;

                Logger?.LogInformation(
                    $"UpdatePlaybackState - PlaybackPositionTicks: {CurrentItem.UserData.PlaybackPositionTicks}, HasProgress: {hasProgress}, CanResume: {CanResume}, IsWatched: {IsWatched}");

                if (hasProgress && CurrentItem.UserData.PlayedPercentage.HasValue)
                {
                    WatchedPercentageText = $"{Math.Round(CurrentItem.UserData.PlayedPercentage.Value)}% watched";
                }

                if (CanResume)
                {
                    var position = TimeSpan.FromTicks(CurrentItem.UserData.PlaybackPositionTicks ?? 0);

                    // Format the resume time using the FormatTime helper
                    var resumeTime = TimeFormattingHelper.FormatTime(position);

                    PlayButtonText = $"Resume ({resumeTime})";
                    ResumeButtonText = $"Resume from {resumeTime}";
                    Logger?.LogInformation($"Set PlayButtonText to: '{PlayButtonText}'");
                }
                else
                {
                    PlayButtonText = "Play";
                    Logger?.LogInformation($"Set PlayButtonText to 'Play' - CanResume: {CanResume}");
                }
            }
            else
            {
                HasProgress = false;
                PlayButtonText = "Play";
                Logger?.LogInformation("Set PlayButtonText to 'Play' - No UserData");
            }
        }

        /// <summary>
        ///     Restart the item from the beginning
        /// </summary>
        public virtual async Task RestartAsync()
        {
            if (CurrentItem == null || MediaPlaybackService == null)
            {
                return;
            }

            var context = CreateErrorContext("RestartMedia", ErrorCategory.Media);
            try
            {
                await MediaPlaybackService.PlayMediaAsync(CurrentItem, 0);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, true);
            }
        }

        partial void OnIsWatchedChanged(bool value)
        {
            WatchedButtonText = value ? "Mark Unwatched" : "Mark Watched";
        }
    }
}
