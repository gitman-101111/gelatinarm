using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using CommunityToolkit.Mvvm.ComponentModel;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Favorites
{
    public partial class FavoritesViewModel : BaseViewModel
    {
        private readonly List<BaseItemDto> _allFavorites = new();
        private readonly JellyfinApiClient _apiClient;
        private readonly IUserProfileService _userProfileService;

        private string _currentFilter = "All";

        // Set with the visibility, once a load has shown whether there is anything
        [ObservableProperty] private string _emptyStateMessage;

        [ObservableProperty] private string _emptyStateTitle;

        [ObservableProperty] private Visibility _emptyStateVisibility = Visibility.Collapsed;

        public FavoritesViewModel(JellyfinApiClient apiClient, IUserProfileService userProfileService,
            ILogger<FavoritesViewModel> logger) : base(logger)
        {
            _apiClient = apiClient;
            _userProfileService = userProfileService;
        }

        public ObservableCollection<BaseItemDto> FavoriteItems { get; } = new();

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            var userIdGuid = _userProfileService.GetCurrentUserGuid();
            if (!userIdGuid.HasValue)
            {
                throw new InvalidOperationException("Invalid or missing user ID");
            }

            var result = await _apiClient.Items.GetAsync(config =>
            {
                config.QueryParameters.UserId = userIdGuid.Value;
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

            await RunOnUIThreadAsync(() =>
            {
                _allFavorites.Clear();
                _allFavorites.AddRange(result?.Items?.Where(item => item != null) ?? Enumerable.Empty<BaseItemDto>());
            });

            await ApplyFilterAsync(_currentFilter).ConfigureAwait(false);
        }

        public async Task ApplyFilterAsync(string filter)
        {
            var context = CreateErrorContext("ApplyFilter");
            try
            {
                _currentFilter = filter ?? "All";

                IEnumerable<BaseItemDto> filteredItems = _allFavorites;

                switch (_currentFilter)
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
                }

                var itemsToAdd = filteredItems.ToList();

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
            EmptyStateVisibility = FavoriteItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            (EmptyStateTitle, EmptyStateMessage) = _currentFilter switch
            {
                "Movies" => ("No favorite movies", "Mark movies as favorites to see them here"),
                "Shows" => ("No favorite TV shows", "Mark TV shows as favorites to see them here"),
                "Music" => ("No favorite music", "Mark albums or songs as favorites to see them here"),
                _ => ("No favorites yet",
                    "Add items to your favorites by selecting them and clicking the heart icon")
            };
        }

        protected override Task RefreshDataCoreAsync()
        {
            return LoadDataCoreAsync(DisposalCts.Token);
        }
    }
}
