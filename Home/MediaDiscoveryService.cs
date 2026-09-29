using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.Shared.Server;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Home
{
    public interface IMediaDiscoveryService
    {
        Task<IEnumerable<BaseItemDto>> GetRecentlyAddedAsync(CancellationToken cancellationToken);

        Task<IEnumerable<BaseItemDto>> GetContinueWatchingAsync(CancellationToken cancellationToken);

        Task<IEnumerable<BaseItemDto>> GetRecommendedAsync(CancellationToken cancellationToken);

        Task<IEnumerable<BaseItemDto>> GetLatestMoviesAsync(CancellationToken cancellationToken);

        Task<IEnumerable<BaseItemDto>> GetLatestShowsAsync(CancellationToken cancellationToken);

        Task<IEnumerable<BaseItemDto>> GetNextUpAsync(CancellationToken cancellationToken);
    }

    public class MediaDiscoveryService : BaseService, IMediaDiscoveryService
    {
        private static readonly ItemFields[] DefaultItemFields =
        {
            ItemFields.Overview, ItemFields.PrimaryImageAspectRatio
        };

        private static readonly ImageType[] DefaultImageTypes =
        {
            ImageType.Primary, ImageType.Banner, ImageType.Thumb
        };

        private static readonly ImageType[] ResumeImageTypes =
        {
            ImageType.Primary, ImageType.Backdrop, ImageType.Banner, ImageType.Thumb
        };

        private static readonly BaseItemKind[] MoviesAndEpisodes = { BaseItemKind.Movie, BaseItemKind.Episode };
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(HomeConstants.DiscoveryCacheExpirationMinutes);

        private readonly JellyfinApiClient _apiClient;
        private readonly ICacheManagerService _cacheManager;
        private readonly IPreferencesService _preferencesService;
        private readonly IUserProfileService _userProfileService;

        public MediaDiscoveryService(
            ILogger<MediaDiscoveryService> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            ICacheManagerService cacheManager,
            IPreferencesService preferencesService) : base(logger)
        {
            _apiClient = apiClient;
            _userProfileService = userProfileService;
            _cacheManager = cacheManager;
            _preferencesService = preferencesService;
        }

        /// <summary>
        ///     Movies and episodes, newest first. The server groups several new episodes of one series
        ///     into that series, with ChildCount the number of new episodes.
        /// </summary>
        public Task<IEnumerable<BaseItemDto>> GetRecentlyAddedAsync(CancellationToken cancellationToken)
        {
            return GetLatestAsync(MoviesAndEpisodes, "RecentlyAdded", cancellationToken);
        }

        // The sections' `return await`: a Task<List<T>> is no Task<IEnumerable<T>> without it
        public async Task<IEnumerable<BaseItemDto>> GetContinueWatchingAsync(CancellationToken cancellationToken)
        {
            // Not cached: the home page refreshes this row on every return, to show what was just played
            return await RunSectionAsync(async (userIdGuid, ct) =>
            {
                var response = await _apiClient.UserItems.Resume.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userIdGuid;
                    config.QueryParameters.Limit = HomeConstants.DefaultQueryLimit;
                    config.QueryParameters.Fields = DefaultItemFields;
                    config.QueryParameters.EnableImageTypes = ResumeImageTypes;
                    config.QueryParameters.IncludeItemTypes = MoviesAndEpisodes;
                    config.QueryParameters.EnableUserData = true;
                }, ct).ConfigureAwait(false);

                return response?.Items ?? new List<BaseItemDto>();
            }, cancellationToken);
        }

        public async Task<IEnumerable<BaseItemDto>> GetRecommendedAsync(CancellationToken cancellationToken)
        {
            return await RunSectionAsync(async (userGuid, ct) =>
            {
                var response = await _apiClient.Movies.Recommendations.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userGuid;
                    // Note: Recommendations endpoint doesn't have a Limit parameter
                    config.QueryParameters.Fields = DefaultItemFields;
                }, ct).ConfigureAwait(false);

                var recommendations = new List<BaseItemDto>();

                if (response != null)
                {
                    foreach (var group in response.Take(3).Where(g => g?.Items is { Count: > 0 }))
                    {
                        recommendations.AddRange(group.Items.Take(HomeConstants.DefaultQueryLimit / 3));
                    }
                }

                return recommendations;
            }, cancellationToken);
        }

        /// <summary>
        ///     The newest movies: by release date, or (the setting off) by the date the file was added,
        ///     which is the server's "latest" feed. Re-encoding a library resets the added date.
        /// </summary>
        public Task<IEnumerable<BaseItemDto>> GetLatestMoviesAsync(CancellationToken cancellationToken)
        {
            return GetLatestRowAsync(BaseItemKind.Movie, "LatestMovies", FetchMoviesByReleaseAsync,
                cancellationToken);
        }

        /// <summary>
        ///     The shows with the newest episodes: by the episodes' air date, or (the setting off) the
        ///     server's "latest" feed, by the date files were added.
        /// </summary>
        public Task<IEnumerable<BaseItemDto>> GetLatestShowsAsync(CancellationToken cancellationToken)
        {
            return GetLatestRowAsync(BaseItemKind.Series, "LatestShows", FetchShowsByReleaseAsync,
                cancellationToken);
        }

        private async Task<IEnumerable<BaseItemDto>> GetLatestRowAsync(BaseItemKind kind, string section,
            Func<Guid, CancellationToken, Task<List<BaseItemDto>>> fetchByReleaseDate,
            CancellationToken cancellationToken)
        {
            var preferences = await _preferencesService.GetAppPreferencesAsync().ConfigureAwait(false);
            if (!preferences.SortLatestByReleaseDate)
            {
                return await GetLatestAsync(new[] { kind }, section, cancellationToken).ConfigureAwait(false);
            }

            return await GetCachedOrFetchAsync(
                $"{section}ByRelease",
                token => RunSectionAsync(fetchByReleaseDate, token),
                cancellationToken);
        }

        private async Task<List<BaseItemDto>> FetchMoviesByReleaseAsync(Guid userIdGuid, CancellationToken ct)
        {
            var response = await _apiClient.Items.GetAsync(config =>
            {
                config.QueryParameters.UserId = userIdGuid;
                config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie };
                config.QueryParameters.Recursive = true;
                config.QueryParameters.SortBy = new[] { ItemSortBy.PremiereDate, ItemSortBy.SortName };
                config.QueryParameters.SortOrder = new[] { SortOrder.Descending };
                config.QueryParameters.Limit = HomeConstants.DefaultQueryLimit;
                config.QueryParameters.Fields = DefaultItemFields;
                config.QueryParameters.EnableImageTypes = DefaultImageTypes;
            }, ct).ConfigureAwait(false);

            return response?.Items ?? new List<BaseItemDto>();
        }

        private async Task<List<BaseItemDto>> FetchShowsByReleaseAsync(Guid userIdGuid, CancellationToken ct)
        {
            // Newest-aired episodes first; their series, in that order. Missing episodes are
            // the server's placeholders for episodes without a file.
            var episodes = await _apiClient.Items.GetAsync(config =>
            {
                config.QueryParameters.UserId = userIdGuid;
                config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Episode };
                config.QueryParameters.Recursive = true;
                config.QueryParameters.IsMissing = false;
                config.QueryParameters.SortBy = new[] { ItemSortBy.PremiereDate };
                config.QueryParameters.SortOrder = new[] { SortOrder.Descending };
                // Enough episodes to find a row of different series when a few shows air daily
                config.QueryParameters.Limit = SystemConstants.ExtendedQueryLimit;
                config.QueryParameters.EnableImages = false;
                config.QueryParameters.EnableUserData = false;
            }, ct).ConfigureAwait(false);

            var seriesIds = episodes?.Items?.Where(e => e.SeriesId.HasValue).Select(e => e.SeriesId.Value)
                .Distinct().Take(HomeConstants.DefaultQueryLimit).ToList() ?? new List<Guid>();
            if (seriesIds.Count == 0)
            {
                return new List<BaseItemDto>();
            }

            var series = await _apiClient.Items.GetAsync(config =>
            {
                config.QueryParameters.UserId = userIdGuid;
                config.QueryParameters.Ids = seriesIds.Select(id => (Guid?)id).ToArray();
                config.QueryParameters.Fields = DefaultItemFields;
                config.QueryParameters.EnableImageTypes = DefaultImageTypes;
            }, ct).ConfigureAwait(false);

            var byId = series?.Items?.Where(s => s.Id.HasValue).ToDictionary(s => s.Id.Value)
                       ?? new Dictionary<Guid, BaseItemDto>();
            return seriesIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }

        private async Task<IEnumerable<BaseItemDto>> GetLatestAsync(BaseItemKind[] kinds, string section,
            CancellationToken cancellationToken)
        {
            return await GetCachedOrFetchAsync(
                section,
                token => RunSectionAsync(async (userIdGuid, ct) =>
                {
                    var response = await _apiClient.Items.Latest.GetAsync(config =>
                    {
                        config.QueryParameters.UserId = userIdGuid;
                        config.QueryParameters.IncludeItemTypes = kinds;
                        config.QueryParameters.Limit = HomeConstants.DefaultQueryLimit;
                        config.QueryParameters.Fields = DefaultItemFields;
                        config.QueryParameters.EnableImageTypes = DefaultImageTypes;
                    }, ct).ConfigureAwait(false);

                    return response ?? new List<BaseItemDto>();
                }, token),
                cancellationToken);
        }

        public async Task<IEnumerable<BaseItemDto>> GetNextUpAsync(CancellationToken cancellationToken)
        {
            return await GetCachedOrFetchAsync(
                "NextUp",
                token => RunSectionAsync(async (userGuid, ct) =>
                {
                    var response = await _apiClient.Shows.NextUp.GetAsync(config =>
                    {
                        config.QueryParameters.UserId = userGuid;
                        config.QueryParameters.Limit = HomeConstants.DefaultQueryLimit;
                        config.QueryParameters.Fields = DefaultItemFields;
                        // Started episodes are Continue Watching's row
                        config.QueryParameters.EnableResumable = false;
                    }, ct).ConfigureAwait(false);

                    return response?.Items ?? new List<BaseItemDto>();
                }, token),
                cancellationToken,
                TimeSpan.FromMinutes(HomeConstants.NextUpCacheMinutes));
        }

        /// <summary>
        ///     What every home-screen section shares: it needs a signed-in user. A failure is the
        ///     caller's to retry or report -- turned into an empty list here, it was cached as
        ///     "nothing to show" and hid the row until the cache expired.
        /// </summary>
        private async Task<List<BaseItemDto>> RunSectionAsync(
            Func<Guid, CancellationToken, Task<List<BaseItemDto>>> fetch, CancellationToken cancellationToken)
        {
            if (!TryGetUserIdGuid(_userProfileService, out var userId))
            {
                return new List<BaseItemDto>();
            }

            return await fetch(userId, cancellationToken).ConfigureAwait(false);
        }

        private async Task<T> GetCachedOrFetchAsync<T>(string cacheKey, Func<CancellationToken, Task<T>> fetchFunc,
            CancellationToken cancellationToken, TimeSpan? customExpiration = null) where T : class
        {
            var expiration = customExpiration ?? CacheExpiration;

            // Prefix cache keys to avoid collisions with other services
            var fullCacheKey = $"MediaDiscovery_{cacheKey}";

            var cachedData = _cacheManager.Get<T>(fullCacheKey);
            if (cachedData != null)
            {
                Logger.LogDebug("Using cached data for {CacheKey}", cacheKey);
                return cachedData;
            }

            Logger.LogDebug("Fetching fresh data for {CacheKey}", cacheKey);
            var data = await fetchFunc(cancellationToken).ConfigureAwait(false);
            if (data != null)
            {
                _cacheManager.Set(fullCacheKey, data, expiration);
            }

            return data;
        }
    }
}
