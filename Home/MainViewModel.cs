using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Favorites;
using Gelatinarm.Library;
using Gelatinarm.Search;
using Gelatinarm.Settings;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.Shared.Server;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Home
{
    public partial class MainViewModel : BaseViewModel
    {
        private const int CacheValidityMinutes = 30;

        private readonly IAuthenticationService _authService;
        private readonly ICacheManagerService _cacheManager;
        private readonly IMediaDiscoveryService _mediaDiscoveryService;
        private readonly INavigationService _navigationService;
        private readonly IPreferencesService _preferencesService;
        private readonly IUserProfileService _userProfileService;

        // The Latest rows' order as they were last loaded; a change empties them
        private bool? _latestByReleaseDate;
        [ObservableProperty] private bool _hasContinueWatching;
        [ObservableProperty] private bool _hasLatestMovies;
        [ObservableProperty] private bool _hasLatestTvShows;
        [ObservableProperty] private bool _hasNextUp;
        [ObservableProperty] private bool _hasRecentlyAdded;
        [ObservableProperty] private bool _hasRecommended;

        public MainViewModel(IMediaDiscoveryService mediaDiscoveryService,
            INavigationService navigationService,
            IUserProfileService userProfileService,
            ILogger<MainViewModel> logger,
            ICacheManagerService cacheManager,
            IAuthenticationService authService,
            IPreferencesService preferencesService)
            : base(logger)
        {
            _mediaDiscoveryService = mediaDiscoveryService;
            _navigationService = navigationService;
            _userProfileService = userProfileService;
            _cacheManager = cacheManager;
            _authService = authService;
            _preferencesService = preferencesService;

            // A singleton that keeps its rows between visits: they are the previous user's after
            // a user or server change, and in the old order after the Latest setting changes
            _authService.UserChanged += OnUserChanged;
            _preferencesService.AppPreferencesChanged += OnAppPreferencesChanged;

            ContinueWatchingItems = new ObservableCollection<BaseItemDto>();
            LatestMovies = new ObservableCollection<BaseItemDto>();
            LatestTvShows = new ObservableCollection<BaseItemDto>();
            RecentlyAdded = new ObservableCollection<BaseItemDto>();
            Recommended = new ObservableCollection<BaseItemDto>();
            NextUpItems = new ObservableCollection<BaseItemDto>();
        }

        [RelayCommand]
        private void NavigateToSearch()
        {
            _navigationService.Navigate(typeof(SearchPage));
        }

        [RelayCommand]
        private void NavigateToFavorites()
        {
            _navigationService.Navigate(typeof(FavoritesPage));
        }

        [RelayCommand]
        private void NavigateToLibrary()
        {
            _navigationService.Navigate(typeof(LibrarySelectionPage));
        }

        [RelayCommand]
        private void NavigateToSettings()
        {
            _navigationService.Navigate(typeof(SettingsPage));
        }

        [RelayCommand]
        private void SwitchUser()
        {
            _navigationService.Navigate(typeof(ProfileSelectionPage));
        }

        // The authentication service's fact, read live; the Switch User button follows it
        public bool HasMultipleProfiles => _authService.HasMultipleSavedProfiles;

        public ObservableCollection<BaseItemDto> ContinueWatchingItems { get; }
        public ObservableCollection<BaseItemDto> LatestMovies { get; }
        public ObservableCollection<BaseItemDto> LatestTvShows { get; }
        public ObservableCollection<BaseItemDto> RecentlyAdded { get; }
        public ObservableCollection<BaseItemDto> Recommended { get; }
        public ObservableCollection<BaseItemDto> NextUpItems { get; }

        public Task LoadDataAsync(bool forceRefresh = false)
        {
            return base.LoadDataAsync(forceRefresh, TimeSpan.FromMinutes(CacheValidityMinutes));
        }

        /// <summary>
        ///     Every home-row load: BaseViewModel.LoadDataAsync owns the loading state, the 30-minute
        ///     reuse window and cancelling a superseded load; each row's freshness is the discovery
        ///     service's.
        /// </summary>
        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            UpdateHasMultipleProfiles();

            if (!_userProfileService.GetCurrentUserGuid().HasValue)
            {
                Logger.LogError("No signed-in user - home sections not loaded");
                return;
            }

            _latestByReleaseDate = (await _preferencesService.GetAppPreferencesAsync().ConfigureAwait(false)).SortLatestByReleaseDate;

            // Each row updates as soon as its own data arrives
            await Task.WhenAll(
                LoadContinueWatchingAsync(cancellationToken),
                LoadSectionAsync("NextUp", ct => _mediaDiscoveryService.GetNextUpAsync(ct),
                    NextUpItems, v => HasNextUp = v, cancellationToken),
                LoadSectionAsync("LatestMovies", ct => _mediaDiscoveryService.GetLatestMoviesAsync(ct),
                    LatestMovies, v => HasLatestMovies = v, cancellationToken),
                LoadSectionAsync("LatestShows", ct => _mediaDiscoveryService.GetLatestShowsAsync(ct),
                    LatestTvShows, v => HasLatestTvShows = v, cancellationToken),
                LoadSectionAsync("RecentlyAdded", ct => _mediaDiscoveryService.GetRecentlyAddedAsync(ct),
                    RecentlyAdded, v => HasRecentlyAdded = v, cancellationToken),
                LoadSectionAsync("Recommended", ct => _mediaDiscoveryService.GetRecommendedAsync(ct),
                    Recommended, v => HasRecommended = v, cancellationToken)
            ).ConfigureAwait(false);
        }

        /// <summary>
        ///     Shows the Switch User button only when other saved profiles exist on this device
        /// </summary>
        private void UpdateHasMultipleProfiles()
        {
            FireAndForget(() => RunOnUIThreadAsync(() => OnPropertyChanged(nameof(HasMultipleProfiles))));
        }

        private void OnUserChanged(object sender, EventArgs e)
        {
            ClearCache();
        }

        private void OnAppPreferencesChanged(object sender, AppPreferences preferences)
        {
            if (_latestByReleaseDate != null && _latestByReleaseDate != preferences.SortLatestByReleaseDate)
            {
                ClearCache();
            }
        }

        /// <summary>
        ///     Continue Watching alone, on every return to the home page: what was just played
        ///     moves there. The discovery service does not cache it.
        /// </summary>
        public async Task RefreshContinueWatchingAsync()
        {
            try
            {
                Logger.LogDebug("Refreshing Continue Watching section only");
                await LoadContinueWatchingAsync(DisposalCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer refresh or by disposal
            }
        }

        private Task LoadContinueWatchingAsync(CancellationToken cancellationToken)
        {
            return LoadSectionAsync("ContinueWatching",
                ct => _mediaDiscoveryService.GetContinueWatchingAsync(ct),
                ContinueWatchingItems, v => HasContinueWatching = v, cancellationToken);
        }

        /// <summary>
        ///     Empties the home screen (a user or server change): the next load goes to the server.
        /// </summary>
        private void ClearCache()
        {
            HasData = false;

            FireAndForget(() => RunOnUIThreadAsync(() =>
            {
                ClearSections();
                HasContinueWatching = false;
                HasLatestMovies = false;
                HasLatestTvShows = false;
                HasRecentlyAdded = false;
                HasRecommended = false;
                HasNextUp = false;
            }));
        }

        private void ClearSections()
        {
            ContinueWatchingItems.Clear();
            LatestMovies.Clear();
            LatestTvShows.Clear();
            RecentlyAdded.Clear();
            Recommended.Clear();
            NextUpItems.Clear();
        }

        public void RefreshData()
        {
            Logger.LogInformation("Manual refresh requested - clearing cached home data");

            ClearCache();
            _cacheManager.Clear();

            FireAndForget(() => LoadDataAsync(true));
        }

        /// <summary>
        ///     Fills one home-screen row from the discovery service, retrying transient failures. A
        ///     failure leaves that row as it was; the other rows load independently.
        /// </summary>
        private async Task LoadSectionAsync(string section,
            Func<CancellationToken, Task<IEnumerable<BaseItemDto>>> fetch,
            ObservableCollection<BaseItemDto> target, Action<bool> setHasItems,
            CancellationToken cancellationToken)
        {
            try
            {
                var items = await RetryHelper.ExecuteWithRetryAsync(() => fetch(cancellationToken), Logger,
                                cancellationToken, HomeConstants.HomeRowRetryAttempts,
                                TimeSpan.FromMilliseconds(HomeConstants.HomeRowRetryDelayMs),
                                section).ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                await RunOnUIThreadAsync(() =>
                {
                    target.ReplaceAll(items);
                    setHasItems(target.Count > 0);
                    Logger.LogDebug("Updated {Section}: {ItemCount} items", section, target.Count);
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext($"Load{section}"), false);
            }
        }

        protected override void DisposeManaged()
        {
            _authService.UserChanged -= OnUserChanged;
            _preferencesService.AppPreferencesChanged -= OnAppPreferencesChanged;
            ClearSections();

            base.DisposeManaged();
        }
    }
}
