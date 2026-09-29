using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Details
{
    /// <summary>
    ///     What DetailsPage needs from any details view model, whatever its item type
    /// </summary>
    public interface IItemDetailsViewModel
    {
        Task InitializeAsync(object parameter);
    }

    public abstract partial class DetailsViewModel<TItem> : BaseViewModel, IItemDetailsViewModel
        where TItem : BaseItemDto
    {
        protected readonly JellyfinApiClient ApiClient;
        protected readonly IImageLoadingService ImageLoadingService;
        protected readonly INavigationService NavigationService;
        protected readonly IUserDataService UserDataService;
        private readonly IUserProfileService _userProfileService;

        [ObservableProperty] private ImageSource _backdropImage;

        [ObservableProperty] private bool _canResume;

        [ObservableProperty] private TItem _currentItem;

        private CancellationTokenSource _loadCts;

        [ObservableProperty] private string _favoriteButtonText = "Favorite";

        [ObservableProperty] private bool _isFavorite;

        [ObservableProperty] private bool _isWatched;

        [ObservableProperty] private string _overview;

        [ObservableProperty] private bool _isBioExpanded;
        [ObservableProperty] private bool _isExpandBioButtonVisible;
        [ObservableProperty] private string _expandBioButtonText = "Show More";
        [ObservableProperty] private double _overviewMaxHeight = double.PositiveInfinity;
        private double _collapsedOverviewHeight;

        [ObservableProperty] private string _playButtonText = "Play";

        [ObservableProperty] private double _playedPercentage;

        [ObservableProperty] private ImageSource _primaryImage;

        [ObservableProperty] private string _rating;

        [ObservableProperty] private string _runtime;

        [ObservableProperty] private string _title;

        [ObservableProperty] private string _watchedButtonText = WatchedButtonLabel(false);

        [ObservableProperty] private string _year;

        protected DetailsViewModel(
            ILogger logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IUserDataService userDataService) : base(logger)
        {
            ApiClient = apiClient;
            _userProfileService = userProfileService;
            NavigationService = navigationService;
            ImageLoadingService = imageLoadingService;
            UserDataService = userDataService;
            PlayCommand = new AsyncRelayCommand(PlayAsync);
            ResumeCommand = new AsyncRelayCommand(ResumeAsync);
            RestartCommand = new AsyncRelayCommand(RestartAsync);
        }

        [RelayCommand]
        private Task ToggleFavoriteAsync()
        {
            return SetFavoriteAsync(!IsFavorite);
        }

        [RelayCommand]
        private Task ToggleWatchedAsync()
        {
            return ToggleWatchedCoreAsync();
        }

        // The season page marks its series or the selected episode instead
        protected virtual Task ToggleWatchedCoreAsync()
        {
            return SetWatchedAsync(!IsWatched);
        }

        public IAsyncRelayCommand PlayCommand { get; }
        public IAsyncRelayCommand ResumeCommand { get; }
        public IAsyncRelayCommand RestartCommand { get; }

        // Read per use: Artist, Collection and Season pages are cached, and a copy taken at
        // construction would outlive a profile switch.
        protected Guid? UserIdGuid => _userProfileService.GetCurrentUserGuid();

        // What the Favorite button marks: the page's item, or on the season page its series
        protected virtual BaseItemDto FavoriteTarget => CurrentItem;

        private async Task SetFavoriteAsync(bool isFavorite)
        {
            var target = FavoriteTarget;

            if (target?.Id == null)
            {
                Logger.LogWarning("Cannot toggle favorite - missing item ID");
                return;
            }

            var context = CreateErrorContext("ToggleFavorite", ErrorCategory.User);

            var originalValue = IsFavorite;

            try
            {
                // Update local state optimistically
                IsFavorite = isFavorite;

                var updatedData =
                    await UserDataService.ToggleFavoriteAsync(target.Id.Value, isFavorite);

                if (updatedData != null)
                {
                    IsFavorite = updatedData.IsFavorite == true;
                    if (target.UserData != null)
                    {
                        target.UserData.IsFavorite = updatedData.IsFavorite;
                    }

                    Logger.LogDebug("Successfully toggled favorite to {UpdatedDataIsFavorite}", updatedData.IsFavorite);
                }
            }
            catch (Exception ex)
            {
                IsFavorite = originalValue;
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        /// <summary>
        ///     Syncs IsFavorite from the current item; the button text follows via OnIsFavoriteChanged
        /// </summary>
        protected void UpdateFavoriteState()
        {
            if (CurrentItem?.UserData != null)
            {
                IsFavorite = CurrentItem.UserData.IsFavorite == true;
            }
        }

        /// <summary>
        ///     Loads the item's primary image (poster, cover, portrait) into PrimaryImage
        /// </summary>
        protected void LoadPrimaryImage()
        {
            var item = CurrentItem;
            FireAndForget(() => ImageLoadingService.LoadImageIntoTargetAsync(
                item, ImageConstants.ImageTypePrimary, image => PrimaryImage = image, 400));
        }

        /// <summary>
        ///     Loads <paramref name="item" />'s backdrop into BackdropImage; a season shows its series'
        /// </summary>
        protected Task LoadBackdropImageAsync(BaseItemDto item)
        {
            return ImageLoadingService.LoadImageIntoTargetAsync(
                item, ImageConstants.ImageTypeBackdrop, image => BackdropImage = image, 1920, 1080);
        }

        public virtual Task InitializeAsync(object parameter)
        {
            return InitializeFromParameterAsync(parameter, "InitializeViewModel",
                (item, _) => LoadItemAsync((TItem)item), (itemId, _) => LoadItemByIdAsync(itemId));
        }

        /// <summary>
        ///     Starts a new load (cancelling the previous one) of the item a navigation parameter names:
        ///     an item goes to <paramref name="fromItem" />, an ID (Guid or string) to <paramref name="byId" />.
        /// </summary>
        protected async Task InitializeFromParameterAsync(object parameter, string operation,
            Func<BaseItemDto, CancellationToken, Task> fromItem, Func<Guid, CancellationToken, Task> byId)
        {
            var loadToken = StartNewLoad();
            var context = CreateErrorContext(operation);
            IsLoading = true;
            try
            {
                if (parameter is TItem item)
                {
                    await fromItem(item, loadToken);
                }
                else if (TryGetGuidFromParameter(parameter, out var itemId))
                {
                    await byId(itemId, loadToken);
                }
                else
                {
                    Logger.LogError("{Operation}: unexpected parameter type {ParameterType}", operation,
                        parameter.GetType().Name);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
            finally
            {
                IsLoading = false;
            }
        }

        // Each page with a Play, Resume or Restart button plays its own way (versions and tracks,
        // an episode queue, an album); a page without one (a person) keeps these defaults
        public virtual Task PlayAsync()
        {
            return Task.CompletedTask;
        }

        public virtual Task ResumeAsync()
        {
            return Task.CompletedTask;
        }

        public virtual Task RestartAsync()
        {
            return Task.CompletedTask;
        }

        public override async Task RefreshAsync()
        {
            if (CurrentItem?.Id == null)
            {
                return;
            }

            await LoadItemByIdAsync(CurrentItem.Id.Value);
        }

        private async Task SetWatchedAsync(bool isWatched)
        {
            if (CurrentItem?.Id == null)
            {
                Logger.LogWarning("Cannot toggle watched - missing item ID");
                return;
            }

            var context = CreateErrorContext("ToggleWatched", ErrorCategory.User);

            var originalWatched = IsWatched;

            try
            {
                // Update local state optimistically
                IsWatched = isWatched;
                UpdatePlaybackState();

                var updatedData = await UserDataService.ToggleWatchedAsync(CurrentItem.Id.Value, isWatched);

                if (updatedData != null)
                {
                    IsWatched = updatedData.Played == true;

                    if (CurrentItem.UserData != null)
                    {
                        CurrentItem.UserData.Played = updatedData.Played;
                        CurrentItem.UserData.PlaybackPositionTicks = updatedData.PlaybackPositionTicks;
                        CurrentItem.UserData.PlayedPercentage = updatedData.PlayedPercentage;
                    }

                    UpdatePlaybackState();
                    Logger.LogDebug("Successfully toggled watched to {UpdatedDataPlayed}", updatedData.Played);
                }
            }
            catch (Exception ex)
            {
                IsWatched = originalWatched;
                UpdatePlaybackState();
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        private async Task LoadItemAsync(TItem item)
        {
            // Always fetch complete item data to ensure we have all fields
            if (item?.Id != null)
            {
                await LoadItemByIdAsync(item.Id.Value);
            }
            else
            {
                Logger.LogWarning("Item ID is null, using provided item data as-is");
                await ApplyItemAsync(item);
            }
        }

        private async Task LoadItemByIdAsync(Guid itemId)
        {
            if (!UserIdGuid.HasValue)
            {
                Logger.LogError("Cannot load item - no user ID");
                return;
            }

            var response = await FetchItemAsync(itemId);

            if (response is TItem typedItem)
            {
                await ApplyItemAsync(typedItem);
            }
            else
            {
                Logger.LogError("Loaded item is not of expected type {TItem}", typeof(TItem).Name);
            }
        }

        /// <summary>
        ///     Cancels a load still running for the previous item and returns the token for the new one
        /// </summary>
        protected CancellationToken StartNewLoad()
        {
            return AsyncHelper.Supersede(ref _loadCts).Token;
        }

        /// <summary>
        ///     The token of the load <see cref="StartNewLoad" /> last started.
        /// </summary>
        protected CancellationToken CurrentLoadToken => _loadCts?.Token ?? CancellationToken.None;

        protected void CancelLoad()
        {
            _loadCts?.Cancel();
        }

        protected override void DisposeManaged()
        {
            base.DisposeManaged();
            AsyncHelper.Cancel(ref _loadCts);
        }

        /// <summary>
        ///     The current user's copy of one item (with its user data). The item endpoint
        ///     returns all fields; it takes no Fields parameter.
        /// </summary>
        protected Task<BaseItemDto> FetchItemAsync(Guid itemId, CancellationToken cancellationToken = default)
        {
            return ApiClient.Items[itemId].GetAsync(config => config.QueryParameters.UserId = UserIdGuid.Value,
                cancellationToken);
        }

        /// <summary>
        ///     <see cref="FetchItemAsync" />, for pages that have nothing to show without the item.
        /// </summary>
        protected async Task<BaseItemDto> FetchRequiredItemAsync(Guid itemId, CancellationToken cancellationToken)
        {
            if (!UserIdGuid.HasValue)
            {
                throw new InvalidOperationException("User ID not available");
            }

            return await FetchItemAsync(itemId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Failed to load item {itemId}");
        }

        private async Task ApplyItemAsync(TItem item)
        {
            CurrentItem = item;
            Title = item.Name;
            Overview = item.Overview;

            if (item.UserData != null)
            {
                IsFavorite = item.UserData.IsFavorite == true;
                IsWatched = item.UserData.Played == true;
                PlayedPercentage = item.UserData.PlayedPercentage ?? 0;
            }

            UpdatePlaybackState();
            await LoadAdditionalDataAsync();
        }

        // What a page shows beyond the shared fields, once the item is applied. Only the movie
        // page loads it this way; the others load theirs as they fetch the item.
        protected virtual Task LoadAdditionalDataAsync()
        {
            return Task.CompletedTask;
        }

        protected void UpdatePlaybackState()
        {
            if (CurrentItem?.UserData != null)
            {
                // Resume follows the saved position alone: an item with one shows Resume even
                // if it is flagged watched (a re-watch). The server resets the position of a
                // finished item to 0, so it shows Play.
                CanResume = CurrentItem.UserData.PlaybackPositionTicks > 0;
            }
            else
            {
                CanResume = false;
            }

            // Plain "Resume" on every details page; the position is already shown by the progress
            // bar over the artwork
            PlayButtonText = CanResume ? "Resume" : "Play";
            Logger.LogDebug(
                "Playback state: position {PlaybackPositionTicks}, watched {IsWatched}, button {PlayButtonText}",
                CurrentItem?.UserData?.PlaybackPositionTicks, IsWatched, PlayButtonText);
        }

        /// <summary>
        ///     Collapses the overview to <paramref name="collapsedHeight" />, with a Show More button,
        ///     when it runs past <paramref name="clampAfterChars" /> characters; otherwise shows it whole.
        /// </summary>
        protected void ClampOverview(string overview, int clampAfterChars, double collapsedHeight)
        {
            _collapsedOverviewHeight = collapsedHeight;
            IsBioExpanded = false;
            IsExpandBioButtonVisible = overview?.Length > clampAfterChars;
            OverviewMaxHeight = IsExpandBioButtonVisible ? collapsedHeight : double.PositiveInfinity;
        }

        [RelayCommand]
        private void NavigateToItem(BaseItemDto item)
        {
            if (item == null)
            {
                return;
            }

            NavigationService.NavigateToItemDetails(item);
        }

        [RelayCommand]
        private void ToggleBioExpansion()
        {
            IsBioExpanded = !IsBioExpanded;
        }

        partial void OnIsBioExpandedChanged(bool value)
        {
            OverviewMaxHeight = value ? double.PositiveInfinity : _collapsedOverviewHeight;
            ExpandBioButtonText = value ? "Show Less" : "Show More";
        }

        protected static string WatchedButtonLabel(bool watched)
        {
            return watched ? "Mark Unwatched" : "Mark Watched";
        }

        partial void OnIsWatchedChanged(bool value)
        {
            WatchedButtonText = WatchedButtonLabel(value);
        }

        partial void OnIsFavoriteChanged(bool value)
        {
            FavoriteButtonText = value ? "Unfavorite" : "Favorite";
        }
    }
}
