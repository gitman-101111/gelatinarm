using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gelatinarm.Details;
using Gelatinarm.Library;
using Gelatinarm.Music;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Server;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public interface IMediaNavigationService : IDisposable
    {
        void Initialize(MediaPlaybackParams playbackParams);

        Task<BaseItemDto> GetNextEpisodeAsync();

        Task NavigateToNextAsync();

        Task NavigateBackToOriginAsync();

        /// <summary>
        ///     Whether the queue holds an item after the current one; an episode without one may
        ///     still have a next episode, which GetNextEpisodeAsync looks up
        /// </summary>
        bool HasQueuedNextItem();
    }

    public interface IEpisodeQueueService
    {
        /// <summary>
        ///     The series' episodes as a queue, starting at <paramref name="targetEpisode" />
        /// </summary>
        Task<(List<BaseItemDto> queue, int startIndex)> BuildEpisodeQueueAsync(BaseItemDto targetEpisode);

        Task<List<BaseItemDto>> BuildShuffledSeriesQueueAsync(Guid seriesId);
    }

    public class MediaQueueService : BaseService, IEpisodeQueueService, IMediaNavigationService
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly INavigationService _navigationService;
        private readonly IUserProfileService _userProfileService;
        private readonly List<BaseItemDto> _queue = new();
        private int _queueIndex = -1;
        private BaseItemDto _nextEpisode;
        private MediaPlaybackParams _playbackParams;

        public MediaQueueService(
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            ILogger<MediaQueueService> logger) : base(logger)
        {
            _apiClient = apiClient;
            _userProfileService = userProfileService;
            _navigationService = navigationService;
        }

        public void Initialize(MediaPlaybackParams playbackParams)
        {
            _playbackParams = playbackParams ?? throw new ArgumentNullException(nameof(playbackParams));

            // Everything here is this playback's: a queue or looked-up next episode left by an
            // earlier playback (this is a singleton) would be offered as this one's Next
            _nextEpisode = null;
            _queue.Clear();
            if (_playbackParams.QueueItems != null)
            {
                _queue.AddRange(_playbackParams.QueueItems);
            }

            _queueIndex = _queue.Count > 0 ? Math.Max(0, Math.Min(_playbackParams.StartIndex, _queue.Count - 1)) : -1;

            Logger.LogDebug("MediaQueueService initialized with {QueueCount} items, index {QueueIndex}", _queue.Count, _queueIndex);
        }

        public async Task<BaseItemDto> GetNextEpisodeAsync()
        {
            // A shuffled queue was shuffled when it was built: it plays in its own order
            if (HasQueuedNextItem())
            {
                return _queue[_queueIndex + 1];
            }

            if (_playbackParams.Item?.Type == BaseItemDto_Type.Episode)
            {
                _nextEpisode ??= await GetNextEpisodeInSeriesAsync();
                return _nextEpisode;
            }

            return null;
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

                PlayQueueItem(nextItem);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("NavigateToNext", ErrorCategory.Media), false);
                await NavigateBackToOriginAsync();
            }
        }

        /// <summary>
        ///     Opens the player on another item of this queue, from its start, keeping the chosen
        ///     tracks and where playback was started from
        /// </summary>
        private void PlayQueueItem(BaseItemDto item)
        {
            // An episode found in the series list, past the queue, keeps the queue position
            var index = _queue.FindIndex(queued => queued.Id == item.Id);

            var playbackParams = new MediaPlaybackParams
            {
                Item = item,
                AudioStreamIndex = _playbackParams.AudioStreamIndex,
                SubtitleStreamIndex = _playbackParams.SubtitleStreamIndex,
                StartPositionTicks = 0,
                QueueItems = _queue.ToList(),
                StartIndex = index >= 0 ? index : _queueIndex,
                NavigationSourcePage = _playbackParams.NavigationSourcePage,
                NavigationSourceParameter = _playbackParams.NavigationSourceParameter
            };

            _navigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
        }

        public bool HasQueuedNextItem()
        {
            return _queueIndex >= 0 && _queueIndex < _queue.Count - 1;
        }

        // Reports its own failure and returns an empty list, so its callers need no catch
        private async Task<List<BaseItemDto>> GetAllSeriesEpisodesAsync(Guid seriesId)
        {
            if (!TryGetUserIdGuid(_userProfileService, out var userId))
            {
                return new List<BaseItemDto>();
            }

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
                    config.QueryParameters.Limit = SystemConstants.ExtendedQueryLimit;
                }).ConfigureAwait(false);

                // The server's series order: seasons in order, specials where the library puts them
                return episodesResponse?.Items ?? new List<BaseItemDto>();
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public async Task<(List<BaseItemDto> queue, int startIndex)> BuildEpisodeQueueAsync(
            BaseItemDto targetEpisode)
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

            var allEpisodes = await GetAllSeriesEpisodesAsync(targetEpisode.SeriesId.Value).ConfigureAwait(false);

            var selectedIndex = allEpisodes.FindIndex(e => e.Id == targetEpisode.Id);
            if (selectedIndex >= 0)
            {
                Logger.LogInformation(
                    "Built episode queue with {AllEpisodesCount} episodes, starting at index {SelectedIndex} " +
                    "(S{TargetEpisodeParentIndexNumber}E{TargetEpisodeIndexNumber} - {TargetEpisodeName})", allEpisodes.Count, selectedIndex, targetEpisode.ParentIndexNumber, targetEpisode.IndexNumber, targetEpisode.Name);
                return (allEpisodes, selectedIndex);
            }

            Logger.LogWarning("Target episode {TargetEpisodeName} not found in series episodes", targetEpisode.Name);
            return (null, 0);
        }

        public async Task<List<BaseItemDto>> BuildShuffledSeriesQueueAsync(Guid seriesId)
        {
            var allEpisodes = await GetAllSeriesEpisodesAsync(seriesId).ConfigureAwait(false);
            if (allEpisodes.Count == 0)
            {
                Logger.LogWarning("No episodes found for series {SeriesId}", seriesId);
                return null;
            }

            var shuffledQueue = ShuffleHelper.Shuffled(allEpisodes);
            Logger.LogInformation(
                "Built shuffled queue with {ShuffledQueueCount} episodes for series {SeriesId}", shuffledQueue.Count, seriesId);

            return shuffledQueue;
        }

        /// <summary>
        ///     The episode after the current one in the series' full episode list
        /// </summary>
        private async Task<BaseItemDto> GetNextEpisodeInSeriesAsync()
        {
            var currentItem = _playbackParams.Item;
            if (currentItem?.Type != BaseItemDto_Type.Episode || !currentItem.SeriesId.HasValue)
            {
                return null;
            }

            var allEpisodes = await GetAllSeriesEpisodesAsync(currentItem.SeriesId.Value);
            var currentIndex = allEpisodes.FindIndex(e => e.Id == currentItem.Id);
            return currentIndex >= 0 && currentIndex + 1 < allEpisodes.Count
                ? allEpisodes[currentIndex + 1]
                : null;
        }

        public Task NavigateBackToOriginAsync()
        {
            // Reached from the resume retry loop, timers and fire-and-forget paths on pool
            // threads; the Frame throws RPC_E_WRONG_THREAD (0x8001010E) off the UI thread.
            return UiHelper.RunOnUIThreadAsync(NavigateBackToOriginCoreAsync, logger: Logger);
        }

        private async Task NavigateBackToOriginCoreAsync()
        {
            try
            {
                var sourcePage = _playbackParams.NavigationSourcePage;
                if (sourcePage != null)
                {
                    // The season page opens on the episode that was playing: next episode may have
                    // moved past the one playback started from
                    var parameter = sourcePage == typeof(SeasonDetailsPage) &&
                                    _playbackParams.Item?.Type == BaseItemDto_Type.Episode
                        ? _playbackParams.Item
                        : _playbackParams.NavigationSourceParameter;
                    _navigationService.Navigate(sourcePage, parameter);
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
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("NavigateBackToOrigin", ErrorCategory.Media), false);
                _navigationService.Navigate(typeof(LibraryPage));
            }
        }
    }
}
