using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Gelatinarm.Views;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Service for managing media navigation (episodes, shuffle, queue)
    /// </summary>
    public class MediaNavigationService : BaseService, IMediaNavigationService
    {
        private const int SHUFFLE_BATCH_SIZE = 20; // Fetch 20 episodes at a time
        private const int SHUFFLE_REFILL_THRESHOLD = 5; // Refill when only 5 episodes left
        private readonly IEpisodeQueueService _episodeQueueService;
        private readonly INavigationService _navigationService;
        private readonly INavigationStateService _navigationStateService;
        private readonly HashSet<Guid> _playedEpisodesInSession = new();
        private readonly IPreferencesService _preferencesService;
        private readonly JellyfinApiClient _sdkClient;
        private readonly Random _shuffleRandom;

        // Advanced shuffled queue management
        private readonly Queue<BaseItemDto> _shuffledEpisodeQueue = new();
        private readonly IUserProfileService _userProfileService;
        private BaseItemDto _currentItem;
        private bool _isFetchingMoreEpisodes;
        private bool _isShuffleEnabled;
        private BaseItemDto _nextEpisode;
        private List<BaseItemDto> _originalQueue;

        private MediaPlaybackParams _playbackParams;

        public MediaNavigationService(
            IEpisodeQueueService episodeQueueService,
            INavigationService navigationService,
            INavigationStateService navigationStateService,
            IUserProfileService userProfileService,
            IPreferencesService preferencesService,
            JellyfinApiClient sdkClient,
            ILogger<MediaNavigationService> logger) : base(logger)
        {
            _episodeQueueService = episodeQueueService ?? throw new ArgumentNullException(nameof(episodeQueueService));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _navigationStateService =
                navigationStateService ?? throw new ArgumentNullException(nameof(navigationStateService));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
            _sdkClient = sdkClient ?? throw new ArgumentNullException(nameof(sdkClient));
            _shuffleRandom = new Random();
        }

        public event EventHandler NavigationStateChanged;

        public Task InitializeAsync(MediaPlaybackParams playbackParams, BaseItemDto currentItem)
        {
            _playbackParams = playbackParams ?? throw new ArgumentNullException(nameof(playbackParams));
            _currentItem = currentItem ?? throw new ArgumentNullException(nameof(currentItem));

            // Store original queue for shuffle/unshuffle
            if (_playbackParams.QueueItems != null)
            {
                _originalQueue = new List<BaseItemDto>(_playbackParams.QueueItems);
            }

            _isShuffleEnabled = _playbackParams.IsShuffled;

            // Mark current episode as played in shuffle session
            if (_isShuffleEnabled && currentItem.Type == BaseItemDto_Type.Episode && currentItem.Id.HasValue)
            {
                _playedEpisodesInSession.Add(currentItem.Id.Value);

                // Start filling the shuffled queue
                FireAndForget(() => RefillShuffledQueueAsync(), "RefillShuffledQueue");
            }

            Logger.LogInformation(
                $"MediaNavigationService initialized with {_playbackParams?.QueueItems?.Count ?? 0} items in queue");

            return Task.CompletedTask;
        }

        public async Task<BaseItemDto> GetNextEpisodeAsync()
        {
            try
            {
                // Use cached next episode if available
                if (_nextEpisode != null)
                {
                    return _nextEpisode;
                }

                // For shuffled episodes, get from shuffled queue
                if (_isShuffleEnabled && _currentItem?.Type == BaseItemDto_Type.Episode)
                {
                    // Check if we need to refill the queue
                    if (_shuffledEpisodeQueue.Count <= SHUFFLE_REFILL_THRESHOLD && !_isFetchingMoreEpisodes)
                    {
                        FireAndForget(() => RefillShuffledQueueAsync(), "RefillShuffledQueueLowThreshold");
                    }

                    if (_shuffledEpisodeQueue.Count > 0)
                    {
                        _nextEpisode = _shuffledEpisodeQueue.Peek();
                        return _nextEpisode;
                    }

                    // No more episodes in shuffle mode
                    Logger.LogInformation("No more episodes available in shuffle mode");
                    return null;
                }

                // For episodes, get the next in series
                if (_currentItem?.Type == BaseItemDto_Type.Episode)
                {
                    _nextEpisode = await GetNextEpisodeInSeriesAsync();
                    return _nextEpisode;
                }

                // For queued items, get next from queue
                if (HasNextItem())
                {
                    var nextIndex = _playbackParams.StartIndex + 1;
                    return _playbackParams.QueueItems[nextIndex];
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting next episode");
                return null;
            }
        }

        public async Task<BaseItemDto> GetPreviousEpisodeAsync()
        {
            try
            {
                // For episodes, get the previous in series
                if (_currentItem?.Type == BaseItemDto_Type.Episode)
                {
                    return await GetPreviousEpisodeInSeriesAsync();
                }

                // For queued items, get previous from queue
                if (HasPreviousItem())
                {
                    var prevIndex = _playbackParams.StartIndex - 1;
                    return _playbackParams.QueueItems[prevIndex];
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting previous episode");
                return null;
            }
        }

        public async Task NavigateToNextAsync()
        {
            try
            {
                var nextItem = await GetNextEpisodeAsync();
                if (nextItem == null)
                {
                    Logger.LogInformation("No next item available - navigating back");
                    await NavigateBackToOriginAsync();
                    return;
                }

                // For shuffled episodes, dequeue and mark as played
                if (_isShuffleEnabled && _currentItem?.Type == BaseItemDto_Type.Episode &&
                    _shuffledEpisodeQueue.Count > 0)
                {
                    var dequeuedEpisode = _shuffledEpisodeQueue.Dequeue();
                    if (dequeuedEpisode.Id.HasValue)
                    {
                        _playedEpisodesInSession.Add(dequeuedEpisode.Id.Value);
                    }

                    Logger.LogInformation($"Dequeued episode from shuffled queue: {dequeuedEpisode.Name}");
                }

                // Save navigation state
                if (_navigationStateService != null)
                {
                    var session = _navigationStateService.GetCurrentPlaybackSession();
                    if (session != null)
                    {
                        session.CurrentItem = nextItem;
                        session.NextEpisode = await GetNextEpisodeAsync();
                        _navigationStateService.SavePlaybackSession(session);
                    }
                }

                // Navigate to next item
                var playbackParams = new MediaPlaybackParams
                {
                    Item = nextItem,
                    ItemId = nextItem.Id?.ToString(),
                    MediaSourceId = null, // Let the server select the appropriate media source for each episode
                    AudioStreamIndex = _playbackParams?.AudioStreamIndex,
                    SubtitleStreamIndex = _playbackParams?.SubtitleStreamIndex,
                    StartPositionTicks = 0,
                    QueueItems = _playbackParams?.QueueItems,
                    StartIndex = (_playbackParams?.StartIndex ?? -1) + 1,
                    IsShuffled = _isShuffleEnabled,
                    NavigationSourcePage = _playbackParams?.NavigationSourcePage,
                    NavigationSourceParameter = _playbackParams?.NavigationSourceParameter
                };

                _navigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error navigating to next item");
                await NavigateBackToOriginAsync();
            }
        }

        public async Task NavigateToPreviousAsync()
        {
            try
            {
                var prevItem = await GetPreviousEpisodeAsync();
                if (prevItem == null)
                {
                    Logger.LogInformation("No previous item available");
                    return;
                }

                // Navigate to previous item
                var playbackParams = new MediaPlaybackParams
                {
                    Item = prevItem,
                    ItemId = prevItem.Id?.ToString(),
                    MediaSourceId = null, // Let the server select the appropriate media source for each episode
                    AudioStreamIndex = _playbackParams?.AudioStreamIndex,
                    SubtitleStreamIndex = _playbackParams?.SubtitleStreamIndex,
                    StartPositionTicks = 0,
                    QueueItems = _playbackParams?.QueueItems,
                    StartIndex = (_playbackParams?.StartIndex ?? 0) - 1,
                    IsShuffled = _isShuffleEnabled,
                    NavigationSourcePage = _playbackParams?.NavigationSourcePage,
                    NavigationSourceParameter = _playbackParams?.NavigationSourceParameter
                };

                _navigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error navigating to previous item");
            }
        }

        public bool HasNextItem()
        {
            if (_playbackParams?.QueueItems == null || _playbackParams.StartIndex < 0)
            {
                return false;
            }

            return _playbackParams.StartIndex < _playbackParams.QueueItems.Count - 1;
        }

        public bool HasPreviousItem()
        {
            if (_playbackParams?.QueueItems == null || _playbackParams.StartIndex < 0)
            {
                return false;
            }

            return _playbackParams.StartIndex > 0;
        }

        public Task SetShuffleModeAsync(bool enabled)
        {
            if (_isShuffleEnabled == enabled)
            {
                return Task.CompletedTask;
            }

            _isShuffleEnabled = enabled;

            if (_playbackParams?.QueueItems != null && _originalQueue != null)
            {
                if (enabled)
                {
                    // Shuffle the queue
                    var currentItem = _playbackParams.QueueItems[_playbackParams.StartIndex];
                    var shuffled = new List<BaseItemDto>(_originalQueue);

                    // Remove current item from list
                    shuffled.Remove(currentItem);

                    // Shuffle remaining items
                    for (var i = shuffled.Count - 1; i > 0; i--)
                    {
                        var j = _shuffleRandom.Next(i + 1);
                        var temp = shuffled[i];
                        shuffled[i] = shuffled[j];
                        shuffled[j] = temp;
                    }

                    // Put current item at the beginning
                    shuffled.Insert(0, currentItem);

                    _playbackParams.QueueItems = shuffled;
                    _playbackParams.StartIndex = 0;
                }
                else
                {
                    // Restore original queue
                    var currentItem = _playbackParams.QueueItems[_playbackParams.StartIndex];
                    _playbackParams.QueueItems = new List<BaseItemDto>(_originalQueue);

                    // Find current item in original queue
                    _playbackParams.StartIndex = _originalQueue.FindIndex(item => item.Id == currentItem.Id);
                    if (_playbackParams.StartIndex < 0)
                    {
                        _playbackParams.StartIndex = 0;
                    }
                }

                _playbackParams.IsShuffled = enabled;
            }

            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public bool IsShuffleEnabled()
        {
            return _isShuffleEnabled;
        }

        public async Task PreloadNextItemAsync()
        {
            try
            {
                // Preload next episode for faster transitions
                if (_currentItem?.Type == BaseItemDto_Type.Episode)
                {
                    _nextEpisode = await GetNextEpisodeInSeriesAsync();
                    if (_nextEpisode != null)
                    {
                        Logger.LogInformation($"Preloaded next episode: {_nextEpisode.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to preload next item");
            }
        }

        public void Dispose()
        {
            _nextEpisode = null;
            _currentItem = null;
            _playbackParams = null;
            _originalQueue = null;

            // Clear shuffled queue
            _shuffledEpisodeQueue?.Clear();
            _playedEpisodesInSession?.Clear();
        }

        private async Task<BaseItemDto> GetNextEpisodeInSeriesAsync()
        {
            try
            {
                if (_currentItem?.Type != BaseItemDto_Type.Episode || !_currentItem.SeriesId.HasValue)
                {
                    return null;
                }

                // Check if we already have a queue with more episodes
                if (_playbackParams?.QueueItems != null && _playbackParams.StartIndex >= 0)
                {
                    var queueIndex = _playbackParams.StartIndex;
                    if (queueIndex < _playbackParams.QueueItems.Count - 1)
                    {
                        return _playbackParams.QueueItems[queueIndex + 1];
                    }
                }

                // Get user ID
                var userIdStr = _userProfileService.CurrentUserId;
                if (!Guid.TryParse(userIdStr, out var userIdGuid))
                {
                    Logger.LogWarning("Invalid user ID");
                    return null;
                }

                // Use episode queue service to get all episodes
                var allEpisodes =
                    await _episodeQueueService.GetAllSeriesEpisodesAsync(_currentItem.SeriesId.Value, userIdGuid);
                if (allEpisodes == null || !allEpisodes.Any())
                {
                    return null;
                }

                // Find current episode and get next
                var currentIndex = allEpisodes.FindIndex(e => e.Id == _currentItem.Id);
                if (currentIndex >= 0 && currentIndex < allEpisodes.Count - 1)
                {
                    return allEpisodes[currentIndex + 1];
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting next episode in series");
                return null;
            }
        }

        private async Task<BaseItemDto> GetPreviousEpisodeInSeriesAsync()
        {
            try
            {
                if (_currentItem?.Type != BaseItemDto_Type.Episode || !_currentItem.SeriesId.HasValue)
                {
                    return null;
                }

                // Get user ID
                var userIdStr = _userProfileService.CurrentUserId;
                if (!Guid.TryParse(userIdStr, out var userIdGuid))
                {
                    Logger.LogWarning("Invalid user ID");
                    return null;
                }

                // Use episode queue service to get all episodes
                var allEpisodes =
                    await _episodeQueueService.GetAllSeriesEpisodesAsync(_currentItem.SeriesId.Value, userIdGuid);
                if (allEpisodes == null || !allEpisodes.Any())
                {
                    return null;
                }

                // Find current episode and get previous
                var currentIndex = allEpisodes.FindIndex(e => e.Id == _currentItem.Id);
                if (currentIndex > 0)
                {
                    return allEpisodes[currentIndex - 1];
                }

                return null;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error getting previous episode in series");
                return null;
            }
        }

        private async Task NavigateBackToOriginAsync()
        {
            try
            {
                // Clear playback session when navigating back
                _navigationStateService?.ClearPlaybackSession();

                // Use saved navigation source if available
                if (_playbackParams?.NavigationSourcePage != null)
                {
                    _navigationService.Navigate(_playbackParams.NavigationSourcePage,
                        _playbackParams.NavigationSourceParameter);
                    return;
                }

                // Otherwise navigate back
                if (_navigationService.CanGoBack)
                {
                    _navigationService.GoBack();
                }
                else
                {
                    // Default to library page
                    _navigationService.Navigate(typeof(LibraryPage));
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error navigating back to origin");
                _navigationService.Navigate(typeof(LibraryPage));
            }
        }

        private async Task RefillShuffledQueueAsync()
        {
            if (_isFetchingMoreEpisodes || _currentItem?.Type != BaseItemDto_Type.Episode ||
                !_currentItem.SeriesId.HasValue)
            {
                return;
            }

            try
            {
                _isFetchingMoreEpisodes = true;
                Logger.LogInformation(
                    $"Refilling shuffled episode queue. Current queue size: {_shuffledEpisodeQueue.Count}");

                // Get user ID
                var userIdStr = _userProfileService.CurrentUserId;
                if (!Guid.TryParse(userIdStr, out var userIdGuid))
                {
                    Logger.LogWarning("Invalid user ID for queue refill");
                    return;
                }

                // Get all episodes from the series
                var allEpisodes =
                    await _episodeQueueService.GetAllSeriesEpisodesAsync(_currentItem.SeriesId.Value, userIdGuid);
                if (allEpisodes == null || !allEpisodes.Any())
                {
                    Logger.LogWarning("No episodes found for queue refill");
                    return;
                }

                // Filter out already played episodes
                var availableEpisodes = allEpisodes
                    .Where(e => e.Id.HasValue && !_playedEpisodesInSession.Contains(e.Id.Value))
                    .ToList();

                if (!availableEpisodes.Any())
                {
                    Logger.LogInformation("All episodes have been played in this session");
                    return;
                }

                // Shuffle available episodes
                var shuffled = _episodeQueueService.ShuffleEpisodes(availableEpisodes, _shuffleRandom);

                // Add up to SHUFFLE_BATCH_SIZE episodes to the queue
                var itemsToAdd = Math.Min(SHUFFLE_BATCH_SIZE - _shuffledEpisodeQueue.Count, shuffled.Count);
                for (var i = 0; i < itemsToAdd; i++)
                {
                    _shuffledEpisodeQueue.Enqueue(shuffled[i]);
                }

                Logger.LogInformation(
                    $"Added {itemsToAdd} episodes to shuffled queue. New queue size: {_shuffledEpisodeQueue.Count}");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error refilling shuffled episode queue");
            }
            finally
            {
                _isFetchingMoreEpisodes = false;
            }
        }
    }
}
