using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
    ///     ViewModel for the CollectionDetailsPage handling collection data and navigation
    /// </summary>
    public partial class CollectionDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        private readonly List<BaseItemDto> _playableItems = new();

        // Observable properties
        [ObservableProperty] private bool _isItemCountVisible;

        [ObservableProperty] private bool _isOverviewVisible;

        [ObservableProperty] private bool _isPlayButtonEnabled;

        [ObservableProperty] private bool _isPlayButtonVisible;

        [ObservableProperty] private string _itemCountText;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _items = new();

        private CancellationTokenSource _loadCts;

        [ObservableProperty] private string _playButtonText = "Play";

        [ObservableProperty] private BitmapImage _posterImage;

        public CollectionDetailsViewModel(
            ILogger<CollectionDetailsViewModel> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IMediaPlaybackService mediaPlaybackService,
            IUserDataService userDataService) : base(
            logger,
            apiClient,
            userProfileService,
            navigationService,
            imageLoadingService,
            mediaPlaybackService,
            userDataService)
        {
        }

        /// <summary>
        ///     Initialize the ViewModel with navigation parameter
        /// </summary>
        public override async Task InitializeAsync(object parameter)
        {
            if (parameter == null)
            {
                return;
            }

            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();

            var context = CreateErrorContext("InitializeCollection");
            try
            {
                IsLoading = true;
                try
                {
                    if (parameter is BaseItemDto dto)
                    {
                        await LoadCollectionFromDtoAsync(dto, _loadCts.Token);
                    }
                    else if (parameter is string guidString && Guid.TryParse(guidString, out var itemId))
                    {
                        await LoadCollectionByIdAsync(itemId, _loadCts.Token);
                    }
                    else if (parameter is Guid guid)
                    {
                        await LoadCollectionByIdAsync(guid, _loadCts.Token);
                    }
                }
                finally
                {
                    IsLoading = false;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        /// <summary>
        ///     Refresh collection data when navigating back
        /// </summary>
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
            Logger?.LogInformation($"CollectionDetailsViewModel: Loading from BaseItemDto: {dto.Name}");
            CurrentItem = dto;
            await LoadCollectionDetailsAsync(cancellationToken);
        }

        private async Task LoadCollectionByIdAsync(Guid itemId, CancellationToken cancellationToken)
        {
            Logger?.LogInformation($"CollectionDetailsViewModel: Loading collection by ID: {itemId}");

            if (!UserIdGuid.HasValue)
            {
                throw new InvalidOperationException("User ID not available");
            }

            var response = await ApiClient.Items[itemId].GetAsync(config =>
            {
                config.QueryParameters.UserId = UserIdGuid.Value;
            }, cancellationToken).ConfigureAwait(false);

            if (response == null)
            {
                throw new Exception($"Failed to load collection with ID {itemId}");
            }

            CurrentItem = response;
            await LoadCollectionDetailsAsync(cancellationToken);
        }

        private async Task LoadCollectionDetailsAsync(CancellationToken cancellationToken)
        {
            if (CurrentItem == null || !UserIdGuid.HasValue)
            {
                return;
            }
            await RunOnUIThreadAsync(() => UpdateCollectionUI()); await LoadCollectionItemsAsync(cancellationToken);

            Logger?.LogInformation(
                $"CollectionDetailsViewModel: Loaded collection: {CurrentItem.Name} with {Items.Count} items");
        }

        private void UpdateCollectionUI()
        {
            if (CurrentItem == null)
            {
                return;
            }

            // Collection title
            Title = CurrentItem.Name ?? string.Empty;

            // Overview
            if (!string.IsNullOrEmpty(CurrentItem.Overview))
            {
                Overview = CurrentItem.Overview;
                IsOverviewVisible = true;
            }
            else
            {
                IsOverviewVisible = false;
            }

            // Load collection image
            LoadCollectionImage();

            // Update favorite button
            UpdateFavoriteButton();

            // Update play button
            UpdatePlayButton();
        }


        private void UpdatePlayButton()
        {
            // Enable play button only if collection has playable items
            IsPlayButtonEnabled = false;
            IsPlayButtonVisible = false;
        }

        private void LoadCollectionImage()
        {
            if (CurrentItem?.Id == null)
            {
                return;
            }

            var context = CreateErrorContext("LoadCollectionImage", ErrorCategory.Media);
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
                        PosterImage = new BitmapImage(new Uri(imageUrl));
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private async Task LoadCollectionItemsAsync(CancellationToken cancellationToken)
        {
            if (CurrentItem?.Id == null || !UserIdGuid.HasValue)
            {
                return;
            }

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

                if (response?.Items != null)
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        Items.Clear();
                        _playableItems.Clear();

                        foreach (var item in response.Items)
                        {
                            Items.Add(item);

                            // Check if item is playable
                            if (item.Type == BaseItemDto_Type.Movie ||
                                item.Type == BaseItemDto_Type.Episode)
                            {
                                _playableItems.Add(item);
                            }
                        }

                        // Update item count
                        if (Items.Any())
                        {
                            ItemCountText = Items.Count == 1 ? "1 item" : $"{Items.Count} items";
                            IsItemCountVisible = true;
                        }
                        else
                        {
                            IsItemCountVisible = false;
                        }

                        // Enable play button if there are playable items
                        if (_playableItems.Any())
                        {
                            IsPlayButtonEnabled = true;
                            IsPlayButtonVisible = true;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        // Commands
        [RelayCommand]
        public override async Task PlayAsync()
        {
            if (!_playableItems.Any() || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("PlayCollection", ErrorCategory.Media);
            try
            {
                // For mixed content, play first item
                var firstItem = _playableItems.FirstOrDefault();
                if (firstItem == null)
                {
                    Logger?.LogWarning("No playable items found in collection");
                    return;
                }

                if (firstItem.Type == BaseItemDto_Type.Movie)
                {
                    NavigationService.Navigate(typeof(MediaPlayerPage), firstItem);
                }
                else if (firstItem.Type == BaseItemDto_Type.Episode)
                {
                    var playbackParams = new MediaPlaybackParams
                    {
                        ItemId = _playableItems[0].Id.Value.ToString(),
                        QueueItems = _playableItems,
                        StartIndex = 0,
                        StartPositionTicks = null,
                        NavigationSourcePage = typeof(CollectionDetailsPage),
                        NavigationSourceParameter = CurrentItem
                    };
                    NavigationService?.Navigate(typeof(MediaPlayerPage), playbackParams);
                }

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        [RelayCommand]
        private async Task ToggleFavoriteAsync()
        {
            if (CurrentItem?.Id == null || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("ToggleFavorite", ErrorCategory.User);
            try
            {
                var newFavoriteStatus = !IsFavorite;

                var updatedData =
                    await UserDataService.ToggleFavoriteAsync(CurrentItem.Id.Value, newFavoriteStatus, UserIdGuid);

                if (updatedData != null)
                {
                    if (CurrentItem.UserData == null)
                    {
                        CurrentItem.UserData = new UserItemDataDto();
                    }

                    CurrentItem.UserData.IsFavorite = updatedData.IsFavorite;

                    IsFavorite = updatedData.IsFavorite ?? false;
                    FavoriteButtonText = IsFavorite ? "Remove from Favorites" : "Add to Favorites";
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        [RelayCommand]
        private async Task ShufflePlayAsync()
        {
            if (!_playableItems.Any() || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("ShufflePlayCollection", ErrorCategory.Media);
            try
            {
                // Shuffle the items
                var random = new Random();
                var shuffledItems = _playableItems.OrderBy(x => random.Next()).ToList();
                
                if (shuffledItems.Count == 0)
                {
                    Logger?.LogWarning("No items to shuffle in collection");
                    return;
                }

                var playbackParams = new MediaPlaybackParams
                {
                    ItemId = shuffledItems[0].Id.Value.ToString(),
                    QueueItems = shuffledItems,
                    StartIndex = 0,
                    StartPositionTicks = null,
                    NavigationSourcePage = typeof(CollectionDetailsPage),
                    NavigationSourceParameter = CurrentItem
                };
                NavigationService?.Navigate(typeof(MediaPlayerPage), playbackParams);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
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

        /// <summary>
        ///     Clean up when navigating away
        /// </summary>
        public void Cleanup()
        {
            // Clear collections - if we're not on UI thread, this might throw but 
            // that's acceptable as Cleanup is typically called during page navigation
            // which should be on UI thread
            Items.Clear();
            _playableItems.Clear();
        }

        protected override void DisposeManaged()
        {
            base.DisposeManaged();
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }

        /// <summary>
        ///     Implementation of abstract method from DetailsViewModel
        /// </summary>
        protected override async Task LoadAdditionalDataAsync()
        {
            // Collection-specific loading is handled in LoadCollectionFromDtoAsync
            await Task.CompletedTask;
        }
    }
}
