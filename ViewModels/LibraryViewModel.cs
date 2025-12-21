// Required for CancellationToken
// Required for AsyncRelayCommand
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml;
using static Gelatinarm.Constants.LibraryConstants;

namespace Gelatinarm.ViewModels
{
    public class LibraryViewModel : BaseViewModel
    {
        private const int PageSize = DefaultPageSize;
        private const int MaxGenreCacheSize = 50; // Limit cache to 50 libraries
        private readonly JellyfinApiClient _apiClient;
        private readonly ConcurrentDictionary<Guid, IEnumerable<string>> _genreCache = new();
        private readonly Queue<Guid> _genreCacheOrder = new(); // Track insertion order for LRU
        private readonly IUserProfileService _userProfileService;
        private CancellationTokenSource _applyFiltersCts;
        private int? _cachedActiveFilterCount;
        private string _currentAlphabetFilter = string.Empty;

        private string _currentFilter = "All";
        private int _currentStartIndex;

        private Guid? _currentUserId;
        private string _emptyStateMessage = "Try adjusting your filters or search term";
        private string _emptyStateTitle = "No items found";

        private bool _hasLoadedOnce = false;

        private bool _hasMoreItems;
        private bool _isAscending = true;
        private bool _isLoadingMore = false;

        private CancellationTokenSource _loadFiltersCts;
        private readonly object _loadFiltersCtsLock = new object();

        private CancellationTokenSource _loadLibrariesCts;
        private readonly object _loadLibrariesCtsLock = new object();

        private CancellationTokenSource _loadMoreCts = new();

        private string _searchTerm = string.Empty;

        private BaseItemDto _selectedLibrary;
        private int _selectedSortIndex = 0;

        private bool _showGenreFilter = true;
        private bool _showPlayedStatusFilter = true;
        private bool _showRatingFilter = true;
        private bool _showResolutionFilter = true;
        private bool _showYearFilter = true;

        private int _totalItemCount = 0;

        public LibraryViewModel(
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            ILogger<LibraryViewModel> logger)
            : base(logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));

            _currentStartIndex = 0;
            HasMoreItems = true;
            // Start with loading state to prevent empty state from showing initially
            IsLoading = true;

            SetFilterCommand = new RelayCommand<string>(SetFilter);
            SetAlphabetFilterCommand = new RelayCommand<string>(SetAlphabetFilter);
            ScrollToAlphabetCommand =
                new RelayCommand<string>(letter => ScrollToAlphabetRequested?.Invoke(this, letter));
            RefreshCommand = new RelayCommand(async () => await RefreshAsync().ConfigureAwait(false));
            LoadMoreItemsCommand = new AsyncRelayCommand(ExecuteLoadMoreItemsAsync, CanExecuteLoadMoreItems);

            InitializeStyles();
            _currentFilter = "All";

            // Subscribe to filter item property changes
            Genres.CollectionChanged += OnGenresCollectionChanged;
            Years.CollectionChanged += OnYearsCollectionChanged;
            Ratings.CollectionChanged += OnRatingsCollectionChanged;
            Resolutions.CollectionChanged += OnResolutionsCollectionChanged;
            PlayedStatuses.CollectionChanged += OnPlayedStatusesCollectionChanged;
            Decades.CollectionChanged += OnDecadesCollectionChanged;
        }

        public ObservableCollection<BaseItemDto> MediaItems { get; } = new();
        public ObservableCollection<BaseItemDto> Libraries { get; } = new();
        public ObservableCollection<FilterItem> Genres { get; } = new();
        public ObservableCollection<FilterItem> Years { get; } = new();
        public ObservableCollection<DecadeFilterItem> Decades { get; } = new();
        public ObservableCollection<FilterItem> Ratings { get; } = new();
        public ObservableCollection<FilterItem> Resolutions { get; } = new();
        public ObservableCollection<FilterItem> PlayedStatuses { get; } = new();

        public string SearchTerm
        {
            get => _searchTerm;
            set
            {
                if (SetProperty(ref _searchTerm, value))
                {
                    // Don't apply filters immediately - wait for Apply button or user action
                    // This could be enhanced to have a debounced search if needed
                }
            }
        }

        public BaseItemDto SelectedLibrary
        {
            get => _selectedLibrary;
            set
            {
                // Check if we're actually changing to a different library
                var isChangingLibrary = _selectedLibrary?.Id != value?.Id;

                if (SetProperty(ref _selectedLibrary, value))
                {
                    if (value != null)
                    {
                        Logger.LogInformation(
                            $"SelectedLibrary changed to: {value.Name} (Type: {value.CollectionType})");

                        // Clear filters when switching to a different library
                        if (isChangingLibrary)
                        {
                            Logger.LogInformation("Clearing filters due to library change");

                            // Cancel any ongoing operations
                            // Cancel loading operations handled by base class
                            _loadFiltersCts?.Cancel();
                            _applyFiltersCts?.Cancel();

                            _ = RunOnUIThreadAsync(() =>
                            {
                                foreach (var genre in Genres)
                                {
                                    genre.IsSelected = false;
                                }

                                foreach (var year in Years)
                                {
                                    year.IsSelected = false;
                                }

                                foreach (var rating in Ratings)
                                {
                                    rating.IsSelected = false;
                                }

                                foreach (var resolution in Resolutions)
                                {
                                    resolution.IsSelected = false;
                                }

                                foreach (var status in PlayedStatuses)
                                {
                                    status.IsSelected = false;
                                }
                            });
                            CurrentAlphabetFilter = string.Empty;
                        }

                        // Batch property change notifications
                        NotifyLibraryPropertiesChanged();
                        InvalidateFilterCountCache();
                        // Don't automatically load data here - wait for InitializeAsync to be called
                        // This prevents the empty state from showing before user ID is loaded
                    }
                    else
                    {
                        Logger.LogWarning("SelectedLibrary set to null");
                        // Batch property change notifications
                        NotifyLibraryPropertiesChanged();
                        InvalidateFilterCountCache();
                        _ = RunOnUIThreadAsync(() =>
                        {
                            MediaItems.Clear();
                            Genres.Clear();
                            Years.Clear();
                            Ratings.Clear();
                            Resolutions.Clear();
                            PlayedStatuses.Clear();
                        });
                        TotalItemCount = 0;
                        HasMoreItems = false;
                        CurrentAlphabetFilter = string.Empty;
                    }
                }
            }
        }

        public string LibraryName => SelectedLibrary?.Name ?? "Library";

        public string LibraryNameWithCount => MediaItems?.Any() == true
            ? $"{LibraryName} ({TotalItemCount:N0} items)"
            : LibraryName;

        public string ItemCountSubtitle =>
            TotalItemCount == 0 ? "" : TotalItemCount == 1 ? "1 item" : $"{TotalItemCount:N0} items";

        public bool ShowShuffleButton => SelectedLibrary?.CollectionType == BaseItemDto_CollectionType.Music;

        public string ItemTemplateName
        {
            get
            {
                if (SelectedLibrary?.CollectionType == BaseItemDto_CollectionType.Music)
                {
                    // For music libraries, always use the album template
                    return "CompactAlbumTemplate";
                }

                // Default to poster template for movies, shows, etc.
                return "CompactPosterTemplate";
            }
        }

        public double ItemWidth
        {
            get
            {
                // Use consistent width for all music library items
                if (SelectedLibrary?.CollectionType == BaseItemDto_CollectionType.Music)
                {
                    return MusicItemWidth; // Width for music items (artists and albums)
                }

                // Default width for regular poster items (movies/TV)
                return DefaultPosterWidth; // Actual content width (130) + margins (2)
            }
        }

        public double ItemHeight
        {
            get
            {
                // Use consistent height for all music library items
                if (SelectedLibrary?.CollectionType == BaseItemDto_CollectionType.Music)
                {
                    // For albums view, we need more height for the text
                    if (CurrentFilter == "Albums")
                    {
                        return AlbumItemHeight; // 130px image + ~50px for two lines of text
                    }

                    // For artists view, we need less height (only one line of text)
                    return ArtistItemHeight; // 130px image + ~25px for one line of text + bottom padding
                }

                // Default height for regular poster items
                return DefaultPosterHeight;
            }
        }

        public int ActiveFilterCount
        {
            get
            {
                if (!_cachedActiveFilterCount.HasValue)
                {
                    _cachedActiveFilterCount = new[] { Genres, Years, Ratings, Resolutions, PlayedStatuses }
                        .Sum(collection => collection.Count(item => item.IsSelected));
                }

                return _cachedActiveFilterCount.Value;
            }
        }

        public bool HasActiveFilters => ActiveFilterCount > 0;
        public bool ShowGenreFilter { get => _showGenreFilter; set => SetProperty(ref _showGenreFilter, value); }
        public bool ShowYearFilter { get => _showYearFilter; set => SetProperty(ref _showYearFilter, value); }
        public bool ShowRatingFilter { get => _showRatingFilter; set => SetProperty(ref _showRatingFilter, value); }

        public bool ShowResolutionFilter
        {
            get => _showResolutionFilter;
            set => SetProperty(ref _showResolutionFilter, value);
        }

        public bool ShowPlayedStatusFilter
        {
            get => _showPlayedStatusFilter;
            set => SetProperty(ref _showPlayedStatusFilter, value);
        }

        public string CurrentFilter
        {
            get => _currentFilter;
            set
            {
                if (SetProperty(ref _currentFilter, value))
                {
                    OnPropertyChanged(nameof(FilterButtonStyles));
                    OnPropertyChanged(nameof(ItemTemplateName));
                    OnPropertyChanged(nameof(ItemWidth));
                    OnPropertyChanged(nameof(ItemHeight));
                    // Don't apply filters immediately - wait for Apply button 
                }
            }
        }

        public string CurrentAlphabetFilter
        {
            get => _currentAlphabetFilter;
            set
            {
                if (SetProperty(ref _currentAlphabetFilter, value)) { OnPropertyChanged(nameof(AlphabetButtonStyles)); }
            }
        }

        public int SelectedSortIndex
        {
            get => _selectedSortIndex;
            set
            {
                if (SetProperty(ref _selectedSortIndex, value))
                {
                    // Trigger refresh when sort changes
                    FireAndForget(async () => await ApplyFiltersAsync());
                }
            }
        }

        public bool IsAscending
        {
            get => _isAscending;
            set
            {
                if (SetProperty(ref _isAscending, value))
                {
                    // Trigger refresh when sort order changes
                    FireAndForget(async () => await ApplyFiltersAsync());
                }
            }
        }

        public int TotalItemCount
        {
            get => _totalItemCount;
            set
            {
                if (SetProperty(ref _totalItemCount, value))
                {
                    OnPropertyChanged(nameof(ResultsCountText));
                    OnPropertyChanged(nameof(IsEmpty));
                    OnPropertyChanged(nameof(LibraryNameWithCount));
                    OnPropertyChanged(nameof(ItemCountSubtitle));
                }
            }
        }

        public string ResultsCountText => TotalItemCount == 0 ? "No items" :
            TotalItemCount == 1 ? "1 item" : $"{TotalItemCount:N0} items";

        public bool IsEmpty => HasLoadedOnce && !IsLoading && !IsLoadingMore && TotalItemCount == 0;
        public string EmptyStateTitle { get => _emptyStateTitle; set => SetProperty(ref _emptyStateTitle, value); }

        public string EmptyStateMessage
        {
            get => _emptyStateMessage;
            set => SetProperty(ref _emptyStateMessage, value);
        }

        public bool HasMoreItems { get => _hasMoreItems; private set => SetProperty(ref _hasMoreItems, value); }
        public bool IsLoadingMore { get => _isLoadingMore; private set => SetProperty(ref _isLoadingMore, value); }

        public bool HasLoadedOnce
        {
            get => _hasLoadedOnce;
            private set
            {
                if (SetProperty(ref _hasLoadedOnce, value))
                {
                    OnPropertyChanged(nameof(IsEmpty));
                }
            }
        }

        public Dictionary<string, Style> FilterButtonStyles { get; private set; }
        public Dictionary<char, Style> AlphabetButtonStyles { get; private set; }

        public ICommand SetFilterCommand { get; }
        public ICommand SetAlphabetFilterCommand { get; }
        public ICommand ScrollToAlphabetCommand { get; }
        public ICommand RefreshCommand { get; }
        public IAsyncRelayCommand LoadMoreItemsCommand { get; }

        private void InvalidateFilterCountCache()
        {
            _cachedActiveFilterCount = null;
            OnPropertyChanged(nameof(ActiveFilterCount));
            OnPropertyChanged(nameof(HasActiveFilters));
        }

        public event EventHandler<string> ScrollToAlphabetRequested;

        private void InitializeStyles()
        {
            FilterButtonStyles = new Dictionary<string, Style>();
            AlphabetButtonStyles = new Dictionary<char, Style>();
        }

        private Style GetFilterButtonStyle(string filter)
        {
            if (Application.Current.Resources.TryGetValue(
                    filter == CurrentFilter ? "SelectedFilterButtonStyle" : "FilterButtonStyle", out var style) &&
                style is Style buttonStyle) { return buttonStyle; }

            return null;
        }

        private Style GetAlphabetButtonStyle(char letter)
        {
            var letterStr = letter.ToString();
            if (Application.Current.Resources.TryGetValue(
                    letterStr == CurrentAlphabetFilter ? "SelectedAlphabetButtonStyle" : "AlphabetButtonStyle",
                    out var style) && style is Style buttonStyle) { return buttonStyle; }

            return null;
        }

        public async Task InitializeAsync()
        {
            // Use the standardized LoadDataAsync pattern
            await LoadDataAsync(true);
        }

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            Logger.LogInformation("Initializing LibraryViewModel");

            if (_userProfileService == null)
            {
                throw new InvalidOperationException("User profile service is not available.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var userProfile = await _userProfileService.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
            if (userProfile?.Id == null)
            {
                throw new InvalidOperationException("User profile not loaded.");
            }

            _currentUserId = userProfile.Id.Value;
            Logger?.LogInformation($"Current user ID: {_currentUserId}");

            cancellationToken.ThrowIfCancellationRequested();

            if (SelectedLibrary == null)
            {
                await LoadLibrariesAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();

            await LoadFiltersAsync(cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            // Load the initial data
            await ApplyFiltersAsync().ConfigureAwait(false);

            Logger.LogInformation("LibraryViewModel initialized successfully");

            // Notify command state change
            LoadMoreItemsCommand.NotifyCanExecuteChanged();
        }

        private async Task LoadLibrariesAsync(CancellationToken cancellationToken = default)
        {
            // Cancel any previous load and create new token source atomically
            CancellationTokenSource localCts;
            lock (_loadLibrariesCtsLock)
            {
                _loadLibrariesCts?.Cancel();
                _loadLibrariesCts?.Dispose();
                _loadLibrariesCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                localCts = _loadLibrariesCts;
            }
            var localToken = localCts.Token;

            IsError = false;
            ErrorMessage = string.Empty;

            var context = CreateErrorContext("LoadLibraries");
            try
            {
                if (!TryGetCurrentUserId(out var userId))
                {
                    IsError = true;
                    ErrorMessage = "User is not logged in.";
                    return;
                }

                localToken.ThrowIfCancellationRequested();

                Logger?.LogInformation("Loading user libraries");
                var userViews = await _apiClient.UserViews.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userId;
                }, localToken).ConfigureAwait(false);

                if (Libraries != null)
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        var libraryList = new List<BaseItemDto>
                        {
                            new() { Name = "All Libraries", Id = Guid.Empty, CollectionType = null }
                        };
                        if (userViews?.Items != null)
                        {
                            libraryList.AddRange(userViews.Items.Where(library => library != null));
                        }

                        Libraries.ReplaceAll(libraryList);
                    });
                    // Ensure a library is selected, but only if one wasn't already (e.g. from previous state)
                    if (SelectedLibrary == null && Libraries.Any())
                    {
                        SelectedLibrary = Libraries.FirstOrDefault();
                    }

                    Logger?.LogInformation($"Loaded {Libraries.Count} libraries");
                }
                else
                {
                    Logger?.LogError("Libraries collection is null");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private async Task LoadFiltersAsync(CancellationToken cancellationToken = default)
        {
            // Cancel any previous load and create new token source atomically
            CancellationTokenSource localCts;
            lock (_loadFiltersCtsLock)
            {
                _loadFiltersCts?.Cancel();
                _loadFiltersCts?.Dispose();
                _loadFiltersCts = new CancellationTokenSource();
                localCts = _loadFiltersCts;
            }

            // Link with the parent token if provided
            using var linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, localCts.Token);
            var localToken = linkedCts.Token;

            if (!TryGetCurrentUserId(out _) || SelectedLibrary == null || !SelectedLibrary.Id.HasValue)
            {
                Logger.LogInformation(
                    "LoadFiltersAsync: CurrentUserId or SelectedLibrary (or its ID) is null. Clearing filters.");
                await RunOnUIThreadAsync(() =>
                {
                    Genres.Clear();
                    Years.Clear();
                    Ratings.Clear();
                    Resolutions.Clear();
                });
                return;
            }

            var selectedLibraryId = SelectedLibrary.Id.Value;
            // If SelectedLibrary.Id is Guid.Empty (representing "All Libraries"), we don't fetch specific genres for it.
            // Genres for "All Libraries" might be handled differently or not shown.
            if (selectedLibraryId == Guid.Empty)
            {
                Logger.LogInformation(
                    "LoadFiltersAsync: Selected library is 'All Libraries'. Clearing specific genre filters.");
                await RunOnUIThreadAsync(() => Genres.Clear());
                // Other filters like Years, Ratings, Resolutions might still be shown globally.
                // The decision to clear them or not here depends on desired UX for "All Libraries".
            }
            else
            {
                await RunOnUIThreadAsync(() =>
                {
                    IsError = false;
                    ErrorMessage = string.Empty;
                });

                var context = CreateErrorContext("LoadFilters");
                try
                {
                    Logger.LogInformation($"Loading filters for library: {SelectedLibrary.Name} (ID: {selectedLibraryId})");

                    // Update filter visibility on UI thread
                    await RunOnUIThreadAsync(() => UpdateFilterVisibility());

                    if (ShowGenreFilter &&
                        selectedLibraryId != Guid.Empty) // Only fetch/cache if showing and not "All Libraries"
                    {
                        if (_genreCache.TryGetValue(selectedLibraryId, out var cachedGenreNames))
                        {
                            Logger.LogInformation($"Loading genres from cache for library ID: {selectedLibraryId}");
                            await RunOnUIThreadAsync(() =>
                            {
                                var genreFilterItems = cachedGenreNames.Select(name => new FilterItem(name)).ToList();
                                Genres.ReplaceAll(genreFilterItems);
                            });
                        }
                        else
                        {
                            Logger?.LogInformation($"Fetching genres from API for library ID: {selectedLibraryId}");

                            localToken.ThrowIfCancellationRequested();

                            var genresResponse = await _apiClient.Genres.GetAsync(config =>
                            {
                                config.QueryParameters.ParentId = selectedLibraryId;
                                config.QueryParameters.UserId = _currentUserId.Value;
                            }, localToken).ConfigureAwait(false);
                            var fetchedGenreItems = genresResponse?.Items?.ToList() ?? new List<BaseItemDto>();

                            var genreNames = new List<string>();
                            if (fetchedGenreItems.Any())
                            {
                                foreach (var genre in fetchedGenreItems.OrderBy(g => g.Name))
                                {
                                    if (!string.IsNullOrEmpty(genre.Name)) { genreNames.Add(genre.Name); }
                                }
                            }

                            // Add to cache with size limit (LRU eviction)
                            lock (_genreCacheOrder)
                            {
                                if (_genreCache.Count >= MaxGenreCacheSize && !_genreCache.ContainsKey(selectedLibraryId))
                                {
                                    // Evict oldest entry
                                    if (_genreCacheOrder.Any())
                                    {
                                        var oldestKey = _genreCacheOrder.Dequeue();
                                        _genreCache.TryRemove(oldestKey, out _);
                                        Logger?.LogDebug($"Evicted genre cache for library {oldestKey} (cache full)");
                                    }
                                }

                                _genreCache[selectedLibraryId] = genreNames;
                                _genreCacheOrder.Enqueue(selectedLibraryId);
                            }

                            if (Genres != null)
                            {
                                await RunOnUIThreadAsync(() =>
                                {
                                    var genreItems = genreNames.Where(g => !string.IsNullOrEmpty(g))
                                        .Select(g => new FilterItem(g));
                                    Genres.ReplaceAll(genreItems);
                                });
                            }
                        }
                    }
                    else
                    {
                        await RunOnUIThreadAsync(() => Genres.Clear());
                    }

                    // Populate Years with recent years
                    if (Years != null)
                    {
                        await RunOnUIThreadAsync(() =>
                        {
                            if (ShowYearFilter)
                            {
                                var currentYear = DateTime.Now.Year;
                                // Add years from current year down to 1900
                                var yearItems = new List<FilterItem>();
                                for (var year = currentYear; year >= MinimumYear; year--)
                                {
                                    yearItems.Add(new FilterItem(year.ToString()));
                                }

                                Years.ReplaceAll(yearItems);
                            }
                            else
                            {
                                Years.Clear();
                            }
                        });
                    }

                    if (Ratings != null)
                    {
                        await RunOnUIThreadAsync(() =>
                        {
                            if (ShowRatingFilter)
                            {
                                var commonRatings = new[]
                                {
                                "G", "PG", "PG-13", "R", "NC-17", "TV-Y", "TV-Y7", "TV-G", "TV-PG", "TV-14", "TV-MA"
                                };
                                Ratings.ReplaceAll(commonRatings.Where(rating => !string.IsNullOrEmpty(rating))
                                    .Select(rating => new FilterItem(rating)));
                            }
                            else
                            {
                                Ratings.Clear();
                            }
                        });
                    }

                    if (Resolutions != null)
                    {
                        await RunOnUIThreadAsync(() =>
                        {
                            if (ShowResolutionFilter && SelectedLibrary != null)
                            {
                                if (SelectedLibrary.CollectionType == BaseItemDto_CollectionType.Music)
                                {
                                    Resolutions.ReplaceAll(new[]
                                    {
                                    new FilterItem("Lossless"), new FilterItem("High Quality"),
                                    new FilterItem("Standard Quality")
                                    });
                                }
                                else
                                {
                                    Resolutions.ReplaceAll(new[]
                                    {
                                    new FilterItem("4K"), new FilterItem("1080p"), new FilterItem("720p"),
                                    new FilterItem("SD")
                                    });
                                }
                            }
                            else
                            {
                                Resolutions.Clear();
                            }
                        });
                    }

                    // Initialize played statuses
                    if (PlayedStatuses != null)
                    {
                        await RunOnUIThreadAsync(() =>
                        {
                            if (ShowPlayedStatusFilter)
                            {
                                PlayedStatuses.ReplaceAll(new[] { new FilterItem("Watched"), new FilterItem("Unwatched") });
                            }
                            else
                            {
                                PlayedStatuses.Clear();
                            }
                        });
                    }
                    if (Decades != null)
                    {
                        await RunOnUIThreadAsync(() =>
                        {
                            if (ShowYearFilter)
                            {
                                Decades.ReplaceAll(new[]
                                {
                                new DecadeFilterItem("2020s", 2020, 2029) { YearsCollection = Years },
                                new DecadeFilterItem("2010s", 2010, 2019) { YearsCollection = Years },
                                new DecadeFilterItem("2000s", 2000, 2009) { YearsCollection = Years },
                                new DecadeFilterItem("1990s", 1990, 1999) { YearsCollection = Years },
                                new DecadeFilterItem("1980s", 1980, 1989) { YearsCollection = Years },
                                new DecadeFilterItem("1970s", 1970, 1979) { YearsCollection = Years },
                                new DecadeFilterItem("1960s", 1960, 1969) { YearsCollection = Years },
                                new DecadeFilterItem("1950s", 1950, 1959) { YearsCollection = Years },
                                new DecadeFilterItem("1940s", 1940, 1949) { YearsCollection = Years },
                                new DecadeFilterItem("1930s", 1930, 1939) { YearsCollection = Years },
                                new DecadeFilterItem("1920s", 1920, 1929) { YearsCollection = Years },
                                new DecadeFilterItem("1910s", 1910, 1919) { YearsCollection = Years },
                                new DecadeFilterItem("1900s", 1900, 1909) { YearsCollection = Years },
                                new DecadeFilterItem("Earlier", 0, 1899) { YearsCollection = Years }
                                });
                            }
                            else
                            {
                                Decades.Clear();
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            }
        }

        private void UpdateFilterVisibility()
        {
            if (SelectedLibrary == null)
            {
                ShowGenreFilter = true;
                ShowYearFilter = true;
                ShowRatingFilter = true;
                ShowResolutionFilter = true;
                ShowPlayedStatusFilter = true;
                return;
            }

            // Set filter visibility based on library type
            switch (SelectedLibrary.CollectionType)
            {
                case BaseItemDto_CollectionType.Movies:
                case BaseItemDto_CollectionType.Tvshows:
                case BaseItemDto_CollectionType.Musicvideos:
                case BaseItemDto_CollectionType.Homevideos:
                    ShowGenreFilter = true;
                    ShowYearFilter = true;
                    ShowRatingFilter = true;
                    ShowResolutionFilter = true;
                    ShowPlayedStatusFilter = true;
                    break;

                case BaseItemDto_CollectionType.Music:
                    ShowGenreFilter = true;
                    ShowYearFilter = true;
                    ShowRatingFilter = false; // Music doesn't typically have ratings
                    ShowResolutionFilter = true; // For audio quality
                    ShowPlayedStatusFilter = true;
                    break;

                case BaseItemDto_CollectionType.Books:
                    ShowGenreFilter = true;
                    ShowYearFilter = true;
                    ShowRatingFilter = false;
                    ShowResolutionFilter = false;
                    ShowPlayedStatusFilter = true;
                    break;

                case BaseItemDto_CollectionType.Photos:
                    ShowGenreFilter = false;
                    ShowYearFilter = true;
                    ShowRatingFilter = false;
                    ShowResolutionFilter = true;
                    ShowPlayedStatusFilter = false;
                    break;

                case BaseItemDto_CollectionType.Boxsets:
                    ShowGenreFilter = false;
                    ShowYearFilter = false;
                    ShowRatingFilter = false;
                    ShowResolutionFilter = false;
                    ShowPlayedStatusFilter = false;
                    break;

                default:
                    // Show all filters by default
                    ShowGenreFilter = true;
                    ShowYearFilter = true;
                    ShowRatingFilter = true;
                    ShowResolutionFilter = true;
                    ShowPlayedStatusFilter = true;
                    break;
            }
        }

        private bool TryGetCurrentUserId(out Guid userId)
        {
            userId = Guid.Empty;
            if (!_currentUserId.HasValue)
            {
                Logger?.LogWarning("CurrentUserId is null.");
                return false;
            }

            userId = _currentUserId.Value;
            return true;
        }

        private Dictionary<string, string> PrepareQueryParams(int startIndex, int limit)
        {
            var queryParams = new Dictionary<string, string>
            {
                ["userId"] = _currentUserId?.ToString() ?? string.Empty,
                ["recursive"] = "true",
                ["enableUserData"] = "true",
                ["imageTypeLimit"] = "1",
                ["enableImageTypes"] = "Primary,Backdrop",
                ["startIndex"] = startIndex.ToString(),
                ["limit"] = limit.ToString(),
                ["fields"] = "ChildCount,PrimaryImageAspectRatio,Overview,DateCreated"
            };

            if (SelectedLibrary?.Id != null && SelectedLibrary.Id != Guid.Empty)
            {
                queryParams["parentId"] = SelectedLibrary.Id.Value.ToString();
            }

            ApplyLibraryTypeRestrictions(queryParams);
            ApplyContentTypeFilterToParams(queryParams);
            if (!string.IsNullOrWhiteSpace(SearchTerm)) { queryParams["searchTerm"] = SearchTerm; }

            ApplySortingToParams(queryParams);
            ApplySpecialFiltersToParams(queryParams);
            var selectedGenres = Genres.Where(g => g.IsSelected).Select(g => g.Value).ToList();
            if (selectedGenres.Any()) { queryParams["genres"] = string.Join(",", selectedGenres); }

            var selectedYears = Years.Where(y => y.IsSelected).Select(y => y.Value).ToList();
            if (selectedYears.Any()) { queryParams["years"] = string.Join(",", selectedYears); }

            var selectedRatings = Ratings.Where(r => r.IsSelected).Select(r => r.Value).ToList();
            if (selectedRatings.Any()) { queryParams["officialRatings"] = string.Join(",", selectedRatings); }

            var selectedResolutions = Resolutions.Where(r => r.IsSelected).Select(r => r.Value).ToList();
            if (selectedResolutions.Count == 1)
            {
                switch (selectedResolutions[0])
                {
                    case "4K": queryParams["is4K"] = "true"; break;
                    case "1080p":
                        queryParams["minHeight"] = "1080";
                        queryParams["maxHeight"] = "1080";
                        break;
                    case "720p":
                        queryParams["minHeight"] = "720";
                        queryParams["maxHeight"] = "720";
                        break;
                    case "SD": queryParams["maxHeight"] = "480"; break;
                }
            }

            var selectedPlayedStatuses = PlayedStatuses.Where(p => p.IsSelected).Select(p => p.Value).ToList();
            if (selectedPlayedStatuses.Any())
            {
                if (selectedPlayedStatuses.Count == 1)
                {
                    if (selectedPlayedStatuses.Contains("Watched")) { queryParams["isPlayed"] = "true"; }
                    else if (selectedPlayedStatuses.Contains("Unwatched")) { queryParams["isPlayed"] = "false"; }
                }
            }

            return queryParams;
        }

        private async Task<BaseItemDtoQueryResult> FetchMediaItemsPageAsync(int startIndex, int limit,
            CancellationToken cancellationToken)
        {
            if (!TryGetCurrentUserId(out _))
            {
                return new BaseItemDtoQueryResult { Items = new List<BaseItemDto>(), TotalRecordCount = 0 };
            }

            var queryParams = PrepareQueryParams(startIndex, limit);

            // Log the query parameters for debugging
            Logger.LogInformation("FetchMediaItemsPageAsync - Query parameters:");
            foreach (var param in queryParams)
            {
                Logger.LogInformation($"  {param.Key}: {param.Value}");
            }

            return await BaseService.RetryAsync(
                async () =>
                {
                    var response = await _apiClient.Items.GetAsync(config =>
                    {
                        // Apply query parameters
                        foreach (var param in queryParams)
                        {
                            switch (param.Key)
                            {
                                case "userId":
                                    config.QueryParameters.UserId = new Guid(param.Value);
                                    break;
                                case "ParentId":
                                case "parentId":
                                    config.QueryParameters.ParentId = new Guid(param.Value);
                                    break;
                                case "includeItemTypes":
                                case "IncludeItemTypes":
                                    var includeTypes = param.Value.Split(',')
                                        .Select(t => Enum.TryParse<BaseItemKind>(t, out var kind) ? (BaseItemKind?)kind : null)
                                        .Where(k => k.HasValue)
                                        .Select(k => k.Value)
                                        .ToArray();
                                    if (includeTypes.Length > 0)
                                    {
                                        config.QueryParameters.IncludeItemTypes = includeTypes;
                                    }
                                    break;
                                case "excludeItemTypes":
                                case "ExcludeItemTypes":
                                    var excludeTypes = param.Value.Split(',')
                                        .Select(t => Enum.TryParse<BaseItemKind>(t, out var kind) ? (BaseItemKind?)kind : null)
                                        .Where(k => k.HasValue)
                                        .Select(k => k.Value)
                                        .ToArray();
                                    if (excludeTypes.Length > 0)
                                    {
                                        config.QueryParameters.ExcludeItemTypes = excludeTypes;
                                    }
                                    break;
                                case "genres":
                                case "Genres":
                                    config.QueryParameters.Genres = param.Value.Split(',');
                                    break;
                                case "years":
                                case "Years":
                                    config.QueryParameters.Years = param.Value.Split(',')
                                        .Select(y => int.TryParse(y, out var year) ? (int?)year : null)
                                        .Where(y => y.HasValue)
                                        .ToArray();
                                    break;
                                case "officialRatings":
                                case "OfficialRatings":
                                    config.QueryParameters.OfficialRatings = param.Value.Split(',');
                                    break;
                                case "MinWidth":
                                    if (int.TryParse(param.Value, out var minWidth))
                                    {
                                        config.QueryParameters.MinWidth = minWidth;
                                    }
                                    break;
                                case "isPlayed":
                                    config.QueryParameters.IsPlayed = bool.Parse(param.Value);
                                    break;
                                case "is4K":
                                    config.QueryParameters.Is4K = bool.Parse(param.Value);
                                    break;
                                case "minHeight":
                                    if (int.TryParse(param.Value, out var minHeight))
                                    {
                                        config.QueryParameters.MinHeight = minHeight;
                                    }
                                    break;
                                case "maxHeight":
                                    if (int.TryParse(param.Value, out var maxHeight))
                                    {
                                        config.QueryParameters.MaxHeight = maxHeight;
                                    }
                                    break;
                                case "Filters":
                                    if (param.Value == "IsPlayed")
                                    {
                                        config.QueryParameters.IsPlayed = true;
                                    }
                                    else if (param.Value == "IsUnplayed")
                                    {
                                        config.QueryParameters.IsPlayed = false;
                                    }

                                    break;
                                case "sortBy":
                                case "SortBy":
                                    config.QueryParameters.SortBy = param.Value.Split(',').Select(s => s switch
                                    {
                                        "Album" => ItemSortBy.Album,
                                        "AlbumArtist" => ItemSortBy.AlbumArtist,
                                        "Artist" => ItemSortBy.Artist,
                                        "Budget" => ItemSortBy.SortName, // Budget not available in SDK
                                        "CommunityRating" => ItemSortBy.CommunityRating,
                                        "CriticRating" => ItemSortBy.CriticRating,
                                        "DateCreated" => ItemSortBy.DateCreated,
                                        "DatePlayed" => ItemSortBy.DatePlayed,
                                        "PlayCount" => ItemSortBy.PlayCount,
                                        "PremiereDate" => ItemSortBy.PremiereDate,
                                        "ProductionYear" => ItemSortBy.ProductionYear,
                                        "SortName" => ItemSortBy.SortName,
                                        "Random" => ItemSortBy.Random,
                                        "Revenue" => ItemSortBy.SortName, // Revenue not available in SDK
                                        "Runtime" => ItemSortBy.Runtime,
                                        _ => ItemSortBy.SortName
                                    }).ToArray();
                                    break;
                                case "sortOrder":
                                case "SortOrder":
                                    config.QueryParameters.SortOrder = param.Value.Split(',')
                                        .Select(o => o == "Ascending" ? SortOrder.Ascending : SortOrder.Descending)
                                        .ToArray();
                                    break;
                                case "StartIndex":
                                    if (int.TryParse(param.Value, out var startIndex))
                                    {
                                        config.QueryParameters.StartIndex = startIndex;
                                    }
                                    break;
                                case "Limit":
                                    if (int.TryParse(param.Value, out var limit))
                                    {
                                        config.QueryParameters.Limit = limit;
                                    }
                                    break;
                                case "searchTerm":
                                case "SearchTerm":
                                    config.QueryParameters.SearchTerm = param.Value;
                                    break;
                                case "nameStartsWith":
                                case "NameStartsWith":
                                    config.QueryParameters.NameStartsWith = param.Value;
                                    break;
                                case "NameStartsWithOrGreater":
                                    config.QueryParameters.NameStartsWithOrGreater = param.Value;
                                    break;
                                case "NameLessThan":
                                    config.QueryParameters.NameLessThan = param.Value;
                                    break;
                            }
                        }

                        config.QueryParameters.Recursive = true;
                        config.QueryParameters.Fields = new[]
                        {
                            ItemFields.Overview, ItemFields.PrimaryImageAspectRatio, ItemFields.ChildCount,
                            ItemFields.DateCreated
                        };
                        config.QueryParameters.EnableImageTypes =
                            new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };
                    }).ConfigureAwait(false);
                    return response;
                },
                Logger,
                RetryConstants.DEFAULT_API_RETRY_ATTEMPTS,
                TimeSpan.FromMilliseconds(RetryConstants.INITIAL_RETRY_DELAY_MS),
                memberName: "GetItemsAsync");
        }

        public async Task ApplyFiltersAsync(bool isFullRefresh = true)
        {
            var cancelContext = CreateErrorContext("CancelPreviousFilter");
            try
            {
                _applyFiltersCts?.Cancel();
                _applyFiltersCts?.Dispose();
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, cancelContext, false);
            }

            _applyFiltersCts = new CancellationTokenSource();
            var cancellationToken = _applyFiltersCts.Token;

            if (isFullRefresh)
            {
                await RunOnUIThreadAsync(() =>
                {
                    IsLoading = true;
                    if (MediaItems != null)
                    {
                        MediaItems.Clear();
                    }
                    else
                    {
                        Logger?.LogError("MediaItems collection is null in ApplyFiltersAsync");
                    }

                    _currentStartIndex = 0;
                    HasMoreItems = true;
                    TotalItemCount = 0;
                    // Notify property changes since _currentStartIndex affects item dimensions
                    OnPropertyChanged(nameof(ItemWidth));
                    OnPropertyChanged(nameof(ItemHeight));
                    OnPropertyChanged(nameof(ItemTemplateName));
                });
            }

            await RunOnUIThreadAsync(() =>
            {
                IsError = false;
                ErrorMessage = string.Empty;
            });

            Logger.LogInformation(
                $"Applying filters (FullRefresh: {isFullRefresh}) - StartIndex: {_currentStartIndex}, PageSize: {PageSize}");

            var applyContext = CreateErrorContext("ApplyFilters");
            try
            {
                var result = await FetchMediaItemsPageAsync(_currentStartIndex, PageSize, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var newItems = result?.Items?.ToList() ?? new List<BaseItemDto>();

                // Log the API response
                Logger.LogInformation(
                    $"Filter API Response: Received {newItems.Count} items, TotalRecordCount: {result?.TotalRecordCount}");

                if (newItems.Any())
                {
                    // Log artist names if this is a music library showing artists
                    if (SelectedLibrary?.CollectionType == BaseItemDto_CollectionType.Music &&
                        CurrentFilter == "Artists")
                    {
                        foreach (var item in newItems.Take(10)) // Log first 10 for debugging
                        {
                            if (item != null)
                            {
                                Logger.LogInformation(
                                    $"Artist loaded: Name='{item.Name}', ID={item.Id}, Type={item.Type}");
                                // Check if this is the AC/DC artist
                                if (item.Name?.Contains("AC") == true || item.Name?.Contains("DC") == true)
                                {
                                    Logger.LogInformation($"Found AC/DC variant: '{item.Name}' with ID {item.Id}");
                                }
                            }
                        }
                    }

                    await RunOnUIThreadAsync(() =>
                    {
                        if (MediaItems != null)
                        {
                            MediaItems.AddRange(newItems.Where(item => item != null));
                        }
                        else
                        {
                            Logger?.LogError("MediaItems collection is null");
                        }
                    });
                    _currentStartIndex += newItems.Count;

                    await RunOnUIThreadAsync(() =>
                    {
                        HasMoreItems = newItems.Count == PageSize;
                        if (isFullRefresh)
                        {
                            TotalItemCount = result?.TotalRecordCount ?? newItems.Count;
                        }
                        else if (result?.TotalRecordCount.HasValue == true)
                        {
                            TotalItemCount = result.TotalRecordCount.Value;
                        }

                        // Notify property changes for item dimensions since they depend on _currentStartIndex
                        OnPropertyChanged(nameof(ItemWidth));
                        OnPropertyChanged(nameof(ItemHeight));
                        OnPropertyChanged(nameof(ItemTemplateName));
                    });
                }
                else
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        HasMoreItems = false;
                        if (isFullRefresh) { TotalItemCount = 0; }

                        UpdateEmptyState();
                    });
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, applyContext);
            }

            // Always run cleanup
            await RunOnUIThreadAsync(() =>
            {
                if (isFullRefresh)
                {
                    IsLoading = false;
                    // Only set HasLoadedOnce if we didn't cancel
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        HasLoadedOnce = true;
                    }
                }

                LoadMoreItemsCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsEmpty));
            });
        }

        private bool CanExecuteLoadMoreItems()
        {
            return HasMoreItems && !IsLoading && !IsLoadingMore;
        }

        private async Task ExecuteLoadMoreItemsAsync()
        {
            if (!CanExecuteLoadMoreItems())
            {
                return;
            }

            IsLoadingMore = true;
            IsError = false;
            ErrorMessage = string.Empty;
            Logger.LogInformation($"Loading more items - StartIndex: {_currentStartIndex}");

            var cancelContext = CreateErrorContext("CancelPreviousLoadMore");
            try
            {
                _applyFiltersCts?.Cancel();
                _applyFiltersCts?.Dispose();
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, cancelContext, false);
            }

            _applyFiltersCts = new CancellationTokenSource();
            var cancellationToken = _applyFiltersCts.Token;

            var loadMoreContext = CreateErrorContext("LoadMoreItems");
            try
            {
                var result = await FetchMediaItemsPageAsync(_currentStartIndex, PageSize, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var newItems = result?.Items?.ToList() ?? new List<BaseItemDto>();

                if (newItems.Any())
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        if (MediaItems != null)
                        {
                            MediaItems.AddRange(newItems.Where(item => item != null));
                        }
                        else
                        {
                            Logger?.LogError("MediaItems collection is null in ExecuteLoadMoreItemsAsync");
                        }
                    });
                    _currentStartIndex += newItems.Count;

                    await RunOnUIThreadAsync(() =>
                    {
                        HasMoreItems = newItems.Count == PageSize;
                        if (result?.TotalRecordCount.HasValue == true)
                        {
                            TotalItemCount = result.TotalRecordCount.Value;
                        }

                        // Notify property changes for item dimensions since they depend on _currentStartIndex
                        OnPropertyChanged(nameof(ItemWidth));
                        OnPropertyChanged(nameof(ItemHeight));
                        OnPropertyChanged(nameof(ItemTemplateName));
                    });
                }
                else
                {
                    await RunOnUIThreadAsync(() => { HasMoreItems = false; });
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, loadMoreContext);
            }

            // Always run cleanup
            await RunOnUIThreadAsync(() =>
            {
                IsLoadingMore = false;
                LoadMoreItemsCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsEmpty));
            });
        }

        private void ApplyLibraryTypeRestrictions(Dictionary<string, string> queryParams)
        {
            if (SelectedLibrary?.CollectionType == null)
            {
                return;
            }

            // For music libraries, default to showing artists only
            // The user can use filters to switch between Artists/Albums/Songs
            switch (SelectedLibrary.CollectionType)
            {
                case BaseItemDto_CollectionType.Movies:
                    queryParams["includeItemTypes"] = "Movie";
                    // Don't show collections by default in movie libraries
                    // Note: The API might still return BoxSets if they're in the library
                    // We rely on includeItemTypes to filter properly
                    break;
                case BaseItemDto_CollectionType.Tvshows:
                    queryParams["includeItemTypes"] = "Series";
                    break;
                case BaseItemDto_CollectionType.Music:
                    // Default to Artists view for music libraries
                    // This will be overridden by ApplyContentTypeFilterToParams if user selects Albums/Songs
                    if (CurrentFilter == "All" || CurrentFilter == "Artists")
                    {
                        queryParams["includeItemTypes"] = "MusicArtist";
                    }

                    break;
                case BaseItemDto_CollectionType.Musicvideos:
                    queryParams["includeItemTypes"] = "MusicVideo";
                    break;
                case BaseItemDto_CollectionType.Homevideos:
                    queryParams["includeItemTypes"] = "Video";
                    break;
                case BaseItemDto_CollectionType.Books:
                    queryParams["includeItemTypes"] = "Book";
                    break;
                case BaseItemDto_CollectionType.Photos:
                    queryParams["includeItemTypes"] = "Photo";
                    break;
                case BaseItemDto_CollectionType.Boxsets:
                    queryParams["includeItemTypes"] = "BoxSet";
                    break;
            }
        }

        private void ApplyContentTypeFilterToParams(Dictionary<string, string> queryParams)
        {
            // Apply content type filters based on CurrentFilter selection
            if (SelectedLibrary?.CollectionType == BaseItemDto_CollectionType.Music)
            {
                switch (CurrentFilter)
                {
                    case "Artists":
                        queryParams["includeItemTypes"] = "MusicArtist";
                        break;
                    case "Albums":
                        queryParams["includeItemTypes"] = "MusicAlbum";
                        break;
                    case "Songs":
                        queryParams["includeItemTypes"] = "Audio";
                        break;
                    case "All":
                        // For music, "All" defaults to Artists
                        queryParams["includeItemTypes"] = "MusicArtist";
                        break;
                }
            }
            else if (SelectedLibrary?.CollectionType == BaseItemDto_CollectionType.Tvshows)
            {
                switch (CurrentFilter)
                {
                    case "Series":
                        queryParams["includeItemTypes"] = "Series";
                        break;
                    case "Episodes":
                        queryParams["includeItemTypes"] = "Episode";
                        break;
                    case "All":
                        queryParams["includeItemTypes"] = "Series";
                        break;
                }
            }
            // For other library types, the filter is handled by ApplyLibraryTypeRestrictions
        }

        private void ApplyAlphabetFilterToParams(Dictionary<string, string> queryParams)
        {
            if (!string.IsNullOrEmpty(CurrentAlphabetFilter))
            {
                if (CurrentAlphabetFilter == "#")
                {
                    // For numbers and special characters
                    queryParams["nameStartsWith"] = "0,1,2,3,4,5,6,7,8,9";
                }
                else
                {
                    queryParams["nameStartsWith"] = CurrentAlphabetFilter;
                }
            }
        }

        private void ApplySortingToParams(Dictionary<string, string> queryParams)
        {
            // Apply sorting based on SelectedSortIndex
            // Default to SortName for alphabetical sorting
            var sortBy = "SortName"; // Default alphabetical

            switch (SelectedSortIndex)
            {
                case 0: // Name
                    sortBy = "SortName";
                    break;
                case 1: // Date Added
                    sortBy = "DateCreated";
                    break;
                case 2: // Release Date
                    sortBy = "PremiereDate,ProductionYear,SortName";
                    break;
                case 3: // Community Rating
                    sortBy = "CommunityRating,SortName";
                    break;
                case 4: // Critic Rating
                    sortBy = "CriticRating,SortName";
                    break;
                case 5: // Random
                    sortBy = "Random";
                    break;
            }

            queryParams["sortBy"] = sortBy;
            queryParams["sortOrder"] = IsAscending ? "Ascending" : "Descending";
        }

        private void ApplySpecialFiltersToParams(Dictionary<string, string> queryParams)
        {
            // This method can be used for any additional special filters
            // Currently empty but available for future use
        }

        private void SetFilter(string filter)
        {
            var context = CreateErrorContext("SetFilter", ErrorCategory.User);
            FireAndForget(async () =>
            {
                try
                {
                    CurrentFilter = filter ?? "All";
                    // Don't call ApplyFiltersAsync here - CurrentFilter setter already does it
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void SetAlphabetFilter(string letter)
        {
            var context = CreateErrorContext("SetAlphabetFilter", ErrorCategory.User);
            FireAndForget(async () =>
            {
                try
                {
                    CurrentAlphabetFilter = letter == CurrentAlphabetFilter ? string.Empty : letter;
                    // Alphabet filter should apply immediately as it's a quick filter outside the filter dialog
                    await ApplyFiltersAsync();
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        protected override async Task RefreshDataCoreAsync()
        {
            // Refresh applies the current filters
            await ApplyFiltersAsync().ConfigureAwait(false);
        }

        protected override async Task ClearDataCoreAsync()
        {
            await RunOnUIThreadAsync(() =>
            {
                MediaItems.Clear();
                Genres.Clear();
                Years.Clear();
                Ratings.Clear();
                Resolutions.Clear();
                PlayedStatuses.Clear();
                Decades.Clear();
                TotalItemCount = 0;
                HasMoreItems = false;
                CurrentAlphabetFilter = string.Empty;
            });
        }

        public void ClearAllFilters()
        {
            var context = CreateErrorContext("ClearAllFilters", ErrorCategory.User);
            FireAndForget(async () =>
            {
                try
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        foreach (var genre in Genres)
                        {
                            genre.IsSelected = false;
                        }

                        foreach (var year in Years)
                        {
                            year.IsSelected = false;
                        }

                        foreach (var decade in Decades)
                        {
                            decade.IsSelected = false;
                        }

                        foreach (var rating in Ratings)
                        {
                            rating.IsSelected = false;
                        }

                        foreach (var resolution in Resolutions)
                        {
                            resolution.IsSelected = false;
                        }

                        foreach (var status in PlayedStatuses)
                        {
                            status.IsSelected = false;
                        }
                    });
                    CurrentAlphabetFilter = string.Empty;
                    InvalidateFilterCountCache();
                    await ApplyFiltersAsync();
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void UpdateEmptyState()
        {
            if (IsEmpty)
            {
                if (!string.IsNullOrWhiteSpace(SearchTerm))
                {
                    EmptyStateTitle = "No results found";
                    EmptyStateMessage = $"No items match '{SearchTerm}'. Try a different search term.";
                }
                else if (HasActiveFilters)
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
        }

        #region IDisposable Implementation

        protected override void DisposeManaged()
        {
            // Unsubscribe from event handlers
            Genres.CollectionChanged -= OnGenresCollectionChanged;
            Years.CollectionChanged -= OnYearsCollectionChanged;
            Ratings.CollectionChanged -= OnRatingsCollectionChanged;
            Resolutions.CollectionChanged -= OnResolutionsCollectionChanged;
            PlayedStatuses.CollectionChanged -= OnPlayedStatusesCollectionChanged;
            Decades.CollectionChanged -= OnDecadesCollectionChanged;

            // Dispose CancellationTokenSources
            DisposeCancellationTokenSource(ref _applyFiltersCts);
            DisposeCancellationTokenSource(ref _loadLibrariesCts);
            DisposeCancellationTokenSource(ref _loadFiltersCts);
            DisposeCancellationTokenSource(ref _loadMoreCts);

            Logger?.LogInformation("LibraryViewModel disposed");

            base.DisposeManaged();
        }

        #endregion

        #region Event Handlers

        private void OnFilterCollectionChanged()
        {
            var context = CreateErrorContext("FilterCollectionChanged");
            FireAndForget(async () =>
            {
                try
                {
                    InvalidateFilterCountCache();
                    // Don't apply filters immediately - wait for Apply button
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void NotifyLibraryPropertiesChanged()
        {
            // Batch property notifications to reduce UI updates
            OnPropertyChanged(nameof(LibraryName));
            OnPropertyChanged(nameof(LibraryNameWithCount));
            OnPropertyChanged(nameof(ItemCountSubtitle));
            OnPropertyChanged(nameof(ShowShuffleButton));
            OnPropertyChanged(nameof(ItemWidth));
            OnPropertyChanged(nameof(ItemHeight));
        }

        #endregion

        #region Collection Changed Event Handlers

        private void OnGenresCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnFilterCollectionChanged();
        }

        private void OnYearsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnFilterCollectionChanged();
        }

        private void OnRatingsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnFilterCollectionChanged();
        }

        private void OnResolutionsCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnFilterCollectionChanged();
        }

        private void OnPlayedStatusesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnFilterCollectionChanged();
        }

        private void OnDecadesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            OnFilterCollectionChanged();
        }

        #endregion
    }
}
