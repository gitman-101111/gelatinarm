using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Models;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    public class EpisodeQueueService : BaseService, IEpisodeQueueService
    {
        private static int _shuffleSeed = Environment.TickCount;
        private static readonly ThreadLocal<Random> ShuffleRandom =
            new ThreadLocal<Random>(() => new Random(Interlocked.Increment(ref _shuffleSeed)));

        private readonly JellyfinApiClient _apiClient;
        private readonly IUserProfileService _userProfileService;

        public EpisodeQueueService(JellyfinApiClient apiClient, ILogger<EpisodeQueueService> logger,
            IUserProfileService userProfileService) : base(logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
        }

        public async Task<List<BaseItemDto>> GetAllSeriesEpisodesAsync(Guid seriesId, Guid userId,
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
                    config.QueryParameters.Limit = MediaConstants.EXTENDED_QUERY_LIMIT; // Get up to 500 episodes
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
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>(), false);
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
                // Get all episodes for the series
                var allEpisodes = await GetAllSeriesEpisodesAsync(seriesId, userId, cancellationToken)
                    .ConfigureAwait(false);

                // Find the target episode's position
                var selectedIndex = allEpisodes.FindIndex(e => e.Id == targetEpisode.Id);
                if (selectedIndex >= 0)
                {
                    // Return all episodes with the proper start index
                    // This allows backward navigation and maintains the full episode list
                    Logger.LogInformation(
                        $"Built episode queue with {allEpisodes.Count} episodes, starting at index {selectedIndex} (S{targetEpisode.ParentIndexNumber}E{targetEpisode.IndexNumber} - {targetEpisode.Name})");
                    return (allEpisodes, selectedIndex);
                }

                Logger.LogWarning($"Target episode {targetEpisode.Name} not found in series episodes");
                return (null, 0);
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<(List<BaseItemDto>, int)>(ex, context, (null, 0), false);
            }
        }

        public async Task<(List<BaseItemDto> queue, int startIndex)> BuildEpisodeQueueAsync(BaseItemDto targetEpisode,
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

        public async Task<(List<BaseItemDto> queue, int startIndex)> BuildShuffledSeriesQueueAsync(Guid seriesId,
            CancellationToken cancellationToken = default)
        {
            if (!TryGetUserIdGuid(_userProfileService, out var userId))
            {
                return (null, 0);
            }

            var context = CreateErrorContext("BuildShuffledSeriesQueue", ErrorCategory.Media);
            try
            {
                // Get all episodes for the series
                var allEpisodes = await GetAllSeriesEpisodesAsync(seriesId, userId, cancellationToken)
                    .ConfigureAwait(false);

                if (allEpisodes == null || allEpisodes.Count == 0)
                {
                    Logger.LogWarning($"No episodes found for series {seriesId}");
                    return (null, 0);
                }

                // Shuffle the episodes
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
            BaseItemDto episode, CancellationToken cancellationToken = default)
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
    }
}
