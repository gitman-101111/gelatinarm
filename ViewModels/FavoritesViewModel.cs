using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml;

namespace Gelatinarm.ViewModels
{
    public class FavoritesViewModel : BaseViewModel, IDisposable
    {
        private readonly ObservableCollection<BaseItemDto> _allFavorites;
        private readonly JellyfinApiClient _apiClient;
        private readonly IMediaDiscoveryService _mediaDiscoveryService;
        private readonly INavigationService _navigationService;
        private readonly IUserProfileService _userProfileService;

        private string _currentFilter = "All";

        private string _emptyStateMessage = "Add items to your favorites by selecting them and clicking the heart icon";

        private string _emptyStateTitle = "No favorites yet";

        private Visibility _emptyStateVisibility = Visibility.Collapsed;
        private ObservableCollection<BaseItemDto> _favoriteItems;

        public FavoritesViewModel(JellyfinApiClient apiClient, INavigationService navigationService,
            IUserProfileService userProfileService, IMediaDiscoveryService mediaDiscoveryService,
            ILogger<FavoritesViewModel> logger) : base(logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
            _mediaDiscoveryService =
                mediaDiscoveryService ?? throw new ArgumentNullException(nameof(mediaDiscoveryService));
            _favoriteItems = new ObservableCollection<BaseItemDto>();
            _allFavorites = new ObservableCollection<BaseItemDto>();

            ItemClickCommand = new RelayCommand<BaseItemDto>(item => _navigationService?.NavigateToItemDetails(item));
        }

        public ObservableCollection<BaseItemDto> FavoriteItems
        {
            get => _favoriteItems;
            set => SetProperty(ref _favoriteItems, value);
        }

        public string CurrentFilter
        {
            get => _currentFilter;
            set
            {
                if (SetProperty(ref _currentFilter, value))
                {
                    // UpdateEmptyState might modify UI properties, so ensure it runs on UI thread
                    AsyncHelper.FireAndForget(async () => await RunOnUIThreadAsync(() => UpdateEmptyState()));
                }
            }
        }

        public Visibility EmptyStateVisibility
        {
            get => _emptyStateVisibility;
            set => SetProperty(ref _emptyStateVisibility, value);
        }

        public string EmptyStateTitle
        {
            get => _emptyStateTitle;
            set => SetProperty(ref _emptyStateTitle, value);
        }

        public string EmptyStateMessage
        {
            get => _emptyStateMessage;
            set => SetProperty(ref _emptyStateMessage, value);
        }

        public ICommand ItemClickCommand { get; }

        public async Task LoadFavoritesAsync()
        {
            // Use the standardized LoadDataAsync pattern
            await LoadDataAsync(true);
        }

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            if (_userProfileService == null)
            {
                throw new InvalidOperationException("User profile service is not available.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var userIdString = _userProfileService.CurrentUserId;
            if (string.IsNullOrEmpty(userIdString) || !Guid.TryParse(userIdString, out var userIdGuid))
            {
                throw new InvalidOperationException("Invalid or missing user ID");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var result = await _apiClient.Items.GetAsync(config =>
            {
                config.QueryParameters.UserId = userIdGuid;
                config.QueryParameters.IsFavorite = true;
                config.QueryParameters.Recursive = true;
                config.QueryParameters.SortBy = new[] { ItemSortBy.SortName };
                config.QueryParameters.SortOrder = new[] { SortOrder.Ascending };
                config.QueryParameters.EnableUserData = true;
                config.QueryParameters.Fields = new[]
                {
                    ItemFields.Overview, ItemFields.PrimaryImageAspectRatio, ItemFields.ChildCount
                };
            }, cancellationToken).ConfigureAwait(false);

            var favoriteItemsCollection = result?.Items;

            if (_allFavorites != null)
            {
                await RunOnUIThreadAsync(() =>
                {
                    _allFavorites.ReplaceAll(favoriteItemsCollection?.Where(item => item != null) ??
                                             Enumerable.Empty<BaseItemDto>());
                });
            }
            else
            {
                Logger?.LogError("_allFavorites collection is null");
            }

            await ApplyFilterAsync(CurrentFilter).ConfigureAwait(false);

            await RunOnUIThreadAsync(() => UpdateEmptyState());
        }

        public async Task ApplyFilterAsync(string filter)
        {
            var context = CreateErrorContext("ApplyFilter");
            try
            {
                CurrentFilter = filter ?? "All";

                if (FavoriteItems != null)
                {
                    await RunOnUIThreadAsync(() => FavoriteItems.Clear());
                }
                else
                {
                    Logger?.LogError("FavoriteItems collection is null in ApplyFilterAsync");
                    return;
                }

                if (_allFavorites == null)
                {
                    Logger?.LogError("_allFavorites collection is null in ApplyFilterAsync");
                    await RunOnUIThreadAsync(() => UpdateEmptyState());
                    return;
                }

                IEnumerable<BaseItemDto> filteredItems = _allFavorites;

                switch (filter)
                {
                    case "Movies":
                        filteredItems = _allFavorites.Where(x => x.Type == BaseItemDto_Type.Movie);
                        break;
                    case "Shows":
                        filteredItems = _allFavorites.Where(x =>
                            x.Type == BaseItemDto_Type.Series || x.Type == BaseItemDto_Type.Episode);
                        break;
                    case "Music":
                        filteredItems = _allFavorites.Where(x =>
                            x.Type == BaseItemDto_Type.MusicAlbum || x.Type == BaseItemDto_Type.Audio ||
                            x.Type == BaseItemDto_Type.MusicArtist);
                        break;
                    case "All":
                    default:
                        // Show all items
                        break;
                }

                var itemsToAdd = filteredItems.Where(item => item != null).ToList();

                await RunOnUIThreadAsync(() =>
                {
                    FavoriteItems.ReplaceAll(itemsToAdd);
                    UpdateEmptyState();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        private void UpdateEmptyState()
        {
            var context = CreateErrorContext("UpdateEmptyState", ErrorCategory.User);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    EmptyStateVisibility = (FavoriteItems?.Count ?? 0) == 0 ? Visibility.Visible : Visibility.Collapsed;

                    switch (CurrentFilter)
                    {
                        case "Movies":
                            EmptyStateTitle = "No favorite movies";
                            EmptyStateMessage = "Mark movies as favorites to see them here";
                            break;
                        case "Shows":
                            EmptyStateTitle = "No favorite TV shows";
                            EmptyStateMessage = "Mark TV shows as favorites to see them here";
                            break;
                        case "Music":
                            EmptyStateTitle = "No favorite music";
                            EmptyStateMessage = "Mark albums or songs as favorites to see them here";
                            break;
                        case "All":
                        default:
                            EmptyStateTitle = "No favorites yet";
                            EmptyStateMessage =
                                "Add items to your favorites by selecting them and clicking the heart icon";
                            break;
                    }
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        protected override async Task RefreshDataCoreAsync()
        {
            // Refresh just reloads all data
            await LoadDataCoreAsync(DisposalCts.Token);
        }

        protected override async Task ClearDataCoreAsync()
        {
            await RunOnUIThreadAsync(() =>
            {
                _allFavorites?.Clear();
                FavoriteItems?.Clear();
                UpdateEmptyState();
            });
        }

        #region IDisposable Implementation

        protected override void DisposeManaged()
        {
            Logger?.LogInformation("FavoritesViewModel disposed");

            base.DisposeManaged();
        }

        #endregion
    }
}
