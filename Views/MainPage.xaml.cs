using System;
using System.Collections;
using System.ComponentModel;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Services;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Gelatinarm.Views
{
    public sealed partial class MainPage : BasePage
    {
        private readonly IMusicPlayerService _musicPlayerService;
        private IMediaPlaybackService _mediaPlaybackService;

        private IUnifiedDeviceService _unifiedDeviceService;

        static MainPage()
        {
        }

        public MainPage() : base(typeof(MainPage))
        {
            InitializeComponent(); _unifiedDeviceService = GetRequiredService<IUnifiedDeviceService>();
            _mediaPlaybackService = GetService<IMediaPlaybackService>();
            _musicPlayerService = GetService<IMusicPlayerService>();

            // Subscribe to events
            Loaded += MainPage_Loaded;
            DataContextChanged += MainPage_DataContextChanged;
            KeyDown += MainPage_KeyDown;

            // Subscribe to ViewModel property changes for focus navigation updates
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        // Static flag to track if content was watched
        internal static bool ContentWasWatched { get; set; }

        protected override Type ViewModelType => typeof(MainViewModel);
        public MainViewModel ViewModel => (MainViewModel)base.ViewModel;

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            Logger?.LogInformation("MainPage Loaded event fired");

            try
            {
                if (ViewModel != null)
                {
                    Logger?.LogInformation(
                        $"MainPage loaded - Collections: ContinueWatching={ViewModel.ContinueWatchingItems?.Count ?? 0}, Movies={ViewModel.LatestMovies?.Count ?? 0}, TVShows={ViewModel.LatestTVShows?.Count ?? 0}");
                }
                else
                {
                    Logger?.LogError("MainPage loaded but ViewModel is null");
                }

                CheckUIElementBinding();
                // Focus navigation setup removed - wasn't working properly
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in MainPage_Loaded");
            }
        }

        private void MainPage_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            Logger?.LogInformation($"MainPage DataContext changed to: {args.NewValue?.GetType().Name ?? "NULL"}");
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Update focus navigation when section visibility changes
            if (e.PropertyName == nameof(MainViewModel.HasContinueWatching) ||
                e.PropertyName == nameof(MainViewModel.HasNextUp) ||
                e.PropertyName == nameof(MainViewModel.HasLatestMovies) ||
                e.PropertyName == nameof(MainViewModel.HasLatestTVShows))
            {
                // Focus navigation setup removed - wasn't working properly
            }
        }

        private void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Prevent B button from navigating back on MainPage
            if (e.Key == VirtualKey.GamepadB)
            {
                Logger?.LogInformation("MainPage: GamepadB pressed - preventing back navigation");
                e.Handled = true;
            }
        }

        protected override bool HandleBackNavigation(BackRequestedEventArgs e)
        {
            // Prevent back navigation from MainPage
            Logger?.LogInformation("MainPage: Back navigation prevented");
            e.Handled = true;
            return true;
        }


        private void MediaItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is BaseItemDto item)
            {
                NavigateToDetailsPage(item);
            }
        }

        private void GridView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto item)
            {
                // All items navigate to their respective details pages
                NavigateToDetailsPage(item);
            }
        }

        private void CheckUIElementBinding()
        {
            try
            {
                var continueWatchingElement = FindName("ContinueWatchingItems");
                var latestMoviesElement = FindName("LatestMovies");
                var latestTVShowsElement = FindName("LatestTVShows");

                Logger?.LogInformation(
                    $"UI Elements found - ContinueWatching: {continueWatchingElement != null}, Movies: {latestMoviesElement != null}, TVShows: {latestTVShowsElement != null}");

                if (continueWatchingElement is ListView continueWatchingListView)
                {
                    if (continueWatchingListView.ItemsSource is ICollection collection)
                    {
#if DEBUG
                        Logger?.LogDebug($"ContinueWatching ListView Items Count: {collection.Count}");
#endif
                    }
                }

                Logger?.LogInformation("UI element binding check completed");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error checking UI element binding");
            }
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BaseItemDto item)
            {
                NavigateToDetailsPage(item);
            }
        }

        private void NavigateToDetailsPage(BaseItemDto item)
        {
            if (item == null)
            {
                return;
            }

            Logger?.LogInformation(
                $"Navigating to details for {item.Name} (Type: {item.Type}, MediaType: {item.MediaType})");

            // Navigate to item details
            NavigationService.NavigateToItemDetails(item);
        }

        // SetupFocusNavigation method removed - wasn't working properly



        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            Logger?.LogInformation("Manual refresh requested by user");
            ViewModel?.RefreshData();
        }



        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            Logger?.LogInformation("MainPage: OnNavigatedFrom called");
        }

        protected override async Task InitializePageAsync(object parameter)
        {
            Logger?.LogInformation($"MainPage: InitializePageAsync called with parameter: {parameter}");

            // Hide back button since this is the main page
            var systemNavigationManager = SystemNavigationManager.GetForCurrentView();
            systemNavigationManager.AppViewBackButtonVisibility = AppViewBackButtonVisibility.Collapsed;

            // Check if we're coming from login by looking for specific parameter
            // or by checking if the ViewModel doesn't have data yet (first time loading)
            var hasExistingData = ViewModel?.ContinueWatchingItems?.Count > 0 ||
                                  ViewModel?.LatestMovies?.Count > 0 ||
                                  ViewModel?.LatestTVShows?.Count > 0;
            var isComingFromLogin = parameter?.ToString() == "FromLogin" || !hasExistingData;

            // Log the actual state
            Logger?.LogInformation(
                $"MainPage: BackStack count = {Frame.BackStack.Count}, Parameter = '{parameter}', HasExistingData = {hasExistingData}, IsComingFromLogin = {isComingFromLogin}");

            // Clear navigation back stack to prevent returning to login
            Frame.BackStack.Clear();
            Logger?.LogInformation("MainPage: Cleared navigation back stack to prevent returning to login");

            // Only clear cache when actually coming from login (no existing data)
            if (isComingFromLogin && !hasExistingData)
            {
                Logger?.LogInformation("MainPage: Coming from login - forcing refresh and clearing cache");
                ViewModel?.ClearCache();
            }
            else
            {
                Logger?.LogInformation("MainPage: Re-initializing after navigation - keeping existing cache and data");
            }

            // Subscribe to mini player service events

            await Task.CompletedTask;
        }

        protected override async Task RefreshDataAsync(bool forceRefresh)
        {
            Logger?.LogInformation($"MainPage: RefreshDataAsync called (ForceRefresh: {forceRefresh})");

            // Handle ContentWasWatched flag logic
            var shouldForceRefresh = forceRefresh;
            if (ContentWasWatched)
            {
                Logger?.LogInformation("MainPage: Content was watched - forcing refresh to update Continue Watching");
                shouldForceRefresh = true;
                ContentWasWatched = false; // Reset the flag
            }

            try
            {
                await Task.Delay(RetryConstants.UI_RENDER_DELAY_MS);

                if (ViewModel != null)
                {
                    Logger?.LogInformation($"MainPage: Loading data (ForceRefresh: {shouldForceRefresh})");

                    if (!shouldForceRefresh)
                    {
                        // Load cached data first then refresh Continue Watching only
                        FireAndForget(async () =>
                        {
                            await ViewModel.LoadDataAsync(); // Load cached data
                            await ViewModel.RefreshContinueWatchingAsync(); // Then refresh Continue Watching only
                        }, "LoadDataAndRefreshContinueWatching");
                    }
                    else
                    {
                        // Normal load with force refresh
                        FireAndForget(() => ViewModel.LoadDataAsync(shouldForceRefresh), "LoadDataAsync");
                    }
                }
                else
                {
                    Logger?.LogError("Cannot load data - ViewModel is null in RefreshDataAsync");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in RefreshDataAsync");
            }
        }

        protected override async Task OnNavigatedBackAsync()
        {
            Logger?.LogInformation("MainPage: OnNavigatedBackAsync called");

            // Ensure the page has focus when navigating back
            await Task.Delay(RetryConstants.UI_FOCUS_READY_DELAY_MS); // Small delay to ensure UI is ready
            Focus(FocusState.Programmatic);
        }

        protected override void CleanupResources()
        {
            Logger?.LogInformation("MainPage: CleanupResources called");

            // Unsubscribe from page events to prevent memory leaks
            Loaded -= MainPage_Loaded;
            DataContextChanged -= MainPage_DataContextChanged;
            KeyDown -= MainPage_KeyDown;

            // Unsubscribe from ViewModel property changes
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            // Unsubscribe from mini player service

            Logger?.LogInformation("MainPage: Cleaned up all event handlers");
        }


        /// <summary>
        ///     Execute a fire-and-forget dispatcher operation with error handling
        /// </summary>
        private void FireAndForgetDispatcher(Func<Task> asyncAction,
            CoreDispatcherPriority priority = CoreDispatcherPriority.Normal, string operationName = null)
        {
            AsyncHelper.FireAndForget(async () => await UIHelper.RunOnUIThreadAsync(async () =>
            {
                try
                {
                    await asyncAction();
                }
                catch (Exception ex)
                {
                    var operation = operationName ?? asyncAction.Method?.Name ?? "Unknown";
                    Logger?.LogError(ex, $"Fire-and-forget dispatcher operation failed in MainPage.{operation}");
                }
            }, Dispatcher, Logger), Logger, GetType());
        }
    }
}
