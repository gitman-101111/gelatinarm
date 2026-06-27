using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Models;
using Gelatinarm.Views;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    public class MediaQueueService : BaseService, IPlaybackQueueService, IEpisodeQueueService, IMediaNavigationService
    {
        private const int ShuffleBatchSize = 20;
        private const int ShuffleRefillThreshold = 5;
        private static int _shuffleSeed = Environment.TickCount;

        private static readonly ThreadLocal<Random> ShuffleRandom =
            new ThreadLocal<Random>(() => new Random(Interlocked.Increment(ref _shuffleSeed)));

        private readonly JellyfinApiClient _apiClient;
        private readonly INavigationService _navigationService;
        private readonly INavigationStateService _navigationStateService;
        private readonly IUserProfileService _userProfileService;
        private int _lastQueueHash;
        private readonly ConcurrentDictionary<Guid, bool> _playedEpisodesInSession = new();
        private readonly ConcurrentQueue<BaseItemDto> _shuffledEpisodeQueue = new();
        private readonly Random _shuffleRandom = new Random();
        private BaseItemDto _currentItem;
        private bool _isFetchingMoreEpisodes;
        private BaseItemDto _nextEpisode;
        private MediaPlaybackParams _playbackParams;

        public MediaQueueService(
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            INavigationStateService navigationStateService,
            ILogger<MediaQueueService> logger) : base(logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
            _navigationService = navigationService ?? throw new ArgumentNullException(nameof(navigationService));
            _navigationStateService =
                navigationStateService ?? throw new ArgumentNullException(nameof(navigationStateService));

            Queue = new List<BaseItemDto>();
            CurrentQueueIndex = -1;
            IsShuffleMode = false;
        }

        public List<BaseItemDto> Queue { get; }

        public int CurrentQueueIndex { get; private set; }

        public bool IsShuffleMode { get; private set; }

        public List<int> ShuffledIndices { get; private set; }

        public int CurrentShuffleIndex { get; private set; }

        public event EventHandler<List<BaseItemDto>> QueueChanged;
        public event EventHandler<int> QueueIndexChanged;
        public event EventHandler NavigationStateChanged;

        public void SetQueue(List<BaseItemDto> items, int startIndex = 0)
        {
            var context = CreateErrorContext("SetQueue", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    if (items == null || !items.Any())
                    {
                        Logger.LogWarning("SetQueue called with null or empty items");
                        return;
                    }

                    Queue.Clear();
                    Queue.AddRange(items);
                    CurrentQueueIndex = Math.Max(0, Math.Min(startIndex, items.Count - 1));

                    Logger.LogInformation($"Queue set with {items.Count} items, starting at index {CurrentQueueIndex}");

                    if (IsShuffleMode && Queue.Count > 1)
                    {
                        CreateShuffledIndices();
                    }

                    QueueChanged?.Invoke(this, Queue);
                    QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void AddToQueue(BaseItemDto item)
        {
            var context = CreateErrorContext("AddToQueue", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    if (item != null)
                    {
                        Queue.Add(item);

                        if (CurrentQueueIndex == -1)
                        {
                            CurrentQueueIndex = 0;
                            QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
                        }

                        _lastQueueHash = 0;
                        QueueChanged?.Invoke(this, Queue);
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void AddToQueueNext(BaseItemDto item)
        {
            var context = CreateErrorContext("AddToQueueNext", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    if (item != null && CurrentQueueIndex >= 0)
                    {
                        Queue.Insert(CurrentQueueIndex + 1, item);
                        _lastQueueHash = 0;
                        QueueChanged?.Invoke(this, Queue);
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void ClearQueue()
        {
            var context = CreateErrorContext("ClearQueue", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    Queue.Clear();
                    CurrentQueueIndex = -1;
                    ShuffledIndices = null;
                    _lastQueueHash = 0;
                    CurrentShuffleIndex = 0;

                    QueueChanged?.Invoke(this, Queue);
                    QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SetCurrentIndex(int index)
        {
            var context = CreateErrorContext("SetCurrentIndex", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    if (index >= 0 && index < Queue.Count)
                    {
                        CurrentQueueIndex = index;

                        if (IsShuffleMode && ShuffledIndices != null)
                        {
                            CurrentShuffleIndex = ShuffledIndices.IndexOf(index);
                            if (CurrentShuffleIndex == -1)
                            {
                                CurrentShuffleIndex = 0;
                            }
                        }

                        QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SetShuffle(bool enabled)
        {
            var context = CreateErrorContext("SetShuffle", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    IsShuffleMode = enabled;
                    Logger.LogInformation($"Shuffle mode set to: {(IsShuffleMode ? "On" : "Off")}");

                    if (IsShuffleMode && Queue.Count > 1)
                    {
                        CreateShuffledIndices();
                    }
                    else
                    {
                        ShuffledIndices = null;
                        CurrentShuffleIndex = 0;
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public Task InitializeAsync(MediaPlaybackParams playbackParams, BaseItemDto currentItem)
        {
            _playbackParams = playbackParams ?? throw new ArgumentNullException(nameof(playbackParams));
            _currentItem = currentItem ?? throw new ArgumentNullException(nameof(currentItem));

            if (_playbackParams.QueueItems != null && _playbackParams.QueueItems.Any())
            {
                Queue.Clear();
                Queue.AddRange(_playbackParams.QueueItems);
                CurrentQueueIndex = Math.Max(0, Math.Min(_playbackParams.StartIndex, Queue.Count - 1));
                _lastQueueHash = 0;
                QueueChanged?.Invoke(this, Queue);
                QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
            }

            IsShuffleMode = _playbackParams.IsShuffled;
            if (IsShuffleMode && Queue.Count > 1)
            {
                CreateShuffledIndices();
            }

            if (IsShuffleMode && _currentItem.Type == BaseItemDto_Type.Episode && _currentItem.Id.HasValue)
            {
                _playedEpisodesInSession.TryAdd(_currentItem.Id.Value, true);
                FireAndForget(() => RefillShuffledQueueAsync(), "RefillShuffledQueue");
            }

            Logger.LogInformation($"MediaQueueService initialized with {Queue.Count} items, index {CurrentQueueIndex}");
            return Task.CompletedTask;
        }

        public async Task<BaseItemDto> GetNextEpisodeAsync()
        {
            try
            {
                if (_nextEpisode != null)
                {
                    return _nextEpisode;
                }

                if (IsShuffleMode && _currentItem?.Type == BaseItemDto_Type.Episode)
                {
                    if (_shuffledEpisodeQueue.Count <= ShuffleRefillThreshold && !_isFetchingMoreEpisodes)
                    {
                        FireAndForget(() => RefillShuffledQueueAsync(), "RefillShuffledQueueLowThreshold");
                    }

                    if (_shuffledEpisodeQueue.TryPeek(out var peekedEpisode))
                    {
                        _nextEpisode = peekedEpisode;
                        return _nextEpisode;
                    }

                    Logger.LogInformation("No more episodes available in shuffle mode");
                    return null;
                }

                if (_currentItem?.Type == BaseItemDto_Type.Episode)
                {
                    _nextEpisode = await GetNextEpisodeInSeriesAsync();
                    return _nextEpisode;
                }

                if (Queue.Any())
                {
                    var nextIndex = GetNextIndex(false);
                    if (nextIndex >= 0 && nextIndex < Queue.Count)
                    {
                        return Queue[nextIndex];
                    }
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
                if (_currentItem?.Type == BaseItemDto_Type.Episode)
                {
                    return await GetPreviousEpisodeInSeriesAsync();
                }

                if (Queue.Any())
                {
                    var prevIndex = GetPreviousIndex(false);
                    if (prevIndex >= 0 && prevIndex < Queue.Count)
                    {
                        return Queue[prevIndex];
                    }
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

                if (IsShuffleMode && _currentItem?.Type == BaseItemDto_Type.Episode &&
                    _shuffledEpisodeQueue.TryDequeue(out var dequeuedEpisode))
                {
                    if (dequeuedEpisode.Id.HasValue)
                    {
                        _playedEpisodesInSession.TryAdd(dequeuedEpisode.Id.Value, true);
                    }
                }

                var nextIndex = Queue.FindIndex(item => item.Id == nextItem.Id);
                if (nextIndex >= 0)
                {
                    CurrentQueueIndex = nextIndex;
                    QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
                }

                var playbackParams = new MediaPlaybackParams
                {
                    Item = nextItem,
                    ItemId = nextItem.Id?.ToString(),
                    MediaSourceId = null,
                    AudioStreamIndex = _playbackParams?.AudioStreamIndex,
                    SubtitleStreamIndex = _playbackParams?.SubtitleStreamIndex,
                    StartPositionTicks = 0,
                    QueueItems = Queue.ToList(),
                    StartIndex = CurrentQueueIndex,
                    IsShuffled = IsShuffleMode,
                    NavigationSourcePage = _playbackParams?.NavigationSourcePage,
                    NavigationSourceParameter = _playbackParams?.NavigationSourceParameter
                };

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

                var prevIndex = Queue.FindIndex(item => item.Id == prevItem.Id);
                if (prevIndex >= 0)
                {
                    CurrentQueueIndex = prevIndex;
                    QueueIndexChanged?.Invoke(this, CurrentQueueIndex);
                }

                var playbackParams = new MediaPlaybackParams
                {
                    Item = prevItem,
                    ItemId = prevItem.Id?.ToString(),
                    MediaSourceId = null,
                    AudioStreamIndex = _playbackParams?.AudioStreamIndex,
                    SubtitleStreamIndex = _playbackParams?.SubtitleStreamIndex,
                    StartPositionTicks = 0,
                    QueueItems = Queue.ToList(),
                    StartIndex = CurrentQueueIndex,
                    IsShuffled = IsShuffleMode,
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
            if (!Queue.Any() || CurrentQueueIndex < 0)
            {
                return false;
            }

            return CurrentQueueIndex < Queue.Count - 1;
        }

        public bool HasPreviousItem()
        {
            if (!Queue.Any() || CurrentQueueIndex < 0)
            {
                return false;
            }

            return CurrentQueueIndex > 0;
        }

        public Task SetShuffleModeAsync(bool enabled)
        {
            if (IsShuffleMode == enabled)
            {
                return Task.CompletedTask;
            }

            IsShuffleMode = enabled;
            if (_playbackParams != null)
            {
                _playbackParams.IsShuffled = enabled;
            }

            if (IsShuffleMode && Queue.Count > 1)
            {
                CreateShuffledIndices();
            }
            else
            {
                ShuffledIndices = null;
                CurrentShuffleIndex = 0;
            }

            NavigationStateChanged?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public bool IsShuffleEnabled()
        {
            return IsShuffleMode;
        }

        public async Task PreloadNextItemAsync()
        {
            try
            {
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

        public void CreateShuffledIndices()
        {
            var currentQueueHash = GetQueueHash();

            if (ShuffledIndices != null &&
                _lastQueueHash == currentQueueHash &&
                ShuffledIndices.Count == Queue.Count)
            {
                CurrentShuffleIndex = ShuffledIndices.IndexOf(CurrentQueueIndex);
                if (CurrentShuffleIndex == -1)
                {
                    CurrentShuffleIndex = 0;
                }

                Logger.LogInformation("Reusing cached shuffle indices");
                return;
            }

            Logger.LogInformation("Creating new shuffle indices");
            ShuffledIndices = new List<int>();
            for (var i = 0; i < Queue.Count; i++)
            {
                if (i != CurrentQueueIndex)
                {
                    ShuffledIndices.Add(i);
                }
            }

            var random = new Random();
            for (var i = ShuffledIndices.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (ShuffledIndices[i], ShuffledIndices[j]) = (ShuffledIndices[j], ShuffledIndices[i]);
            }

            ShuffledIndices.Insert(0, CurrentQueueIndex);
            CurrentShuffleIndex = 0;

            _lastQueueHash = currentQueueHash;
        }

        public int GetNextIndex(bool isRepeatAll)
        {
            if (!Queue.Any())
            {
                return -1;
            }

            if (IsShuffleMode && ShuffledIndices?.Any() == true)
            {
                var nextShuffleIndex = CurrentShuffleIndex + 1;

                if (nextShuffleIndex >= ShuffledIndices.Count)
                {
                    if (isRepeatAll)
                    {
                        CreateShuffledIndices();
                        return ShuffledIndices.Count > 0 ? ShuffledIndices[0] : -1;
                    }

                    return -1;
                }

                return ShuffledIndices[nextShuffleIndex];
            }

            if (CurrentQueueIndex < Queue.Count - 1)
            {
                return CurrentQueueIndex + 1;
            }

            if (isRepeatAll && Queue.Any())
            {
                return 0;
            }

            return -1;
        }

        public int GetPreviousIndex(bool isRepeatAll)
        {
            if (!Queue.Any())
            {
                return -1;
            }

            if (IsShuffleMode && ShuffledIndices?.Any() == true)
            {
                var prevShuffleIndex = CurrentShuffleIndex - 1;

                if (prevShuffleIndex < 0)
                {
                    if (isRepeatAll)
                    {
                        return ShuffledIndices[ShuffledIndices.Count - 1];
                    }

                    return 0;
                }

                return ShuffledIndices[prevShuffleIndex];
            }

            if (CurrentQueueIndex > 0)
            {
                return CurrentQueueIndex - 1;
            }

            if (isRepeatAll && Queue.Any())
            {
                return Queue.Count - 1;
            }

            return 0;
        }

        public async Task<List<BaseItemDto>> GetAllSeriesEpisodesAsync(
            Guid seriesId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("GetAllSeriesEpisodes", ErrorCategory.Media);
            try
            {
                var episodesResponse = await _apiClient.Shows[seriesId].Episodes.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userId;
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.MediaStreams, ItemFields.MediaSources, ItemFields.Overview, ItemFields.Path
                    };
                    config.QueryParameters.EnableImages = true;
                    config.QueryParameters.EnableUserData = true;
                    config.QueryParameters.Limit = MediaConstants.ExtendedQueryLimit;
                }, cancellationToken).ConfigureAwait(false);

                if (episodesResponse?.Items == null)
                {
                    Logger.LogWarning($"No episodes found for series {seriesId}");
                    return new List<BaseItemDto>();
                }

                var sortedEpisodes = SortEpisodesBySeasonAndNumber(episodesResponse.Items);
                Logger.LogInformation($"Retrieved {sortedEpisodes.Count} episodes for series {seriesId}");

                return sortedEpisodes;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public async Task<(List<BaseItemDto> queue, int startIndex)> BuildEpisodeQueueAsync(
            BaseItemDto targetEpisode,
            Guid seriesId,
            Guid userId,
            CancellationToken cancellationToken = default)
        {
            if (targetEpisode == null)
            {
                Logger.LogWarning("Cannot build episode queue: target episode is null");
                return (null, 0);
            }

            var context = CreateErrorContext("BuildEpisodeQueue", ErrorCategory.Media);
            try
            {
                var allEpisodes = await GetAllSeriesEpisodesAsync(seriesId, userId, cancellationToken)
                    .ConfigureAwait(false);

                var selectedIndex = allEpisodes.FindIndex(e => e.Id == targetEpisode.Id);
                if (selectedIndex >= 0)
                {
                    Logger.LogInformation(
                        $"Built episode queue with {allEpisodes.Count} episodes, starting at index {selectedIndex} " +
                        $"(S{targetEpisode.ParentIndexNumber}E{targetEpisode.IndexNumber} - {targetEpisode.Name})");
                    return (allEpisodes, selectedIndex);
                }

                Logger.LogWarning($"Target episode {targetEpisode.Name} not found in series episodes");
                return (null, 0);
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<(List<BaseItemDto>, int)>(ex, context, (null, 0));
            }
        }

        public async Task<(List<BaseItemDto> queue, int startIndex)> BuildEpisodeQueueAsync(
            BaseItemDto targetEpisode,
            CancellationToken cancellationToken = default)
        {
            if (targetEpisode == null)
            {
                Logger.LogWarning("Cannot build episode queue: target episode is null");
                return (null, 0);
            }

            if (!targetEpisode.SeriesId.HasValue)
            {
                Logger.LogWarning("Cannot build episode queue: episode has no series ID");
                return (null, 0);
            }

            if (!TryGetUserIdGuid(_userProfileService, out var userId))
            {
                return (null, 0);
            }

            return await BuildEpisodeQueueAsync(targetEpisode, targetEpisode.SeriesId.Value, userId, cancellationToken);
        }

        public List<BaseItemDto> SortEpisodesBySeasonAndNumber(IEnumerable<BaseItemDto> episodes)
        {
            if (episodes == null)
            {
                return new List<BaseItemDto>();
            }

            return episodes
                .OrderBy(e => e.ParentIndexNumber ?? 0)
                .ThenBy(e => e.IndexNumber ?? 0)
                .ToList();
        }

        public List<BaseItemDto> ShuffleEpisodes(IEnumerable<BaseItemDto> episodes, Random random = null)
        {
            if (episodes == null)
            {
                return new List<BaseItemDto>();
            }

            var list = episodes.ToList();
            if (list.Count < 2)
            {
                return list;
            }

            var rng = random ?? ShuffleRandom.Value;
            for (var i = list.Count - 1; i > 0; i--)
            {
                var j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }

            return list;
        }

        public async Task<(List<BaseItemDto> queue, int startIndex)> BuildShuffledSeriesQueueAsync(
            Guid seriesId,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetUserIdGuid(_userProfileService, out var userId))
            {
                return (null, 0);
            }

            var context = CreateErrorContext("BuildShuffledSeriesQueue", ErrorCategory.Media);
            try
            {
                var allEpisodes = await GetAllSeriesEpisodesAsync(seriesId, userId, cancellationToken)
                    .ConfigureAwait(false);

                if (allEpisodes == null || allEpisodes.Count == 0)
                {
                    Logger.LogWarning($"No episodes found for series {seriesId}");
                    return (null, 0);
                }

                var shuffledQueue = ShuffleEpisodes(allEpisodes);
                Logger.LogInformation(
                    $"Built shuffled queue with {shuffledQueue.Count} episodes for series {seriesId}");

                return (shuffledQueue, 0);
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<(List<BaseItemDto>, int)>(ex, context, (null, 0));
            }
        }

        public async Task<(List<BaseItemDto> queue, int startIndex, bool success)> BuildContinueWatchingQueueAsync(
            BaseItemDto episode,
            CancellationToken cancellationToken = default)
        {
            if (episode == null)
            {
                Logger.LogWarning("Cannot build continue watching queue: episode is null");
                return (null, 0, false);
            }

            if (episode.Type != BaseItemDto_Type.Episode || !episode.SeriesId.HasValue)
            {
                Logger.LogWarning("Cannot build continue watching queue: item is not an episode or has no series ID");
                return (null, 0, false);
            }

            var context = CreateErrorContext("BuildContinueWatchingQueue", ErrorCategory.Media);
            try
            {
                var (queue, startIndex) =
                    await BuildEpisodeQueueAsync(episode, cancellationToken).ConfigureAwait(false);

                if (queue == null || queue.Count == 0)
                {
                    Logger.LogWarning("Failed to build continue watching queue");
                    return (null, 0, false);
                }

                Logger.LogInformation(
                    $"Built continue watching queue with {queue.Count} episodes starting at index {startIndex}");
                return (queue, startIndex, true);
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<(List<BaseItemDto>, int, bool)>(ex, context,
                    (null, 0, false));
            }
        }

        private int GetQueueHash()
        {
            if (Queue == null || !Queue.Any())
            {
                return 0;
            }

            unchecked
            {
                var hash = 17;
                for (var i = 0; i < Queue.Count; i++)
                {
                    if (Queue[i]?.Id != null)
                    {
                        hash = (hash * 31) + Queue[i].Id.GetHashCode();
                        hash = (hash * 31) + i;
                    }
                }

                return hash;
            }
        }

        private async Task<BaseItemDto> GetNextEpisodeInSeriesAsync()
        {
            try
            {
                if (_currentItem?.Type != BaseItemDto_Type.Episode || !_currentItem.SeriesId.HasValue)
                {
                    return null;
                }

                if (Queue.Any() && CurrentQueueIndex >= 0 && CurrentQueueIndex < Queue.Count - 1)
                {
                    return Queue[CurrentQueueIndex + 1];
                }

                if (!TryGetUserIdGuid(_userProfileService, out var userIdGuid))
                {
                    return null;
                }

                var allEpisodes = await GetAllSeriesEpisodesAsync(_currentItem.SeriesId.Value, userIdGuid);
                if (allEpisodes == null || !allEpisodes.Any())
                {
                    return null;
                }

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

                if (Queue.Any() && CurrentQueueIndex > 0 && CurrentQueueIndex < Queue.Count)
                {
                    return Queue[CurrentQueueIndex - 1];
                }

                if (!TryGetUserIdGuid(_userProfileService, out var userIdGuid))
                {
                    return null;
                }

                var allEpisodes = await GetAllSeriesEpisodesAsync(_currentItem.SeriesId.Value, userIdGuid);
                if (allEpisodes == null || !allEpisodes.Any())
                {
                    return null;
                }

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

        public async Task NavigateBackToOriginAsync()
        {
            try
            {
                _navigationStateService?.ClearPlaybackSession();

                if (_playbackParams?.NavigationSourcePage != null)
                {
                    _navigationService.Navigate(_playbackParams.NavigationSourcePage,
                        _playbackParams.NavigationSourceParameter);
                    return;
                }

                if (_navigationService.CanGoBack)
                {
                    _navigationService.GoBack();
                }
                else
                {
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

                if (!TryGetUserIdGuid(_userProfileService, out var userIdGuid))
                {
                    return;
                }

                var allEpisodes = await GetAllSeriesEpisodesAsync(_currentItem.SeriesId.Value, userIdGuid);
                if (allEpisodes == null || !allEpisodes.Any())
                {
                    Logger.LogWarning("No episodes found for queue refill");
                    return;
                }

                var availableEpisodes = allEpisodes
                    .Where(e => e.Id.HasValue && !_playedEpisodesInSession.ContainsKey(e.Id.Value))
                    .ToList();

                if (!availableEpisodes.Any())
                {
                    Logger.LogInformation("All episodes have been played in this session");
                    return;
                }

                var shuffled = ShuffleEpisodes(availableEpisodes, _shuffleRandom);

                var itemsToAdd = Math.Min(ShuffleBatchSize - _shuffledEpisodeQueue.Count, shuffled.Count);
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
