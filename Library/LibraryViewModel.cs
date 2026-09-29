using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Gelatinarm.Music;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Items;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using static Gelatinarm.Library.LibraryConstants;

namespace Gelatinarm.Library
{
    public partial class LibraryViewModel : BaseViewModel
    {
        private static readonly string[] VideoResolutions = { "4K", "1080p", "720p", "SD" };

        private readonly JellyfinApiClient _apiClient;
        private readonly JellyfinRequestAdapter _requestAdapter;
        private readonly IUserProfileService _userProfileService;
        private readonly IMusicPlayerService _musicPlayerService;
        private CancellationTokenSource _applyFiltersCts;
        private int? _cachedActiveFilterCount;

        private string _currentFilter = "All";

        // Written by UpdateEmptyState before the empty state can show
        [ObservableProperty] private string _emptyStateMessage;
        [ObservableProperty] private string _emptyStateTitle;

        private bool _hasLoadedOnce;

        private bool _isAscending = true;

        private CancellationTokenSource _loadFiltersCts;

        private BaseItemDto _selectedLibrary;
        private int _selectedSortIndex;

        [ObservableProperty] private bool _showGenreFilter = true;
        [ObservableProperty] private bool _showPlayedStatusFilter = true;
        [ObservableProperty] private bool _showRatingFilter = true;
        [ObservableProperty] private bool _showResolutionFilter = true;
        [ObservableProperty] private bool _showYearFilter = true;

        private int _totalItemCount;

        public LibraryViewModel(
            JellyfinApiClient apiClient,
            JellyfinRequestAdapter requestAdapter,
            IUserProfileService userProfileService,
            IMusicPlayerService musicPlayerService,
            ILogger<LibraryViewModel> logger)
            : base(logger)
        {
            _musicPlayerService = musicPlayerService;
            _apiClient = apiClient;
            _requestAdapter = requestAdapter;
            _userProfileService = userProfileService;

            // Loading from the start, or the empty state would show before the first load
            IsLoading = true;

            foreach (var options in FilterOptionLists)
            {
                options.CollectionChanged += OnFilterOptionsChanged;
            }
        }

        public ObservableCollection<BaseItemDto> MediaItems { get; } = new();
        public ObservableCollection<FilterItem> Genres { get; } = new();
        public ObservableCollection<FilterItem> Years { get; } = new();
        public ObservableCollection<DecadeFilterItem> Decades { get; } = new();
        public ObservableCollection<FilterItem> Ratings { get; } = new();
        public ObservableCollection<FilterItem> Resolutions { get; } = new();
        public ObservableCollection<FilterItem> PlayedStatuses { get; } = new();

        /// <summary>
        ///     Every filter option on the page, decade shortcuts included
        /// </summary>
        private IEnumerable<INotifyCollectionChanged> FilterOptionLists =>
            new INotifyCollectionChanged[] { Genres, Years, Decades, Ratings, Resolutions, PlayedStatuses };

        public IEnumerable<FilterItem> AllFilterOptions =>
            Genres.Concat(Years).Concat(Decades).Concat(Ratings).Concat(Resolutions).Concat(PlayedStatuses);

        public BaseItemDto SelectedLibrary
        {
            get => _selectedLibrary;
            set
            {
                var isChangingLibrary = _selectedLibrary?.Id != value.Id;

                if (SetProperty(ref _selectedLibrary, value))
                {
                    Logger.LogInformation(
                        "SelectedLibrary changed to: {ValueName} (Type: {ValueCollectionType})", value.Name, value.CollectionType);

                    if (isChangingLibrary)
                    {
                        _loadFiltersCts?.Cancel();
                        _applyFiltersCts?.Cancel();

                        FireAndForget(() => RunOnUIThreadAsync(DeselectAllFilterOptions));
                    }

                    NotifyLibraryPropertiesChanged();
                    InvalidateFilterCountCache();
                    // Loading waits for the page's InitializeAsync, once the signed-in user is known.
                    // Otherwise the empty state would show first
                }
            }
        }

        public string LibraryName => SelectedLibrary?.Name ?? "Library";

        public string ItemCountSubtitle => TotalItemCount switch
        {
            0 => "",
            1 => "1 item",
            _ => $"{TotalItemCount:N0} items"
        };

        public bool IsMusicLibrary => SelectedLibrary?.CollectionType == BaseItemDto_CollectionType.Music;

        public double ItemWidth => IsMusicLibrary ? MusicItemWidth : DefaultPosterWidth;

        // Album cells carry two lines of text under the image, artist cells one
        public double ItemHeight => (IsMusicLibrary, CurrentFilter) switch
        {
            (false, _) => DefaultPosterHeight,
            (true, "Albums") => AlbumItemHeight,
            _ => ArtistItemHeight
        };

        public int ActiveFilterCount
        {
            get
            {
                // Decades are left out: selecting one selects its years, which are counted
                return _cachedActiveFilterCount ??= new[] { Genres, Years, Ratings, Resolutions, PlayedStatuses }
                    .Sum(collection => collection.Count(item => item.IsSelected));
            }
        }

        public bool HasActiveFilters => ActiveFilterCount > 0;

        public string CurrentFilter
        {
            get => _currentFilter;
            set
            {
                if (SetProperty(ref _currentFilter, value))
                {
                    OnPropertyChanged(nameof(ItemWidth));
                    OnPropertyChanged(nameof(ItemHeight));
                }
            }
        }

        public int SelectedSortIndex
        {
            get => _selectedSortIndex;
            set
            {
                if (SetProperty(ref _selectedSortIndex, value))
                {
                    OnPropertyChanged(nameof(ShowJumpLetters));

                    FireAndForget(() => ApplyFiltersAsync());
                }
            }
        }

        /// <summary>
        ///     The jump column's letters, in list order: one for each letter the loaded items file under
        /// </summary>
        public IReadOnlyList<string> JumpLetters { get; private set; } = Array.Empty<string>();

        /// <summary>
        ///     Letters only mean something in name order, and a single letter is no jump
        /// </summary>
        public bool ShowJumpLetters => SelectedSortIndex == 0 && JumpLetters.Count > 1;

        public bool IsAscending
        {
            get => _isAscending;
            set
            {
                if (SetProperty(ref _isAscending, value))
                {
                    FireAndForget(() => ApplyFiltersAsync());
                }
            }
        }

        private int TotalItemCount
        {
            get => _totalItemCount;
            set
            {
                if (SetProperty(ref _totalItemCount, value))
                {
                    OnPropertyChanged(nameof(IsEmpty));
                    OnPropertyChanged(nameof(ItemCountSubtitle));
                }
            }
        }

        public bool IsEmpty => HasLoadedOnce && !IsLoading && TotalItemCount == 0;

        private bool HasLoadedOnce
        {
            get => _hasLoadedOnce;
            set
            {
                if (SetProperty(ref _hasLoadedOnce, value))
                {
                    OnPropertyChanged(nameof(IsEmpty));
                }
            }
        }

        // The decades the library's years fall in, newest first
        private List<DecadeFilterItem> BuildDecades(IEnumerable<int> years)
        {
            return years.Select(year => year / 10 * 10).Distinct().OrderByDescending(start => start)
                .Select(start => new DecadeFilterItem($"{start}s", start, start + 9, Years))
                .ToList();
        }

        private void InvalidateFilterCountCache()
        {
            _cachedActiveFilterCount = null;
            OnPropertyChanged(nameof(ActiveFilterCount));
            OnPropertyChanged(nameof(HasActiveFilters));
        }

        public event EventHandler<BaseItemDto> JumpRequested;

        public void JumpTo(string letter)
        {
            var target = MediaItems.FirstOrDefault(item => JumpLetter(item) == letter);
            if (target != null)
            {
                JumpRequested?.Invoke(this, target);
            }
        }

        /// <summary>
        ///     The letter an item files under: the first character of the name the server sorted
        ///     the list by, accents dropped; anything outside A-Z files under "#"
        /// </summary>
        private string JumpLetter(BaseItemDto item)
        {
            var key = SortsByTitle ? item.Name : item.SortName ?? item.Name;
            if (string.IsNullOrEmpty(key))
            {
                return "#";
            }

            var first = char.ToUpperInvariant(key.Normalize(NormalizationForm.FormD)[0]);
            return first >= 'A' && first <= 'Z' ? first.ToString() : "#";
        }

        private void SetMediaItems(IReadOnlyCollection<BaseItemDto> items)
        {
            MediaItems.ReplaceAll(items);

            var letters = new List<string>();
            var seen = new HashSet<string>();
            foreach (var item in items)
            {
                var letter = JumpLetter(item);
                if (seen.Add(letter))
                {
                    letters.Add(letter);
                }
            }

            JumpLetters = letters;
            OnPropertyChanged(nameof(JumpLetters));
            OnPropertyChanged(nameof(ShowJumpLetters));
        }

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetCurrentUserId(out _))
            {
                throw new InvalidOperationException("No signed-in user.");
            }

            await LoadFiltersAsync(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            await ApplyFiltersAsync().ConfigureAwait(false);
        }

        /// <summary>
        ///     Empties every filter's options; LoadFiltersAsync fills them again for the library.
        /// </summary>
        private void ClearFilterOptions()
        {
            Genres.Clear();
            Years.Clear();
            Decades.Clear();
            Ratings.Clear();
            Resolutions.Clear();
            PlayedStatuses.Clear();
        }

        private async Task LoadFiltersAsync(CancellationToken cancellationToken)
        {
            var localToken = AsyncHelper.Supersede(ref _loadFiltersCts, cancellationToken).Token;

            if (!TryGetCurrentUserId(out var userId) || SelectedLibrary?.Id == null)
            {
                Logger.LogDebug(
                    "LoadFiltersAsync: CurrentUserId or SelectedLibrary (or its ID) is null. Clearing filters.");
                await RunOnUIThreadAsync(ClearFilterOptions);
                return;
            }

            var selectedLibraryId = SelectedLibrary.Id.Value;
            var context = CreateErrorContext("LoadFilters");
            try
            {
                Logger.LogDebug(
                    "Loading filters for library: {SelectedLibraryName} (ID: {SelectedLibraryId})", SelectedLibrary.Name, selectedLibraryId);

                await RunOnUIThreadAsync(UpdateFilterVisibility);

                // The server's own lists for what the page shows: the genres, official ratings and
                // years its items have (a UK library has UK ratings; no year without an item)
                localToken.ThrowIfCancellationRequested();
                var kind = ItemKindForCurrentView();
                var filters = await _apiClient.Items.Filters.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.ParentId = selectedLibraryId;
                    if (kind.HasValue)
                    {
                        config.QueryParameters.IncludeItemTypes = new[] { kind.Value };
                    }
                }, localToken).ConfigureAwait(false);

                var genres = filters?.Genres?.Where(g => !string.IsNullOrEmpty(g)).OrderBy(g => g).ToList()
                             ?? new List<string>();
                var ratings = filters?.OfficialRatings?.Where(r => !string.IsNullOrEmpty(r)).ToList()
                              ?? new List<string>();
                var years = filters?.Years?.Where(y => y.HasValue).Select(y => y.Value).Distinct()
                                .OrderByDescending(y => y).ToList()
                            ?? new List<int>();

                await RunOnUIThreadAsync(() =>
                {
                    Genres.ReplaceAll(ShowGenreFilter
                        ? genres.Select(genre => new FilterItem(genre))
                        : Enumerable.Empty<FilterItem>());

                    Years.ReplaceAll(ShowYearFilter
                        ? years.Select(year => new FilterItem(year.ToString()))
                        : Enumerable.Empty<FilterItem>());

                    Ratings.ReplaceAll(ShowRatingFilter
                        ? ratings.Select(rating => new FilterItem(rating))
                        : Enumerable.Empty<FilterItem>());

                    Resolutions.ReplaceAll(ShowResolutionFilter
                        ? VideoResolutions.Select(resolution => new FilterItem(resolution))
                        : Enumerable.Empty<FilterItem>());

                    PlayedStatuses.ReplaceAll(ShowPlayedStatusFilter
                        ? new[] { new FilterItem("Watched"), new FilterItem("Unwatched") }
                        : Enumerable.Empty<FilterItem>());

                    Decades.ReplaceAll(ShowYearFilter ? BuildDecades(years) : Enumerable.Empty<DecadeFilterItem>());
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private void UpdateFilterVisibility()
        {
            // Music has no ratings or resolutions (the server filters items by no audio quality).
            // Books have neither; photos have no genres, ratings or played state; collections have
            // none of these.
            var (genre, year, rating, resolution, played) = SelectedLibrary?.CollectionType switch
            {
                BaseItemDto_CollectionType.Music => (true, true, false, false, true),
                BaseItemDto_CollectionType.Books => (true, true, false, false, true),
                BaseItemDto_CollectionType.Photos => (false, true, false, true, false),
                BaseItemDto_CollectionType.Boxsets => (false, false, false, false, false),
                _ => (true, true, true, true, true)
            };

            ShowGenreFilter = genre;
            ShowYearFilter = year;
            ShowRatingFilter = rating;
            ShowResolutionFilter = resolution;
            ShowPlayedStatusFilter = played;
        }

        private bool TryGetCurrentUserId(out Guid userId)
        {
            var current = _userProfileService.GetCurrentUserGuid();
            userId = current ?? Guid.Empty;
            if (!current.HasValue)
            {
                Logger.LogWarning("No signed-in user");
            }

            return current.HasValue;
        }

        /// <summary>
        ///     The whole library in one request: the page has no incremental loading.
        /// </summary>
        private async Task<BaseItemDtoQueryResult> FetchMediaItemsAsync(CancellationToken cancellationToken)
        {
            if (!TryGetCurrentUserId(out var userId))
            {
                return new BaseItemDtoQueryResult { Items = new List<BaseItemDto>(), TotalRecordCount = 0 };
            }

            var query = new ItemsRequestBuilder.ItemsRequestBuilderGetQueryParameters();
            ConfigureItemsQuery(query, userId);

            return await RetryHelper.ExecuteWithRetryAsync(
                () =>
                {
                    if (!ListsAlbumArtists)
                    {
                        return _apiClient.Items.GetAsync(config => config.QueryParameters = query, cancellationToken);
                    }

                    // The artists endpoint takes the same filters; parameters only /Items knows
                    // are left out of its URL
                    var request = _apiClient.Artists.AlbumArtists.ToGetRequestInformation();
                    request.AddQueryParameters(query);
                    return _requestAdapter.SendAsync(request, BaseItemDtoQueryResult.CreateFromDiscriminatorValue,
                        cancellationToken: cancellationToken);
                },
                Logger,
                cancellationToken);
        }

        private void ConfigureItemsQuery(ItemsRequestBuilder.ItemsRequestBuilderGetQueryParameters query, Guid userId)
        {
            query.UserId = userId;
            query.Recursive = true;
            query.Fields = new[]
            {
                ItemFields.Overview, ItemFields.PrimaryImageAspectRatio, ItemFields.ChildCount, ItemFields.DateCreated,
                ItemFields.SortName
            };
            query.EnableImageTypes = new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };

            query.ParentId = SelectedLibrary?.Id;

            // On the album-artists endpoint the item types select the items whose artists are
            // listed, so the artist kind itself would list none
            if (ItemKindForCurrentView() is BaseItemKind itemKind && !ListsAlbumArtists)
            {
                query.IncludeItemTypes = new[] { itemKind };
            }

            query.SortBy = SortByForCurrentView();
            query.SortOrder = new[] { IsAscending ? SortOrder.Ascending : SortOrder.Descending };

            var genres = SelectedNames(Genres);
            if (genres.Length > 0)
            {
                query.Genres = genres;
            }

            var years = SelectedNames(Years)
                .Select(y => int.TryParse(y, out var year) ? (int?)year : null)
                .Where(y => y.HasValue)
                .ToArray();
            if (years.Length > 0)
            {
                query.Years = years;
            }

            var ratings = SelectedNames(Ratings);
            if (ratings.Length > 0)
            {
                query.OfficialRatings = ratings;
            }

            // More than one resolution (or played status) selected means no restriction
            var resolutions = SelectedNames(Resolutions);
            if (resolutions.Length == 1)
            {
                switch (resolutions[0])
                {
                    case "4K":
                        query.Is4K = true;
                        break;
                    case "1080p":
                        query.MinHeight = 1080;
                        query.MaxHeight = 1080;
                        break;
                    case "720p":
                        query.MinHeight = 720;
                        query.MaxHeight = 720;
                        break;
                    case "SD":
                        query.MaxHeight = 480;
                        break;
                }
            }

            var playedStatuses = SelectedNames(PlayedStatuses);
            if (playedStatuses.Length == 1)
            {
                query.IsPlayed = playedStatuses[0] switch
                {
                    "Watched" => true,
                    "Unwatched" => false,
                    _ => null
                };
            }
        }

        private static string[] SelectedNames(IEnumerable<FilterItem> options)
        {
            return options.Where(o => o.IsSelected).Select(o => o.Name).ToArray();
        }

        /// <summary>
        ///     What the page lists: fixed by the library type, except that music and TV libraries
        ///     switch between their kinds with the content filter. Music's "All" means artists.
        /// </summary>
        private BaseItemKind? ItemKindForCurrentView()
        {
            switch (SelectedLibrary?.CollectionType)
            {
                case BaseItemDto_CollectionType.Movies:
                    return BaseItemKind.Movie;
                case BaseItemDto_CollectionType.Tvshows:
                    return BaseItemKind.Series;
                case BaseItemDto_CollectionType.Music:
                    switch (CurrentFilter)
                    {
                        case "All":
                        case "Artists":
                            return BaseItemKind.MusicArtist;
                        case "Albums":
                            return BaseItemKind.MusicAlbum;
                        case "Songs":
                            return BaseItemKind.Audio;
                        default:
                            return null;
                    }
                case BaseItemDto_CollectionType.Musicvideos:
                    return BaseItemKind.MusicVideo;
                case BaseItemDto_CollectionType.Homevideos:
                    return BaseItemKind.Video;
                case BaseItemDto_CollectionType.Books:
                    return BaseItemKind.Book;
                case BaseItemDto_CollectionType.Photos:
                    return BaseItemKind.Photo;
                case BaseItemDto_CollectionType.Boxsets:
                    return BaseItemKind.BoxSet;
                default:
                    return null;
            }
        }

        /// <summary>
        ///     The sort-menu entries in order: name, date added, release date, community rating,
        ///     critic rating, random. The later keys break ties.
        /// </summary>
        private ItemSortBy[] SortByForCurrentView()
        {
            return SelectedSortIndex switch
            {
                1 => new[] { ItemSortBy.DateCreated },
                2 => new[] { ItemSortBy.PremiereDate, ItemSortBy.ProductionYear, ItemSortBy.SortName },
                3 => new[] { ItemSortBy.CommunityRating, ItemSortBy.SortName },
                4 => new[] { ItemSortBy.CriticRating, ItemSortBy.SortName },
                5 => new[] { ItemSortBy.Random },
                _ => new[] { SortsByTitle ? ItemSortBy.Name : ItemSortBy.SortName }
            };
        }

        /// <summary>
        ///     The Artists view lists the server's album artists: artists credited only on tracks
        ///     ("X feat. Y") would crowd the grid with near-duplicates
        /// </summary>
        private bool ListsAlbumArtists => ItemKindForCurrentView() == BaseItemKind.MusicArtist;

        /// <summary>
        ///     A song's sort name is "disc - track - title" (album order), so a name sort of songs
        ///     goes by their title
        /// </summary>
        private bool SortsByTitle => ItemKindForCurrentView() == BaseItemKind.Audio;

        public async Task ApplyFiltersAsync()
        {
            var cancellationToken = AsyncHelper.Supersede(ref _applyFiltersCts).Token;

            await RunOnUIThreadAsync(() =>
            {
                IsLoading = true;
                SetMediaItems(Array.Empty<BaseItemDto>());
                TotalItemCount = 0;
            });

            var applyContext = CreateErrorContext("ApplyFilters");
            try
            {
                var result = await FetchMediaItemsAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var items = result?.Items?.Where(item => item != null).ToList() ?? new List<BaseItemDto>();
                Logger.LogInformation("Library query returned {ItemCount} of {TotalRecordCount} items", items.Count,
                    result?.TotalRecordCount);

                await RunOnUIThreadAsync(() =>
                {
                    SetMediaItems(items);
                    if (items.Count > 0)
                    {
                        TotalItemCount = result?.TotalRecordCount ?? items.Count;
                    }
                    else
                    {
                        UpdateEmptyState();
                    }
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, applyContext);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // A newer apply owns the loading state now
                return;
            }

            await RunOnUIThreadAsync(() =>
            {
                IsLoading = false;
                HasLoadedOnce = true;
                OnPropertyChanged(nameof(IsEmpty));
            });
        }

        protected override Task RefreshDataCoreAsync()
        {
            return ApplyFiltersAsync();
        }

        private void DeselectAllFilterOptions()
        {
            foreach (var option in AllFilterOptions)
            {
                option.IsSelected = false;
            }
        }

        public void ClearAllFilters()
        {
            FireAndForget(async () =>
            {
                await RunOnUIThreadAsync(DeselectAllFilterOptions);
                InvalidateFilterCountCache();
                await ApplyFiltersAsync();
            });
        }

        // Called when a query returns nothing: IsEmpty is not true yet at that point (the load has
        // not finished), which is why it is not checked here
        private void UpdateEmptyState()
        {
            if (HasActiveFilters)
            {
                EmptyStateTitle = "No items match your filters";
                EmptyStateMessage = "Try adjusting or clearing some filters.";
            }
            else
            {
                EmptyStateTitle = "This library is empty";
                EmptyStateMessage = "Add some media to get started.";
            }
        }

        protected override void DisposeManaged()
        {
            foreach (var options in FilterOptionLists)
            {
                options.CollectionChanged -= OnFilterOptionsChanged;
            }

            AsyncHelper.Cancel(ref _applyFiltersCts);
            AsyncHelper.Cancel(ref _loadFiltersCts);

            base.DisposeManaged();
        }

        // The count of selected options follows the lists; filters apply on the Apply button
        private void OnFilterOptionsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            InvalidateFilterCountCache();
        }

        /// <summary>
        ///     Shuffle All on a music library: songs from the whole library, picked at random by the
        ///     server (up to ShufflePlaylistLimit), whichever view -- artists or albums -- is showing.
        /// </summary>
        public async Task ShuffleAllAsync()
        {
            var userId = _userProfileService.GetCurrentUserGuid();
            var libraryId = SelectedLibrary?.Id;
            if (!userId.HasValue || !libraryId.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("ShuffleAll", ErrorCategory.Media);
            try
            {
                var result = await _apiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userId.Value;
                    config.QueryParameters.ParentId = libraryId.Value;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Audio };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.Random };
                    config.QueryParameters.Limit = ShufflePlaylistLimit;
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.Fields = new[] { ItemFields.PrimaryImageAspectRatio, ItemFields.MediaSources };
                }).ConfigureAwait(false);

                if (result?.Items == null || result.Items.Count == 0)
                {
                    Logger.LogWarning("No songs found in music library {LibraryName}", SelectedLibrary.Name);
                    return;
                }

                Logger.LogInformation("Shuffling {Count} songs from {LibraryName}", result.Items.Count, SelectedLibrary.Name);
                await _musicPlayerService.ShufflePlayAsync(result.Items.ToList()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        private void NotifyLibraryPropertiesChanged()
        {
            OnPropertyChanged(nameof(LibraryName));
            OnPropertyChanged(nameof(ItemCountSubtitle));
            OnPropertyChanged(nameof(IsMusicLibrary));
            OnPropertyChanged(nameof(ItemWidth));
            OnPropertyChanged(nameof(ItemHeight));
        }
    }
}
