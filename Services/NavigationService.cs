using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Views;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Gelatinarm.Services
{
    public interface INavigationService : IDisposable
    {
        bool CanGoBack { get; }
        event EventHandler<Type> Navigated;
        void Initialize(Frame frame);
        bool Navigate(Type pageType, object parameter = null);
        Task<bool> NavigateAsync(Type pageType, object parameter = null);
        void NavigateToHome();
        void NavigateToSettings();
        void NavigateToSearch(object parameter = null);
        void NavigateToFavorites();
        void NavigateToLibrary();
        void NavigateToItemDetails(BaseItemDto item);
        bool GoBack();
        object GetLastNavigationParameter();
    }

    /// <summary>
    ///     Navigation service that combines navigation and state management
    /// </summary>
    public class NavigationService : BaseService, INavigationService, INavigationStateService
    {
        private readonly HashSet<string> _navigationHistory = new();
        private readonly SemaphoreSlim _navigationSemaphore = new(1, 1);
        private readonly Stack<(Type PageType, object Parameter)> _navigationStack = new();
        private readonly TimeSpan _navigationThrottleTime = TimeSpan.FromMilliseconds(500);
        private readonly Dictionary<string, Type> _pageMap;

        // State management
        private readonly Dictionary<Type, object> _returnStates = new();
        private readonly IServiceProvider _serviceProvider;
        private readonly object _stackLock = new(); // Synchronize stack access
        private PlaybackSession _currentPlaybackSession;
        private Frame _frame;
        private Type _lastNavigatedPageType;
        private object _lastNavigationParameter;
        private DateTime _lastNavigationTime = DateTime.MinValue;
        private (Guid? libraryId, string libraryName, string libraryType) _librarySelection;
        private Type _pendingNavigationPageType;

        public NavigationService(ILogger<NavigationService> logger, IServiceProvider serviceProvider) : base(logger)
        {
            _serviceProvider = serviceProvider;
            _pageMap = new Dictionary<string, Type>
            {
                { "MainPage", typeof(MainPage) },
                { "SettingsPage", typeof(SettingsPage) },
                { "SearchPage", typeof(SearchPage) },
                { "FavoritesPage", typeof(FavoritesPage) },
                { "LibraryPage", typeof(LibraryPage) },
                { "LibrarySelectionPage", typeof(LibrarySelectionPage) },
                { "MovieDetailsPage", typeof(MovieDetailsPage) },
                { "MediaPlayerPage", typeof(MediaPlayerPage) },
                { "SeasonDetailsPage", typeof(SeasonDetailsPage) },
                { "AlbumDetailsPage", typeof(AlbumDetailsPage) },
                { "CollectionDetailsPage", typeof(CollectionDetailsPage) },
                { "ServerSelectionPage", typeof(ServerSelectionPage) },
                { "LoginPage", typeof(LoginPage) },
                { "QuickConnectInstructionsPage", typeof(QuickConnectInstructionsPage) },
                { "PersonDetailsPage", typeof(PersonDetailsPage) },
                { "ArtistDetailsPage", typeof(ArtistDetailsPage) }
            };
        }

        public event EventHandler<Type> Navigated;
        public bool CanGoBack => _frame?.CanGoBack ?? false;

        public void Dispose()
        {
            if (_frame != null)
            {
                _frame.Navigated -= OnNavigated;
            }

            _navigationSemaphore?.Dispose();
        }

        #region Private Methods

        private void CleanupMediaPlayerBackStack()
        {
            try
            {
                if (_frame?.BackStack == null || !_frame.BackStack.Any())
                {
                    return;
                }

                // Count MediaPlayerPage entries in back stack
                var mediaPlayerCount = _frame.BackStack.Count(entry => entry.SourcePageType == typeof(MediaPlayerPage));

                // If we have more than 2 MediaPlayerPage entries, remove the oldest ones
                if (mediaPlayerCount > 2)
                {
                    Logger.LogInformation($"Cleaning up MediaPlayerPage back stack entries (found {mediaPlayerCount})");

                    // Remove oldest MediaPlayerPage entries, keeping only the most recent 2
                    var toRemove = mediaPlayerCount - 2;
                    for (var i = 0; i < _frame.BackStack.Count && toRemove > 0; i++)
                    {
                        if (_frame.BackStack[i].SourcePageType == typeof(MediaPlayerPage))
                        {
                            _frame.BackStack.RemoveAt(i);
                            i--; // Adjust index after removal
                            toRemove--;
                        }
                    }

                    Logger.LogInformation($"Removed {mediaPlayerCount - 2} old MediaPlayerPage entries");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error cleaning up MediaPlayerPage back stack");
            }
        }

        #endregion

        #region INavigationService Implementation

        public void Initialize(Frame frame)
        {
            if (frame == null)
            {
                Logger.LogError("NavigationService.Initialize: frame is null");
                throw new ArgumentNullException(nameof(frame));
            }

            try
            {
                _frame = frame;
                _frame.Navigated += OnNavigated;
                Logger.LogInformation("NavigationService initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "NavigationService.Initialize failed");
                throw;
            }
        }

        public bool Navigate(Type pageType, object parameter = null)
        {
            if (!_navigationSemaphore.Wait(TimeSpan.FromSeconds(RetryConstants.NAVIGATION_TIMEOUT_SECONDS)))
            {
                Logger.LogWarning("Navigate: Navigation timeout - another navigation in progress");
                return false;
            }

            try
            {
                return NavigateCore(pageType, parameter);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Navigate failed");
                return false;
            }
            finally
            {
                _navigationSemaphore.Release();
            }
        }

        private bool NavigateCore(Type pageType, object parameter = null)
        {
            Logger.LogInformation(
                "NavigationService.Navigate: Attempting to navigate to {PageType} with parameter: {Parameter}",
                pageType?.FullName, parameter);

            if (pageType == null)
            {
                Logger.LogError("NavigationService.Navigate: pageType is null");
                return false;
            }

            if (_frame == null)
            {
                Logger.LogError("NavigationService.Navigate: Frame is not initialized");
                return false;
            }

            // Check if we should allow navigation to the same page
            var isBackNavigation = _frame.CurrentSourcePageType == pageType &&
                                   _frame.BackStackDepth > 0 &&
                                   _frame.BackStack.Any() &&
                                   _frame.BackStack[_frame.BackStack.Count - 1].SourcePageType != pageType;

            // Special handling for BaseItemDto parameters
            var isSameParameter = false;
            if (parameter is BaseItemDto newItem &&
                _lastNavigationParameter is BaseItemDto lastItem)
            {
                // Compare by ID for BaseItemDto objects
                isSameParameter = newItem.Id == lastItem.Id;
            }
            else
            {
                // Default comparison for other types
                isSameParameter = (parameter == null && _lastNavigationParameter == null) ||
                                  (parameter != null && parameter.Equals(_lastNavigationParameter));
            }

            // Only prevent navigation if we're not in a back navigation scenario
            if (!isBackNavigation && _lastNavigatedPageType == pageType && isSameParameter)
            {
                Logger.LogInformation("Already on {PageType} with same parameter, skipping navigation", pageType.Name);
                return false;
            }

            // Prevent duplicate navigation within throttle time
            var now = DateTime.UtcNow;
            if (_pendingNavigationPageType == pageType && now - _lastNavigationTime < _navigationThrottleTime)
            {
                Logger.LogInformation(
                    $"Duplicate navigation to {pageType.Name} detected within {_navigationThrottleTime.TotalMilliseconds}ms, ignoring");
                return false;
            }

            _pendingNavigationPageType = pageType;
            _lastNavigationTime = now;

            // Check for circular navigation patterns
            var navigationKey = $"{pageType.FullName}:{parameter?.GetHashCode() ?? 0}";
            if (_navigationHistory.Contains(navigationKey))
            {
                Logger.LogWarning("Potential circular navigation detected for {PageType}", pageType.Name);
                // Clear history to allow navigation but prevent loops
                _navigationHistory.Clear();
            }

            // Special handling for MediaPlayerPage to prevent memory buildup
            bool isEpisodeToEpisodeNavigation = false;
            if (pageType == typeof(MediaPlayerPage))
            {
                // Check if we're navigating from MediaPlayerPage to MediaPlayerPage (episode to episode)
                isEpisodeToEpisodeNavigation = _frame.CurrentSourcePageType == typeof(MediaPlayerPage);
                if (isEpisodeToEpisodeNavigation)
                {
                    Logger.LogInformation("Detected episode-to-episode navigation");
                }

                CleanupMediaPlayerBackStack();
            }

            // Limit back stack depth
            if (_frame.BackStackDepth > UIConstants.MAX_BACK_STACK_DEPTH)
            {
                Logger.LogInformation("Trimming back stack (current depth: {Depth})", _frame.BackStackDepth);

                // Remove oldest entries from back stack
                while (_frame.BackStackDepth > UIConstants.MAX_BACK_STACK_DEPTH - 1)
                {
                    if (_frame.BackStack.Any())
                    {
                        _frame.BackStack.RemoveAt(0);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            // Store navigation state BEFORE navigation
            _lastNavigatedPageType = pageType;
            _lastNavigationParameter = parameter;

            // For forward navigation, push to stack BEFORE navigating
            // This ensures the parameter is available immediately when the page loads
            lock (_stackLock)
            {
                _navigationStack.Push((pageType, parameter));
                Logger.LogInformation(
                    $"Pre-navigation stack push for {pageType.Name} - stack depth: {_navigationStack.Count}");
            }

            // Navigate using the frame
            var result = false;
            try
            {
                result = _frame.Navigate(pageType, parameter);
            }
            catch (Exception navEx)
            {
                Logger.LogError(navEx, "Navigation failed for {PageType}", pageType?.FullName);
                throw;
            }

            if (result)
            {
                Logger.LogInformation("Successfully navigated to {PageType}", pageType?.FullName);
                _navigationHistory.Add(navigationKey);

                // For episode-to-episode navigation, remove the previous MediaPlayerPage from back stack
                if (isEpisodeToEpisodeNavigation && _frame.BackStack.Count > 0)
                {
                    var lastIndex = _frame.BackStack.Count - 1;
                    if (_frame.BackStack[lastIndex].SourcePageType == typeof(MediaPlayerPage))
                    {
                        _frame.BackStack.RemoveAt(lastIndex);
                        Logger.LogInformation("Removed previous MediaPlayerPage from back stack after episode-to-episode navigation");
                    }
                }

                // Keep navigation history size reasonable
                if (_navigationHistory.Count > UIConstants.MAX_BACK_STACK_DEPTH * 2)
                {
                    _navigationHistory.Clear();
                }
            }
            else
            {
                Logger.LogWarning("Failed to navigate to {PageType}", pageType?.FullName);
            }

            return result;
        }

        public async Task<bool> NavigateAsync(Type pageType, object parameter = null)
        {
            // Use async wait with timeout
            using (var cts = new CancellationTokenSource(
                       TimeSpan.FromSeconds(RetryConstants.NAVIGATION_TIMEOUT_SECONDS)))
            {
                try
                {
                    await _navigationSemaphore.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Logger.LogWarning("NavigateAsync: Navigation timeout - another navigation in progress");
                    return false;
                }
            }

            try
            {
                Logger.LogInformation("NavigateAsync: Attempting to navigate to {PageType} with parameter: {Parameter}",
                    pageType?.FullName, parameter);

                if (pageType == null)
                {
                    Logger.LogError("NavigateAsync: pageType is null");
                    return false;
                }

                if (_frame == null)
                {
                    Logger.LogError("NavigateAsync: Frame is not initialized");
                    return false;
                }

                // Navigation must happen on UI thread
                var result = false;
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    // Call NavigateCore directly to avoid double semaphore acquisition
                    result = NavigateCore(pageType, parameter);
                }, logger: Logger);

                return result;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "NavigateAsync failed");
                return false;
            }
            finally
            {
                _navigationSemaphore.Release();
            }
        }

        public void NavigateToHome()
        {
            Navigate(typeof(MainPage));
        }

        public void NavigateToSettings()
        {
            Navigate(typeof(SettingsPage));
        }

        public void NavigateToSearch(object parameter = null)
        {
            Navigate(typeof(SearchPage), parameter);
        }

        public void NavigateToFavorites()
        {
            Navigate(typeof(FavoritesPage));
        }

        public void NavigateToLibrary()
        {
            Navigate(typeof(LibrarySelectionPage));
        }

        public void NavigateToItemDetails(BaseItemDto item)
        {
            if (item == null)
            {
                Logger.LogWarning("NavigateToItemDetails called with null item.");
                return;
            }

            if (!item.Id.HasValue)
            {
                Logger.LogWarning($"NavigateToItemDetails called for item '{item.Name}' with no ID.");
                return;
            }

            var itemId = item.Id.Value.ToString();
            Type pageType = null;

            switch (item.Type)
            {
                case BaseItemDto_Type.Movie:
                    // Always go to movie details page
                    pageType = typeof(MovieDetailsPage);
                    break;
                case BaseItemDto_Type.Series:
                    pageType = typeof(SeasonDetailsPage);
                    break;
                case BaseItemDto_Type.Episode:
                    // Episodes navigate to SeasonDetailsPage to show episode in context
                    pageType = typeof(SeasonDetailsPage);
                    break;
                case BaseItemDto_Type.Season:
                    pageType = typeof(SeasonDetailsPage);
                    break;
                case BaseItemDto_Type.Audio: // For individual songs
                    // Use MusicPlayer for songs instead of navigating
                    AsyncHelper.FireAndForget(async () =>
                    {
                        try
                        {
                            var musicPlayerService = _serviceProvider.GetService<IMusicPlayerService>();
                            if (musicPlayerService == null)
                            {
                                Logger.LogError("MusicPlayerService not available");
                                return;
                            }

                            Logger.LogInformation($"Playing song '{item.Name}' with MusicPlayer");
                            await musicPlayerService.PlayItem(item);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, $"Error playing song '{item.Name}' with MusicPlayer");
                        }
                    }, Logger, typeof(NavigationService));
                    return;
                case BaseItemDto_Type.MusicAlbum:
                    pageType = typeof(AlbumDetailsPage);
                    break;
                case BaseItemDto_Type.MusicArtist:
                    pageType = typeof(ArtistDetailsPage);
                    break;
                case BaseItemDto_Type.Person:
                    pageType = typeof(PersonDetailsPage);
                    break;
                case BaseItemDto_Type.BoxSet: // Collection of movies/shows
                    pageType = typeof(CollectionDetailsPage);
                    break;
                default:
                    Logger.LogWarning(
                        $"NavigateToItemDetails: Unknown item type '{item.Type}' for item '{item.Name}' (ID: {itemId}). No navigation action defined.");
                    return;
            }

            if (pageType != null)
            {
                Logger.LogInformation(
                    $"Navigating to page type {pageType.Name} for item '{item.Name}' (ID: {itemId}).");
                // Pass the full item object instead of just the ID to avoid extra API calls
                Navigate(pageType, item);
            }
        }

        public bool GoBack()
        {
            if (_frame?.CanGoBack == true)
            {
                _frame.GoBack();
                return true;
            }

            return false;
        }

        public object GetLastNavigationParameter()
        {
            lock (_stackLock)
            {
                // This method is called after back navigation has occurred
                // At this point, the stack has been popped and the current top is our page
                if (_navigationStack.Count > 0)
                {
                    var current = _navigationStack.Peek();

                    // Check if we're being called from the page that matches the stack top
                    // This ensures we return the correct parameter for the current page
                    if (_frame?.Content?.GetType() == current.PageType ||
                        _frame?.CurrentSourcePageType == current.PageType)
                    {
                        Logger.LogInformation(
                            $"GetLastNavigationParameter returning parameter for current page {current.PageType?.Name}");
                        return current.Parameter;
                    }
                    else
                    {
                        Logger.LogWarning(
                            $"GetLastNavigationParameter called but frame content ({_frame?.Content?.GetType()?.Name}) doesn't match stack top ({current.PageType?.Name})");
                    }
                }
            }

            // Fallback to last navigation parameter
            Logger.LogInformation("GetLastNavigationParameter using fallback parameter");
            return _lastNavigationParameter;
        }

        private void OnNavigated(object sender, NavigationEventArgs e)
        {
            try
            {
                Logger.LogInformation(
                    $"Frame navigated to {e.SourcePageType?.Name} (NavigationMode: {e.NavigationMode})");

                lock (_stackLock)
                {
                    // Handle navigation modes
                    if (e.NavigationMode == NavigationMode.Back)
                    {
                        if (_navigationStack.Count > 1)
                        {
                            // Pop the page we're leaving
                            _navigationStack.Pop();

                            // Update last navigation info to the page we're going back to
                            if (_navigationStack.Count > 0)
                            {
                                var (pageType, parameter) = _navigationStack.Peek();
                                _lastNavigatedPageType = pageType;
                                _lastNavigationParameter = parameter;

                                Logger.LogInformation(
                                    $"Back navigation restored state for {pageType.Name} - stack depth: {_navigationStack.Count}");
                            }
                        }
                    }
                    else if (e.NavigationMode == NavigationMode.New)
                    {
                        // For new navigation, stack was already pushed in Navigate method
                        Logger.LogInformation(
                            $"New navigation to {e.SourcePageType?.Name} confirmed - stack depth: {_navigationStack.Count}");
                    }
                    else if (e.NavigationMode == NavigationMode.Forward)
                    {
                        // Forward navigation after going back
                        _navigationStack.Push((e.SourcePageType, e.Parameter));
                        Logger.LogInformation(
                            $"Forward navigation to {e.SourcePageType?.Name} - stack depth: {_navigationStack.Count}");
                    }
                }

                Navigated?.Invoke(this, e.SourcePageType);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in OnNavigated event handler");
            }
        }

        #endregion

        #region INavigationStateService Implementation

        public void SavePlaybackSession(PlaybackSession session)
        {
            _currentPlaybackSession = session;
            Logger.LogInformation($"Saved playback session: {session.SessionId}, " +
                                  $"OriginatingPage: {session.OriginatingPage?.Name}, " +
                                  $"Queue: {session.Queue?.Count ?? 0} items, " +
                                  $"IsMultiEpisode: {session.IsMultiEpisodeSession}");
        }

        public PlaybackSession GetCurrentPlaybackSession()
        {
            return _currentPlaybackSession;
        }

        public void ClearPlaybackSession()
        {
            Logger.LogInformation($"Clearing playback session: {_currentPlaybackSession?.SessionId}");
            _currentPlaybackSession = null;
        }

        public void SaveReturnState(Type pageType, object state)
        {
            _returnStates[pageType] = state;
            Logger.LogInformation($"Saved return state for {pageType.Name}");
        }

        public object GetAndClearReturnState(Type pageType)
        {
            if (_returnStates.TryGetValue(pageType, out var state))
            {
                _returnStates.Remove(pageType);
                Logger.LogInformation($"Retrieved and cleared return state for {pageType.Name}");
                return state;
            }

            return null;
        }

        public bool HasReturnState(Type pageType)
        {
            return _returnStates.ContainsKey(pageType);
        }

        public void SaveLibrarySelection(Guid? libraryId, string libraryName, string libraryType)
        {
            _librarySelection = (libraryId, libraryName, libraryType);
            Logger.LogInformation($"Saved library selection: {libraryName} ({libraryType})");
        }

        public (Guid? libraryId, string libraryName, string libraryType) GetLibrarySelection()
        {
            return _librarySelection;
        }

        #endregion

        #region Additional Navigation Helpers

        public Type GetPageType(string pageKey)
        {
            return _pageMap.TryGetValue(pageKey, out var pageType) ? pageType : null;
        }

        public string GetPageKey(Type pageType)
        {
            return _pageMap.FirstOrDefault(x => x.Value == pageType).Key;
        }

        public IReadOnlyDictionary<string, Type> GetPageMap()
        {
            return _pageMap;
        }

        public IReadOnlyCollection<string> GetNavigationHistory()
        {
            return _navigationHistory;
        }

        public (Type pageType, object parameter) GetLastNavigation()
        {
            return (_lastNavigatedPageType, _lastNavigationParameter);
        }

        #endregion
    }
}
