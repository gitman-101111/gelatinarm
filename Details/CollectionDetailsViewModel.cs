using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Music;
using Gelatinarm.Player;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Details
{
    public partial class CollectionDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        private readonly List<BaseItemDto> _playableItems = new();

        [ObservableProperty] private bool _isPlayButtonVisible;

        [ObservableProperty] private string _itemCountText;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _items = new();

        public CollectionDetailsViewModel(
            ILogger<CollectionDetailsViewModel> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IUserDataService userDataService) : base(
            logger,
            apiClient,
            userProfileService,
            navigationService,
            imageLoadingService,
            userDataService)
        {
        }

        public override Task InitializeAsync(object parameter)
        {
            return InitializeFromParameterAsync(parameter, "InitializeCollection", LoadCollectionFromDtoAsync, LoadCollectionByIdAsync);
        }

        public override async Task RefreshAsync()
        {
            if (CurrentItem?.Id == null)
            {
                return;
            }

            var context = CreateErrorContext("RefreshCollection");
            try
            {
                await LoadCollectionByIdAsync(CurrentItem.Id.Value, CancellationToken.None);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private async Task LoadCollectionFromDtoAsync(BaseItemDto dto, CancellationToken cancellationToken)
        {
            Logger.LogDebug("CollectionDetailsViewModel: Loading from BaseItemDto: {DtoName}", dto.Name);

            // A list's copy may lack the overview; the album and artist pages refetch the same way
            if (dto.Id.HasValue)
            {
                await LoadCollectionByIdAsync(dto.Id.Value, cancellationToken);
                return;
            }

            CurrentItem = dto;
            await LoadCollectionDetailsAsync(cancellationToken);
        }

        private async Task LoadCollectionByIdAsync(Guid itemId, CancellationToken cancellationToken)
        {
            Logger.LogDebug("CollectionDetailsViewModel: Loading collection by ID: {ItemId}", itemId);

            CurrentItem = await FetchRequiredItemAsync(itemId, cancellationToken);
            await LoadCollectionDetailsAsync(cancellationToken);
        }

        private async Task LoadCollectionDetailsAsync(CancellationToken cancellationToken)
        {
            if (!UserIdGuid.HasValue)
            {
                return;
            }

            await RunOnUIThreadAsync(UpdateCollectionUi);
            await LoadCollectionItemsAsync(cancellationToken);

            Logger.LogDebug(
                "CollectionDetailsViewModel: Loaded collection: {CurrentItemName} with {ItemsCount} items", CurrentItem.Name, Items.Count);
        }

        private void UpdateCollectionUi()
        {
            Title = CurrentItem.Name ?? string.Empty;
            Overview = CurrentItem.Overview;

            LoadPrimaryImage();

            UpdateFavoriteState();

            // Shown once the items are in and some of them play
            IsPlayButtonVisible = false;
        }

        private async Task LoadCollectionItemsAsync(CancellationToken cancellationToken)
        {
            var context = CreateErrorContext("LoadCollectionItems");
            try
            {
                var response = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.ParentId = CurrentItem.Id.Value;
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.Fields = new[] { ItemFields.PrimaryImageAspectRatio };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                }, cancellationToken).ConfigureAwait(false);

                var items = response?.Items ?? new List<BaseItemDto>();
                await RunOnUIThreadAsync(() =>
                {
                    Items.ReplaceAll(items);
                    _playableItems.Clear();
                    _playableItems.AddRange(items.Where(item =>
                        item.Type == BaseItemDto_Type.Movie || item.Type == BaseItemDto_Type.Episode));

                    ItemCountText = Items.Count switch
                    {
                        0 => null,
                        1 => "1 item",
                        _ => $"{Items.Count} items"
                    };
                    IsPlayButtonVisible = _playableItems.Count > 0;
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public override async Task PlayAsync()
        {
            var items = _playableItems.ToList();
            PlayAsQueue(items, await FirstUnwatchedIndexAsync(items));
        }

        [RelayCommand]
        private void ShufflePlay()
        {
            PlayAsQueue(ShuffleHelper.Shuffled(_playableItems), 0);
        }

        /// <summary>
        ///     Where Play starts, as Play on a series does: at the first item not yet watched, the
        ///     whole collection staying queued around it. The watched state is fetched now, since
        ///     the list on the page may have loaded before the last playback was reported. The
        ///     first item when everything is watched.
        /// </summary>
        private async Task<int> FirstUnwatchedIndexAsync(List<BaseItemDto> items)
        {
            var context = CreateErrorContext("FirstUnwatchedIndex");
            try
            {
                // The page's own query, asked again now, with the watched state read here
                // rather than filtered by the server
                var response = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.ParentId = CurrentItem.Id.Value;
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                });

                var watched = new HashSet<Guid>((response?.Items ?? new List<BaseItemDto>())
                    .Where(item => item.Id.HasValue && item.UserData?.Played == true)
                    .Select(item => item.Id.Value));
                var index = items.FindIndex(item => item.Id.HasValue && !watched.Contains(item.Id.Value));
                index = Math.Max(index, 0);
                Logger.LogInformation(
                    "Collection Play: server reports {WatchedCount} watched, starting at {ItemName} ({Position} of {Count})",
                    watched.Count, items.ElementAtOrDefault(index)?.Name, index + 1, items.Count);
                return index;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, 0);
            }
        }

        private void PlayAsQueue(List<BaseItemDto> items, int startIndex)
        {
            if (items.Count == 0)
            {
                return;
            }

            var playbackParams = new MediaPlaybackParams
            {
                Item = items[startIndex],
                QueueItems = items,
                StartIndex = startIndex,
                NavigationSourcePage = typeof(CollectionDetailsPage),
                NavigationSourceParameter = CurrentItem
            };
            NavigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
        }
    }
}
