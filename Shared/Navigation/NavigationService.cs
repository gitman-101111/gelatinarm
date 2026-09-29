using System;
using System.Linq;
using System.Threading;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Gelatinarm.Details;
using Gelatinarm.Music;
using Gelatinarm.Player;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Server;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Navigation
{
    public interface INavigationService : IDisposable
    {
        bool CanGoBack { get; }
        bool IsNavigating { get; }
        void Initialize(Frame frame);
        bool Navigate(Type pageType, object parameter = null);
        void NavigateToItemDetails(BaseItemDto item);
        bool GoBack();
        void ClearBackStack();
        object GetLastNavigationParameter();
    }

    /// <summary>
    ///     Page navigation over the app's root Frame
    /// </summary>
    public class NavigationService : BaseService, INavigationService
    {
        private readonly SemaphoreSlim _navigationSemaphore = new(1, 1);
        private readonly TimeSpan _navigationThrottleTime = TimeSpan.FromMilliseconds(500);
        private int _navigationInProgress;

        private Frame _frame;
        private object _lastNavigationParameter;
        private DateTime _lastNavigationTime = DateTime.MinValue;
        private Type _pendingNavigationPageType;

        public NavigationService(ILogger<NavigationService> logger) : base(logger)
        {
        }

        public bool CanGoBack => _frame?.CanGoBack == true;
        public bool IsNavigating => _navigationInProgress == 1;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_frame != null)
                {
                    _frame.Navigated -= OnNavigated;
                }

                _navigationSemaphore.Dispose();
            }

            base.Dispose(disposing);
        }

        private void CleanupMediaPlayerBackStack()
        {
            try
            {
                if (_frame?.BackStack is not { Count: > 0 })
                {
                    return;
                }

                var mediaPlayerCount = _frame.BackStack.Count(entry => entry.SourcePageType == typeof(MediaPlayerPage));

                if (mediaPlayerCount > 2)
                {
                    Logger.LogDebug("Cleaning up MediaPlayerPage back stack entries (found {MediaPlayerCount})", mediaPlayerCount);

                    // Oldest first: the back stack starts at the oldest entry
                    var toRemove = mediaPlayerCount - 2;
                    var i = 0;
                    while (i < _frame.BackStack.Count && toRemove > 0)
                    {
                        if (_frame.BackStack[i].SourcePageType == typeof(MediaPlayerPage))
                        {
                            _frame.BackStack.RemoveAt(i);
                            toRemove--;
                        }
                        else
                        {
                            i++;
                        }
                    }

                    Logger.LogDebug("Removed {MediaPlayerCount} old MediaPlayerPage entries", mediaPlayerCount - 2);
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("CleanupMediaPlayerBackStack"));
            }
        }

        public void Initialize(Frame frame)
        {
            _frame = frame ?? throw new ArgumentNullException(nameof(frame));
            _frame.Navigated += OnNavigated;
        }

        public bool Navigate(Type pageType, object parameter = null)
        {
            if (!_navigationSemaphore.Wait(TimeSpan.FromSeconds(RetryConstants.NavigationTimeoutSeconds)))
            {
                Logger.LogWarning("Navigate: Navigation timeout - another navigation in progress");
                return false;
            }

            try
            {
                Interlocked.Exchange(ref _navigationInProgress, 1);
                return NavigateCore(pageType, parameter);
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("Navigate"));
                return false;
            }
            finally
            {
                Interlocked.Exchange(ref _navigationInProgress, 0);
                _navigationSemaphore.Release();
            }
        }

        private bool NavigateCore(Type pageType, object parameter)
        {
            Logger.LogDebug(
                "NavigationService.Navigate: Attempting to navigate to {PageType} with parameter: {Parameter}",
                pageType.FullName, parameter);

            if (_frame == null)
            {
                Logger.LogError("NavigationService.Navigate: Frame is not initialized");
                return false;
            }

            var isBackNavigation = _frame.CurrentSourcePageType == pageType &&
                                   _frame.BackStackDepth > 0 &&
                                   _frame.BackStack.Count > 0 &&
                                   _frame.BackStack[_frame.BackStack.Count - 1].SourcePageType != pageType;

            bool isSameParameter;
            if (parameter is BaseItemDto newItem &&
                _lastNavigationParameter is BaseItemDto lastItem)
            {
                isSameParameter = newItem.Id == lastItem.Id;
            }
            else
            {
                isSameParameter = Equals(parameter, _lastNavigationParameter);
            }

            if (!isBackNavigation && _frame.CurrentSourcePageType == pageType && isSameParameter)
            {
                Logger.LogDebug("Already on {PageType} with same parameter, skipping navigation", pageType.Name);
                return false;
            }

            var now = DateTime.UtcNow;
            if (_pendingNavigationPageType == pageType && now - _lastNavigationTime < _navigationThrottleTime)
            {
                Logger.LogDebug(
                    "Duplicate navigation to {PageTypeName} detected within {NavigationThrottleTimeTotalMilliseconds}ms, ignoring", pageType.Name, _navigationThrottleTime.TotalMilliseconds);
                return false;
            }

            _pendingNavigationPageType = pageType;
            _lastNavigationTime = now;

            // Special handling for MediaPlayerPage to prevent memory buildup
            var isEpisodeToEpisodeNavigation = false;
            if (pageType == typeof(MediaPlayerPage))
            {
                isEpisodeToEpisodeNavigation = _frame.CurrentSourcePageType == typeof(MediaPlayerPage);
                if (isEpisodeToEpisodeNavigation)
                {
                    Logger.LogDebug("Detected episode-to-episode navigation");
                }

                CleanupMediaPlayerBackStack();
            }

            if (_frame.BackStackDepth > UiConstants.MaxBackStackDepth)
            {
                Logger.LogDebug("Trimming back stack (current depth: {Depth})", _frame.BackStackDepth);

                while (_frame.BackStack.Count > UiConstants.MaxBackStackDepth - 1)
                {
                    _frame.BackStack.RemoveAt(0);
                }
            }

            var result = _frame.Navigate(pageType, parameter);

            if (result)
            {
                Logger.LogInformation("Successfully navigated to {PageType}", pageType.FullName);

                if (isEpisodeToEpisodeNavigation && _frame.BackStack.Count > 0)
                {
                    var lastIndex = _frame.BackStack.Count - 1;
                    if (_frame.BackStack[lastIndex].SourcePageType == typeof(MediaPlayerPage))
                    {
                        _frame.BackStack.RemoveAt(lastIndex);
                        Logger.LogDebug(
                            "Removed previous MediaPlayerPage from back stack after episode-to-episode navigation");
                    }
                }
                else if (pageType != typeof(MediaPlayerPage) &&
                         _frame.BackStack.Count > 0 &&
                         _frame.BackStack[_frame.BackStack.Count - 1].SourcePageType == typeof(MediaPlayerPage))
                {
                    // Leaving MediaPlayerPage for a non-player page: remove it from the back stack.
                    // There is no "return to stopped video" UX worth preserving.
                    _frame.BackStack.RemoveAt(_frame.BackStack.Count - 1);
                    Logger.LogDebug("Removed MediaPlayerPage from back stack after leaving playback");

                    // After MediaPlayerPage is removed, check whether the new back-stack top is the same
                    // page type as our destination and represents the same series context (e.g. two
                    // SeasonDetailsPage entries for the same show). Collapse the duplicate so the user
                    // doesn't have to press Back through a page they've already seen.
                    if (_frame.BackStack.Count > 0 &&
                        _frame.BackStack[_frame.BackStack.Count - 1].SourcePageType == pageType &&
                        IsSameNavigationContext(_frame.BackStack[_frame.BackStack.Count - 1].Parameter, parameter))
                    {
                        _frame.BackStack.RemoveAt(_frame.BackStack.Count - 1);
                        Logger.LogDebug(
                            "Collapsed duplicate {PageTypeName} from back stack (same series context)", pageType.Name);
                    }
                }
            }
            else
            {
                Logger.LogWarning("Failed to navigate to {PageType}", pageType.FullName);
            }

            return result;
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
                Logger.LogWarning("NavigateToItemDetails called for item '{ItemName}' with no ID.", item.Name);
                return;
            }

            var itemId = item.Id.Value.ToString();
            Type pageType;

            switch (item.Type)
            {
                case BaseItemDto_Type.Movie:
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
                case BaseItemDto_Type.Audio:
                    Logger.LogDebug("Playing song '{ItemName}' with MusicPlayer", item.Name);
                    FireAndForget(() => ServiceLocator.GetRequiredService<IMusicPlayerService>().PlayItemAsync(item), "PlayMusicItem");
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
                        "NavigateToItemDetails: Unknown item type '{ItemType}' for item '{ItemName}' (ID: {ItemId}). No navigation action defined.", item.Type, item.Name, itemId);
                    return;
            }

            Logger.LogDebug(
                "Navigating to page type {PageTypeName} for item '{ItemName}' (ID: {ItemId}).", pageType.Name, item.Name, itemId);
            // The full item rather than its ID: the page needs no extra request for it
            Navigate(pageType, item);
        }

        /// <summary>
        ///     Forgets all history behind the current page, so back navigation cannot return to it.
        ///     Used after sign-out, where the pages behind belong to a user who is no longer signed in.
        /// </summary>
        public void ClearBackStack()
        {
            _frame?.BackStack.Clear();
            _frame?.ForwardStack.Clear();
            UpdateBackButton();
            Logger.LogDebug("Navigation back stack cleared");
        }

        /// <summary>
        ///     The title-bar back button (shown on desktop, not on Xbox) follows the back stack
        /// </summary>
        private void UpdateBackButton()
        {
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                _frame?.CanGoBack == true ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
        }

        public bool GoBack()
        {
            if (_frame?.CanGoBack == true)
            {
                try
                {
                    Interlocked.Exchange(ref _navigationInProgress, 1);
                    _frame.GoBack();
                    return true;
                }
                finally
                {
                    Interlocked.Exchange(ref _navigationInProgress, 0);
                }
            }

            return false;
        }

        /// <summary>
        ///     The parameter the page now shown was navigated with, including on back navigation,
        ///     where the page receives none of its own.
        /// </summary>
        public object GetLastNavigationParameter()
        {
            return _lastNavigationParameter;
        }

        private void OnNavigated(object sender, NavigationEventArgs e)
        {
            try
            {
                Logger.LogDebug(
                    "Frame navigated to {SourcePageTypeName} (NavigationMode: {ENavigationMode})", e.SourcePageType?.Name, e.NavigationMode);

                // The Frame exposes the current page type but not its parameter, so the parameter
                // is taken here, in every mode: entries are removed from Frame.BackStack directly
                // (the player page after playback, collapsed duplicates, trimming), which a copy
                // tracked on our own navigations cannot follow. Navigated fires before the new
                // page's OnNavigatedTo, so the page can read its parameter here.
                _lastNavigationParameter = e.Parameter;
                UpdateBackButton();
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("OnNavigated"));
            }
        }

        private static bool IsSameNavigationContext(object paramA, object paramB)
        {
            if (paramA is BaseItemDto itemA && paramB is BaseItemDto itemB)
            {
                if (itemA.Id == itemB.Id)
                {
                    return true;
                }

                var seriesIdA = itemA.SeriesId ?? itemA.Id;
                var seriesIdB = itemB.SeriesId ?? itemB.Id;
                return seriesIdA.HasValue && seriesIdA == seriesIdB;
            }

            return false;
        }
    }
}
