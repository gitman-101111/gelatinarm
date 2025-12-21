using System;
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
using Windows.ApplicationModel.Core;
// Add System.Threading for CancellationTokenSource

namespace Gelatinarm.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        private const int CACHE_VALIDITY_MINUTES = 30;

        private readonly TimeSpan _cacheExpiration =
            TimeSpan.FromMinutes(RetryConstants.MAIN_VIEW_CACHE_EXPIRATION_MINUTES);

        private readonly ICacheManagerService _cacheManager;
        private readonly JellyfinApiClient _jellyfinApiClient;
        private readonly ILogger<MainViewModel> _logger;
        private readonly IMediaDiscoveryService _mediaDiscoveryService;
        private readonly INavigationService _navigationService;
        private readonly IUserProfileService _userProfileService;
        private bool _hasContinueWatching = false;
        private bool _hasLatestMovies = false;
        private bool _hasLatestTVShows = false;
        private bool _hasLoadedData = false;
        private bool _hasNextUp = false;
        private bool _hasRecentlyAdded = false;
        private bool _hasRecommended = false;
        private DateTime _lastDataLoadTime = DateTime.MinValue;

        public MainViewModel(IMediaDiscoveryService mediaDiscoveryService,
            INavigationService navigationService,
            JellyfinApiClient jellyfinApiClient,
            IUserProfileService userProfileService,
            ILogger<MainViewModel> logger,
            ICacheManagerService cacheManager = null)
            : base(logger)
        {
            _mediaDiscoveryService =
                mediaDiscoveryService ?? throw new ArgumentNullException(nameof(mediaDiscoveryService));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _jellyfinApiClient = jellyfinApiClient ?? throw new ArgumentNullException(nameof(jellyfinApiClient));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cacheManager = cacheManager;

            ContinueWatchingItems = new ObservableCollection<BaseItemDto>();
            LatestMovies = new ObservableCollection<BaseItemDto>();
            LatestTVShows = new ObservableCollection<BaseItemDto>();
            RecentlyAdded = new ObservableCollection<BaseItemDto>();
            Recommended = new ObservableCollection<BaseItemDto>();
            NextUpItems = new ObservableCollection<BaseItemDto>();

            HasContinueWatching = false;
            HasLatestMovies = false;
            HasLatestTVShows = false;
            HasRecentlyAdded = false;
            HasRecommended = false;
            HasNextUp = false;
            IsLoading = false;

            // Event handlers - will be unsubscribed in Dispose
            ContinueWatchingItems.CollectionChanged += OnContinueWatchingItemsChanged;
            LatestMovies.CollectionChanged += OnLatestMoviesChanged;
            LatestTVShows.CollectionChanged += OnLatestTVShowsChanged;
            RecentlyAdded.CollectionChanged += OnRecentlyAddedChanged;
            Recommended.CollectionChanged += OnRecommendedChanged;

            NavigateToSearchCommand = new RelayCommand(() => _navigationService?.NavigateToSearch());
            NavigateToFavoritesCommand = new RelayCommand(() => _navigationService?.NavigateToFavorites());
            NavigateToLibraryCommand = new RelayCommand(() => _navigationService?.NavigateToLibrary());
            NavigateToSettingsCommand = new RelayCommand(() => _navigationService?.NavigateToSettings());
            ContinueWatchingItemClickCommand = new RelayCommand<BaseItemDto>(OnContinueWatchingItemClick);
            MovieItemClickCommand = new RelayCommand<BaseItemDto>(OnMovieItemClick);
            TVShowItemClickCommand = new RelayCommand<BaseItemDto>(OnTVShowItemClick);

            // Commands initialized
        }

        public bool HasContinueWatching
        {
            get => _hasContinueWatching;
            set => SetProperty(ref _hasContinueWatching, value);
        }

        public bool HasLatestMovies
        {
            get => _hasLatestMovies;
            set => SetProperty(ref _hasLatestMovies, value);
        }

        public bool HasLatestTVShows
        {
            get => _hasLatestTVShows;
            set => SetProperty(ref _hasLatestTVShows, value);
        }

        public bool HasRecentlyAdded
        {
            get => _hasRecentlyAdded;
            set => SetProperty(ref _hasRecentlyAdded, value);
        }

        public bool HasRecommended
        {
            get => _hasRecommended;
            set => SetProperty(ref _hasRecommended, value);
        }

        public bool HasNextUp
        {
            get => _hasNextUp;
            set => SetProperty(ref _hasNextUp, value);
        }

        public ObservableCollection<BaseItemDto> ContinueWatchingItems { get; }
        public ObservableCollection<BaseItemDto> LatestMovies { get; }
        public ObservableCollection<BaseItemDto> LatestTVShows { get; }
        public ObservableCollection<BaseItemDto> RecentlyAdded { get; }
        public ObservableCollection<BaseItemDto> Recommended { get; }
        public ObservableCollection<BaseItemDto> NextUpItems { get; }

        public ICommand NavigateToSearchCommand { get; }
        public ICommand NavigateToFavoritesCommand { get; }
        public ICommand NavigateToLibraryCommand { get; }
        public ICommand NavigateToSettingsCommand { get; }
        public ICommand ContinueWatchingItemClickCommand { get; }
        public ICommand MovieItemClickCommand { get; }
        public ICommand TVShowItemClickCommand { get; }

        public async Task LoadDataAsync(bool forceRefresh = false)
        {
            // Use the standardized base class LoadDataAsync
            await base.LoadDataAsync(forceRefresh, TimeSpan.FromMinutes(CACHE_VALIDITY_MINUTES));
        }

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            // Always force refresh when LoadDataCoreAsync is called - this ensures data loads when ViewModel is recreated
            await LoadHomeDataAsync(true);
        }

        protected override async Task RefreshDataCoreAsync()
        {
            // Clear cache and reload
            // Cache cleared through CacheManagerService
            _cacheManager?.Clear();
            await LoadHomeDataAsync(true);
        }

        protected override async Task ClearDataCoreAsync()
        {
            await RunOnUIThreadAsync(() =>
            {
                ContinueWatchingItems?.Clear();
                LatestMovies?.Clear();
                LatestTVShows?.Clear();
                RecentlyAdded?.Clear();
                Recommended?.Clear();
                NextUpItems?.Clear();

                HasContinueWatching = false;
                HasLatestMovies = false;
                HasLatestTVShows = false;
                HasRecentlyAdded = false;
                HasRecommended = false;
                HasNextUp = false;
            });

            // Cache cleared through CacheManagerService
            _hasLoadedData = false;
            _lastDataLoadTime = DateTime.MinValue;
        }

        private async Task LoadHomeDataAsync(bool forceRefresh = false)
        {
            var cancellationToken = DisposalCts.Token;

            var context = CreateErrorContext("InitializeLoadData");
            bool initialized;
            try
            {
                // LoadDataAsync started

                if (CoreApplication.MainView?.CoreWindow?.Dispatcher == null)
                {
                    _logger?.LogError("Dispatcher is null in LoadDataAsync");
                    initialized = false;
                }
                else
                {
                    // Batch initial UI updates
                    await UpdateUIAsync(() =>
                    {
                        IsError = false;
                        ErrorMessage = string.Empty;
                    }).ConfigureAwait(false);
                    initialized = true;
                }
            }
            catch (Exception ex)
            {
                initialized = await ErrorHandler.HandleErrorAsync(ex, context, false, false);
            }

            if (!initialized)
            {
                return;
            }

            if (!forceRefresh && _hasLoadedData)
            {
                var timeSinceLastLoad = DateTime.Now - _lastDataLoadTime;
                if (timeSinceLastLoad.TotalMinutes < CACHE_VALIDITY_MINUTES)
                {
                    // Using cached home screen data
                    return;
                }
            }

            var loadingContext = CreateErrorContext("SetLoadingState", ErrorCategory.User);
            try
            {
                // Set loading state
                await UpdateUIAsync(() => IsLoading = true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, loadingContext, false);
            }

            var dataContext = CreateErrorContext("LoadHomeData");
            try
            {
                if (_userProfileService == null)
                {
                    _logger?.LogError("UserProfileService is null in LoadDataAsync");
                    throw new InvalidOperationException("User profile service is not available");
                }

                if (_mediaDiscoveryService == null)
                {
                    _logger?.LogError("MediaDiscoveryService is null in LoadDataAsync");
                    throw new InvalidOperationException("Media discovery service is not available");
                }

                var userGuid = _userProfileService.GetCurrentUserGuid();
                if (!userGuid.HasValue)
                {
                    var profileLoaded = await _userProfileService.LoadUserProfileAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!profileLoaded)
                    {
                        _logger?.LogError("Failed to load user profile");
                        return;
                    }

                    userGuid = _userProfileService.GetCurrentUserGuid();
                    if (!userGuid.HasValue)
                    {
                        _logger?.LogError("User profile loaded without a user ID");
                        return;
                    }
                }

                // Loading data for user

                // Batch API calls into priority groups
                // Priority 1: Continue Watching and Next Up (most important for user engagement)
                var priority1Tasks = new List<Task<object>>(2); // Always 2 tasks

                var getContinueWatchingTask = GetOrFetchCachedAsync(
                    "ContinueWatching",
                    async () => await BaseService.RetryAsync(
                        () => _mediaDiscoveryService.GetContinueWatchingAsync(20, cancellationToken),
                        _logger,
                        2,
                        TimeSpan.FromMilliseconds(RetryConstants.QUICK_CONNECT_POLL_DELAY_MS),
                        cancellationToken,
                        nameof(_mediaDiscoveryService.GetContinueWatchingAsync)).ConfigureAwait(false),
                    cancellationToken);
                priority1Tasks.Add(getContinueWatchingTask);

                var getNextUpTask = GetOrFetchCachedAsync(
                    "NextUp",
                    async () => await BaseService.RetryAsync(
                        () => _mediaDiscoveryService.GetNextUpAsync(cancellationToken),
                        _logger,
                        2,
                        TimeSpan.FromMilliseconds(RetryConstants.QUICK_CONNECT_POLL_DELAY_MS),
                        cancellationToken,
                        nameof(_mediaDiscoveryService.GetNextUpAsync)).ConfigureAwait(false),
                    cancellationToken);
                priority1Tasks.Add(getNextUpTask);

                // Wait for priority 1 to complete
                await Task.WhenAll(priority1Tasks).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                // Priority 2: Latest content
                var priority2Tasks = new List<Task<object>>(3); // Always 3 tasks

                var getLatestMoviesTask = GetOrFetchCachedAsync(
                    "LatestMovies",
                    async () => await BaseService.RetryAsync(
                        () => _mediaDiscoveryService.GetLatestMoviesAsync(20, cancellationToken),
                        _logger,
                        2,
                        TimeSpan.FromMilliseconds(RetryConstants.QUICK_CONNECT_POLL_DELAY_MS),
                        cancellationToken,
                        nameof(_mediaDiscoveryService.GetLatestMoviesAsync)).ConfigureAwait(false),
                    cancellationToken);
                priority2Tasks.Add(getLatestMoviesTask);

                var getLatestShowsTask = GetOrFetchCachedAsync(
                    "LatestShows",
                    async () => await BaseService.RetryAsync(
                        () => _mediaDiscoveryService.GetLatestShowsAsync(20, cancellationToken),
                        _logger,
                        2,
                        TimeSpan.FromMilliseconds(RetryConstants.QUICK_CONNECT_POLL_DELAY_MS),
                        cancellationToken,
                        nameof(_mediaDiscoveryService.GetLatestShowsAsync)).ConfigureAwait(false),
                    cancellationToken);
                priority2Tasks.Add(getLatestShowsTask);

                var getRecentlyAddedTask = GetOrFetchCachedAsync(
                    "RecentlyAdded",
                    async () => await BaseService.RetryAsync(
                        () => _mediaDiscoveryService.GetRecentlyAddedAsync(20, cancellationToken),
                        _logger,
                        2,
                        TimeSpan.FromMilliseconds(RetryConstants.QUICK_CONNECT_POLL_DELAY_MS),
                        cancellationToken,
                        nameof(_mediaDiscoveryService.GetRecentlyAddedAsync)).ConfigureAwait(false),
                    cancellationToken);
                priority2Tasks.Add(getRecentlyAddedTask);

                // Wait for priority 2 to complete
                await Task.WhenAll(priority2Tasks).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                // Priority 3: Recommendations (can be loaded last)
                var getRecommendedTask = GetOrFetchCachedAsync(
                    "Recommended",
                    async () => await BaseService.RetryAsync(
                        () => _mediaDiscoveryService.GetRecommendedAsync(20, cancellationToken),
                        _logger,
                        2,
                        TimeSpan.FromMilliseconds(RetryConstants.QUICK_CONNECT_POLL_DELAY_MS),
                        cancellationToken,
                        nameof(_mediaDiscoveryService.GetRecommendedAsync)).ConfigureAwait(false),
                    cancellationToken);

                await getRecommendedTask.ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested(); // Check before processing results

                // Get results from completed tasks
                var continueWatching = await getContinueWatchingTask.ConfigureAwait(false) as IEnumerable<BaseItemDto>;
                var latestMovies = await getLatestMoviesTask.ConfigureAwait(false) as IEnumerable<BaseItemDto>;
                var latestTVShows = await getLatestShowsTask.ConfigureAwait(false) as IEnumerable<BaseItemDto>;
                var recentlyAdded = await getRecentlyAddedTask.ConfigureAwait(false) as IEnumerable<BaseItemDto>;
                var recommended = await getRecommendedTask.ConfigureAwait(false) as IEnumerable<BaseItemDto>;
                var nextUp = await getNextUpTask.ConfigureAwait(false) as IEnumerable<BaseItemDto>;

                _logger?.LogInformation(
                    $"Fetched data - ContinueWatching: {continueWatching?.Count() ?? 0}, Movies: {latestMovies?.Count() ?? 0}, TVShows: {latestTVShows?.Count() ?? 0}");

                await RunOnUIThreadAsync(() =>
                {
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        if (ContinueWatchingItems != null)
                        {
                            ContinueWatchingItems.ReplaceAll(continueWatching ?? Enumerable.Empty<BaseItemDto>());
                            HasContinueWatching = ContinueWatchingItems.Any();
                            _logger?.LogInformation(
                                $"Updated ContinueWatching: {ContinueWatchingItems.Count} items, HasContinueWatching: {HasContinueWatching}");
                        }

                        if (LatestMovies != null)
                        {
                            LatestMovies.ReplaceAll(latestMovies ?? Enumerable.Empty<BaseItemDto>());
                            HasLatestMovies = LatestMovies.Any();
                            _logger?.LogInformation(
                                $"Updated LatestMovies: {LatestMovies.Count} items, HasLatestMovies: {HasLatestMovies}");
                        }

                        if (LatestTVShows != null)
                        {
                            LatestTVShows.ReplaceAll(latestTVShows ?? Enumerable.Empty<BaseItemDto>());
                            HasLatestTVShows = LatestTVShows.Any();
                            _logger?.LogInformation(
                                $"Updated LatestTVShows: {LatestTVShows.Count} items, HasLatestTVShows: {HasLatestTVShows}");
                        }

                        if (RecentlyAdded != null)
                        {
                            RecentlyAdded.ReplaceAll(recentlyAdded ?? Enumerable.Empty<BaseItemDto>());
                            HasRecentlyAdded = RecentlyAdded.Any();
                            _logger?.LogInformation(
                                $"Updated RecentlyAdded: {RecentlyAdded.Count} items, HasRecentlyAdded: {HasRecentlyAdded}");
                        }

                        if (Recommended != null)
                        {
                            Recommended.ReplaceAll(recommended ?? Enumerable.Empty<BaseItemDto>());
                            HasRecommended = Recommended.Any();
                            _logger?.LogInformation(
                                $"Updated Recommended: {Recommended.Count} items, HasRecommended: {HasRecommended}");
                        }

                        if (NextUpItems != null)
                        {
                            NextUpItems.ReplaceAll(nextUp ?? Enumerable.Empty<BaseItemDto>());
                            HasNextUp = NextUpItems.Any();
                            _logger?.LogInformation(
                                $"Updated NextUpItems: {NextUpItems.Count} items, HasNextUp: {HasNextUp}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error updating collections in UI thread");
                    }
                });

                // Data loading completed successfully

                await UpdateUIAsync(() =>
                {
                    _hasLoadedData = true;
                    _lastDataLoadTime = DateTime.Now;
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, dataContext);
            }

            // Clear loading state in finally
            var finallyContext = CreateErrorContext("ClearLoadingState", ErrorCategory.User);
            try
            {
                await UpdateUIAsync(() => IsLoading = false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, finallyContext, false);
            }
            // LoadDataAsync completed
        }

        public async Task RefreshContinueWatchingAsync()
        {
            var context = CreateErrorContext("RefreshContinueWatching");
            try
            {
                _logger?.LogInformation("Refreshing Continue Watching section only");

                var cancellationToken = DisposalCts?.Token ?? default;

                // Clear cache for Continue Watching only
                _cacheManager?.Remove(GetCacheKey("ContinueWatching"));

                // Fetch fresh Continue Watching data
                var continueWatching = await BaseService.RetryAsync(
                    () => _mediaDiscoveryService.GetContinueWatchingAsync(20, cancellationToken),
                    _logger,
                    2,
                    TimeSpan.FromMilliseconds(RetryConstants.QUICK_CONNECT_POLL_DELAY_MS),
                    cancellationToken,
                    nameof(_mediaDiscoveryService.GetContinueWatchingAsync)).ConfigureAwait(false);

                // Update UI on UI thread
                await RunOnUIThreadAsync(() =>
                {
                    ContinueWatchingItems.Clear();
                    if (continueWatching != null)
                    {
                        foreach (var item in continueWatching)
                        {
                            ContinueWatchingItems.Add(item);
                        }
                    }

                    HasContinueWatching = ContinueWatchingItems.Any();
                    _logger?.LogInformation($"Refreshed Continue Watching with {ContinueWatchingItems.Count} items");
                });

                // Cache the refreshed data
                if (continueWatching != null)
                {
                    _cacheManager?.Set(GetCacheKey("ContinueWatching"), continueWatching,
                        TimeSpan.FromMinutes(CACHE_VALIDITY_MINUTES));
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public void ClearCache()
        {
            // Clearing MainViewModel cache
            _hasLoadedData = false;
            _lastDataLoadTime = DateTime.MinValue;

            // Clear cache through CacheManagerService
            _cacheManager?.Clear();

            // Clear all collections
            _ = RunOnUIThreadAsync(() =>
            {
                ContinueWatchingItems?.Clear();
                LatestMovies?.Clear();
                LatestTVShows?.Clear();
                RecentlyAdded?.Clear();
                Recommended?.Clear();
                NextUpItems?.Clear();
            });

            // Reset visibility flags
            _ = RunOnUIThreadAsync(() =>
            {
                HasContinueWatching = false;
                HasLatestMovies = false;
                HasLatestTVShows = false;
                HasRecentlyAdded = false;
                HasRecommended = false;
                HasNextUp = false;
            });

            // Cache cleared
        }

        private void OnContinueWatchingItemClick(BaseItemDto item)
        {
            var context = CreateErrorContext("ContinueWatchingItemClick", ErrorCategory.User);
            FireAndForget(async () =>
            {
                try
                {
                    // Continue watching item clicked

                    if (item == null || !item.Id.HasValue)
                    {
                        _logger?.LogWarning("OnContinueWatchingItemClick called with null item or missing ID");
                        return;
                    }

                    if (_navigationService == null)
                    {
                        _logger?.LogError("NavigationService is null in OnContinueWatchingItemClick");
                        await UpdateUIAsync(() =>
                        {
                            IsError = true;
                            ErrorMessage = "Navigation service is not available.";
                        }).ConfigureAwait(false);
                        return;
                    }

                    // Navigate to details pages for all item types
                    _navigationService.NavigateToItemDetails(item);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context);
                }
            });
        }

        private void OnMovieItemClick(BaseItemDto item)
        {
            var context = CreateErrorContext("MovieItemClick", ErrorCategory.User);
            FireAndForget(async () =>
            {
                try
                {
                    // Movie item clicked

                    if (item == null || !item.Id.HasValue)
                    {
                        _logger?.LogWarning("OnMovieItemClick called with null item or missing ID");
                        return;
                    }

                    if (_navigationService == null)
                    {
                        _logger?.LogError("NavigationService is null in OnMovieItemClick");
                        await UpdateUIAsync(() =>
                        {
                            IsError = true;
                            ErrorMessage = "Navigation service is not available.";
                        }).ConfigureAwait(false);
                        return;
                    }

                    // Navigate to movie details page
                    _navigationService.NavigateToItemDetails(item);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context);
                }
            });
        }

        private void OnTVShowItemClick(BaseItemDto item)
        {
            var context = CreateErrorContext("TVShowItemClick", ErrorCategory.User);
            FireAndForget(async () =>
            {
                try
                {
                    // TV show item clicked

                    if (item == null || !item.Id.HasValue)
                    {
                        _logger?.LogWarning("OnTVShowItemClick called with null item or missing ID");
                        return;
                    }

                    if (_navigationService == null)
                    {
                        _logger?.LogError("NavigationService is null in OnTVShowItemClick");
                        await UpdateUIAsync(() =>
                        {
                            IsError = true;
                            ErrorMessage = "Navigation service is not available.";
                        }).ConfigureAwait(false);
                        return;
                    }

                    // Navigate to TV show details page
                    _navigationService.NavigateToItemDetails(item);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context);
                }
            });
        }

        public bool GoBack()
        {
            if (_navigationService == null)
            {
                _logger?.LogError("NavigationService is null in GoBack");
                return false;
            }

            return _navigationService.GoBack();
        }

        public void RefreshData()
        {
            var context = CreateErrorContext("RefreshData", ErrorCategory.User);
            FireAndForget(async () =>
            {
                try
                {
                    _logger?.LogInformation("Manual refresh requested - clearing all caches including images");

                    // Clear all caches to force complete refresh
                    ClearCache();

                    // Clear the image cache as well for a complete refresh
                    await ImageHelper.ClearCacheAsync();
                    _logger?.LogInformation("Image cache cleared for complete refresh");

                    // Force refresh all data
                    await LoadHomeDataAsync(true);
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context);
                }
            });
        }

        private string GetCacheKey(string key)
        {
            return $"MainViewModel_{key}";
        }

        private async Task UpdateUIAsync(Action action)
        {
            if (CoreApplication.MainView?.CoreWindow?.Dispatcher == null)
            {
                _logger?.LogWarning("Dispatcher is null in UpdateUIAsync");
                return;
            }

            await RunOnUIThreadAsync(() =>
            {
                var context = CreateErrorContext("UpdateUI", ErrorCategory.User);
                FireAndForget(async () =>
                {
                    try
                    {
                        action?.Invoke();
                        await Task.CompletedTask;
                    }
                    catch (Exception ex)
                    {
                        await ErrorHandler.HandleErrorAsync(ex, context, false);
                    }
                });
            });
        }

        private async Task<object> GetOrFetchCachedAsync(string cacheKey, Func<Task<object>> fetchFunc,
            CancellationToken cancellationToken)
        {
            // Use centralized cache manager if available
            if (_cacheManager != null)
            {
                var cachedData = _cacheManager.Get<object>($"MainViewModel_{cacheKey}");
                if (cachedData != null)
                {
                    if (cachedData is IEnumerable<BaseItemDto> cachedItems)
                    {
                        _logger?.LogInformation(
                            $"Using cached data from CacheManager for {cacheKey}: {cachedItems.Count()} items");
                    }
                    else
                    {
                        _logger?.LogInformation(
                            $"Using cached data from CacheManager for {cacheKey} (type: {cachedData.GetType().Name})");
                    }

                    return cachedData;
                }

                // Fetch fresh data
                _logger?.LogInformation($"Fetching fresh data for {cacheKey}");
                var data = await fetchFunc().ConfigureAwait(false);

                if (data is IEnumerable<BaseItemDto> items)
                {
                    _logger?.LogInformation($"Fetched {items.Count()} items for {cacheKey}");
                }

                // Store in centralized cache
                _cacheManager.Set($"MainViewModel_{cacheKey}", data, _cacheExpiration);

                return data;
            }

            // No cache manager available, just fetch fresh data
            _logger?.LogWarning("CacheManager not available, fetching data without caching");
            return await fetchFunc().ConfigureAwait(false);
        }

        #region IDisposable Implementation

        protected override void DisposeManaged()
        {
            // Cancel any pending operations

            // Unsubscribe from events to prevent memory leaks
            if (ContinueWatchingItems != null)
            {
                ContinueWatchingItems.CollectionChanged -= OnContinueWatchingItemsChanged;
            }

            if (LatestMovies != null)
            {
                LatestMovies.CollectionChanged -= OnLatestMoviesChanged;
            }

            if (LatestTVShows != null)
            {
                LatestTVShows.CollectionChanged -= OnLatestTVShowsChanged;
            }

            if (RecentlyAdded != null)
            {
                RecentlyAdded.CollectionChanged -= OnRecentlyAddedChanged;
            }

            if (Recommended != null)
            {
                Recommended.CollectionChanged -= OnRecommendedChanged;
            }

            // Clear collections to free memory
            ContinueWatchingItems?.Clear();
            LatestMovies?.Clear();
            LatestTVShows?.Clear();
            RecentlyAdded?.Clear();
            Recommended?.Clear();
            NextUpItems?.Clear();

            base.DisposeManaged();
        }

        #endregion

        #region Event Handlers

        private void OnContinueWatchingItemsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            var context = CreateErrorContext("ContinueWatchingItemsChanged");
            FireAndForget(async () =>
            {
                try
                {
                    // ContinueWatchingItems collection changed
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void OnLatestMoviesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            var context = CreateErrorContext("LatestMoviesChanged");
            FireAndForget(async () =>
            {
                try
                {
                    // LatestMovies collection changed
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void OnLatestTVShowsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            var context = CreateErrorContext("LatestTVShowsChanged");
            FireAndForget(async () =>
            {
                try
                {
                    // LatestTVShows collection changed
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void OnRecentlyAddedChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            var context = CreateErrorContext("RecentlyAddedChanged");
            FireAndForget(async () =>
            {
                try
                {
                    // RecentlyAdded collection changed
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void OnRecommendedChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            var context = CreateErrorContext("RecommendedChanged");
            FireAndForget(async () =>
            {
                try
                {
                    // Recommended collection changed
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        #endregion
    }
}
