using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    public class MediaDiscoveryService : BaseService, IMediaDiscoveryService
    {
        private const int MAX_SEARCH_HISTORY = 10;
        private readonly JellyfinApiClient _apiClient;
        private readonly IAuthenticationService _authService;

        private readonly TimeSpan _cacheExpiration =
            TimeSpan.FromMinutes(MediaConstants.DISCOVERY_CACHE_EXPIRATION_MINUTES); // Shorter cache for discovery data

        private readonly ICacheManagerService _cacheManager;
        private readonly INavigationStateService _navigationStateService;
        private readonly Dictionary<string, BaseItemDto[]> _recentSearches;
        private readonly IUserProfileService _userProfileService;

        public MediaDiscoveryService(
            ILogger<MediaDiscoveryService> logger,
            JellyfinApiClient apiClient,
            IAuthenticationService authService,
            IUserProfileService userProfileService,
            INavigationStateService navigationStateService,
            ICacheManagerService cacheManager) : base(logger)
        {
            _apiClient = apiClient;
            _authService = authService;
            _userProfileService = userProfileService;
            _navigationStateService = navigationStateService;
            _cacheManager = cacheManager;
            _recentSearches = new Dictionary<string, BaseItemDto[]>();
        }

        public event EventHandler<BaseItemDto[]> RecommendationsUpdated;
        public event EventHandler<BaseItemDto[]> ContinueWatchingUpdated;

        public async Task<IEnumerable<BaseItemDto>> GetRecentlyAddedAsync(int limit = 0,
            CancellationToken cancellationToken = default)
        {
            limit = ValidateLimit(limit);
            if (limit > MediaConstants.MAX_DISCOVERY_QUERY_LIMIT)
            {
                limit = MediaConstants.MAX_DISCOVERY_QUERY_LIMIT; // Reasonable upper limit
            }

            return await GetCachedOrFetchAsync(
                $"RecentlyAdded_{limit}",
                async ct =>
                {
                    var context = CreateErrorContext("GetRecentlyAdded", ErrorCategory.Media);
                    try
                    {
                        var userId = _userProfileService?.CurrentUserId;
                        if (string.IsNullOrEmpty(userId))
                        {
                            return new List<BaseItemDto>();
                        }

                        if (_apiClient == null)
                        {
                            Logger?.LogError("SDK client not available");
                            return new List<BaseItemDto>();
                        }

                        if (!Guid.TryParse(userId, out var userIdGuid))
                        {
                            Logger?.LogError($"Invalid user ID format: {userId}");
                            return new List<BaseItemDto>();
                        }

                        // Simplified query - single sort for better performance
                        var response = await _apiClient.Items.GetAsync(config =>
                        {
                            config.QueryParameters.UserId = userIdGuid;
                            config.QueryParameters.SortBy = new[] { ItemSortBy.DateCreated };
                            config.QueryParameters.SortOrder = new[] { SortOrder.Descending };
                            config.QueryParameters.Limit = limit + 10; // Small buffer for grouping
                            config.QueryParameters.Fields =
                                new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                            config.QueryParameters.EnableImageTypes =
                                new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };
                            config.QueryParameters.Recursive = true;
                            config.QueryParameters.ExcludeItemTypes = new[] { BaseItemKind.CollectionFolder };
                            config.QueryParameters.IncludeItemTypes = new[]
                            {
                                BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Series
                            };
                        }, ct).ConfigureAwait(false);

                        var items = response?.Items ?? new List<BaseItemDto>();

                        // Optimize: Single pass through items with capacity hints
                        var estimatedCapacity = Math.Min(items.Count, limit);
                        var result = new List<BaseItemDto>(estimatedCapacity);
                        var episodeGroups = new Dictionary<Guid, List<BaseItemDto>>(estimatedCapacity / 2);

                        // Single pass - process all items
                        foreach (var item in items)
                        {
                            if (item.Type == BaseItemDto_Type.Episode && item.SeriesId.HasValue)
                            {
                                // Group episodes by series
                                var seriesId = item.SeriesId.Value;
                                if (!episodeGroups.TryGetValue(seriesId, out var episodes))
                                {
                                    episodes = new List<BaseItemDto>(4); // Most series will have few recent episodes
                                    episodeGroups[seriesId] = episodes;
                                }

                                episodes.Add(item);
                            }
                            else
                            {
                                // Non-episodes go directly to result
                                result.Add(item);
                            }
                        }

                        // Process grouped episodes
                        foreach (var group in episodeGroups)
                        {
                            var episodes = group.Value;
                            if (!episodes.Any())
                            {
                                continue;
                            }

                            if (episodes.Count == 1)
                            {
                                // Single episode
                                result.Add(episodes[0]);
                            }
                            else
                            {
                                // Multiple episodes - create grouped item
                                // Find most recent episode without sorting entire list
                                var mostRecent = episodes[0];
                                for (var i = 1; i < episodes.Count; i++)
                                {
                                    if (episodes[i].DateCreated > mostRecent.DateCreated)
                                    {
                                        mostRecent = episodes[i];
                                    }
                                }

                                // Create a pseudo-series item for grouped episodes
                                var groupedItem = new BaseItemDto
                                {
                                    Id = mostRecent.SeriesId ?? mostRecent.Id,
                                    Name = mostRecent.SeriesName,
                                    Type = BaseItemDto_Type.Series,
                                    SeriesId = mostRecent.SeriesId,
                                    SeriesName = mostRecent.SeriesName,
                                    DateCreated = mostRecent.DateCreated,
                                    ImageTags = mostRecent.ImageTags,
                                    PrimaryImageAspectRatio = mostRecent.PrimaryImageAspectRatio,
                                    // Store episode count for display
                                    ChildCount = episodes.Count,
                                    // Mark as grouped with special prefix
                                    Overview = $"__RecentlyAddedGrouped__{episodes.Count}"
                                };
                                result.Add(groupedItem);
                            }
                        }

                        // Sort by date and take the requested limit
                        var finalResult = result.OrderByDescending(i => i.DateCreated).Take(limit).ToList();

                        // Debug logging
                        foreach (var item in finalResult)
                        {
                            if (item.Overview?.StartsWith("__RecentlyAddedGrouped__") == true)
                            {
#if DEBUG
                                Logger?.LogDebug($"Recently Added Grouped: {item.Name} - {item.Overview}");
#endif
                            }
                        }

                        return finalResult;
                    }
                    catch (Exception ex)
                    {
                        return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
                    }
                },
                cancellationToken);
        }

        public async Task<IEnumerable<BaseItemDto>> GetContinueWatchingAsync(int limit = 0,
            CancellationToken cancellationToken = default)
        {
            limit = ValidateLimit(limit);
            if (limit > 100)
            {
                limit = 100;
            }

            // Continue watching changes frequently, use shorter cache
            return await GetCachedOrFetchAsync(
                $"ContinueWatching_{limit}",
                async ct =>
                {
                    var context = CreateErrorContext("GetContinueWatching", ErrorCategory.Media);
                    try
                    {
                        var userId = _userProfileService?.CurrentUserId;
                        if (string.IsNullOrEmpty(userId))
                        {
                            return new List<BaseItemDto>();
                        }

                        if (_apiClient == null)
                        {
                            Logger?.LogError("SDK client not available");
                            return new List<BaseItemDto>();
                        }

                        if (!Guid.TryParse(userId, out var userIdGuid))
                        {
                            Logger?.LogError($"Invalid user ID format: {userId}");
                            return new List<BaseItemDto>();
                        }
                        var response = await _apiClient.UserItems.Resume.GetAsync(config =>
                        {
                            config.QueryParameters.UserId = userIdGuid;
                            config.QueryParameters.Limit = limit;
                            config.QueryParameters.Fields =
                                new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                            config.QueryParameters.EnableImageTypes = new[]
                            {
                                ImageType.Primary, ImageType.Backdrop, ImageType.Banner, ImageType.Thumb
                            };
                            config.QueryParameters.IncludeItemTypes =
                                new[] { BaseItemKind.Movie, BaseItemKind.Episode };
                            config.QueryParameters.EnableUserData = true;
                        }, ct).ConfigureAwait(false);

                        var items = response?.Items ?? new List<BaseItemDto>();
                        await UIHelper.RunOnUIThreadAsync(() =>
                        {
                            ContinueWatchingUpdated?.Invoke(this, items.ToArray());
                        }, logger: Logger);
                        return items;
                    }
                    catch (Exception ex)
                    {
                        return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
                    }
                },
                cancellationToken,
                TimeSpan.FromSeconds(MediaConstants
                    .CONTINUE_WATCHING_CACHE_SECONDS)); // Very short cache for continue watching
        }

        public async Task<IEnumerable<BaseItemDto>> GetRecommendedAsync(int limit = 0,
            CancellationToken cancellationToken = default)
        {
            limit = ValidateLimit(limit);
            if (limit > 100)
            {
                limit = 100;
            }

            var context = CreateErrorContext("GetRecommended", ErrorCategory.Media);
            try
            {
                var userId = _userProfileService?.CurrentUserId;
                if (string.IsNullOrEmpty(userId))
                {
                    return new List<BaseItemDto>();
                }

                if (_apiClient == null)
                {
                    Logger?.LogError("SDK client not available");
                    return new List<BaseItemDto>();
                }

                if (!Guid.TryParse(userId, out var userGuid))
                {
                    Logger?.LogError($"Invalid user ID format: {userId}");
                    return new List<BaseItemDto>();
                }

                var response = await _apiClient.Movies.Recommendations.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userGuid;
                    // Note: Recommendations endpoint doesn't have a Limit parameter
                    config.QueryParameters.Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                }, cancellationToken).ConfigureAwait(false);

                var recommendations = new List<BaseItemDto>();

                if (response?.Any() == true)
                {
                    foreach (var group in response.Take(3))
                    {
                        if (group?.Items?.Any() == true)
                        {
                            recommendations.AddRange(group.Items.Take(limit / 3));
                        }
                    }
                }

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    RecommendationsUpdated?.Invoke(this, recommendations.ToArray());
                }, logger: Logger);
                return recommendations;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public async Task<IEnumerable<BaseItemDto>> GetNextUpEpisodesAsync(string seriesId, int limit = 0,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(seriesId))
            {
                Logger?.LogError("Series ID cannot be null or empty");
                return new List<BaseItemDto>();
            }

            limit = ValidateLimit(limit);
            if (limit > 100)
            {
                limit = 100;
            }

            var context = CreateErrorContext("GetNextUpEpisodes", ErrorCategory.Media);
            try
            {
                var userId = _userProfileService?.CurrentUserId;
                if (string.IsNullOrEmpty(userId))
                {
                    return new List<BaseItemDto>();
                }

                if (_apiClient == null)
                {
                    Logger?.LogError("SDK client not available");
                    return new List<BaseItemDto>();
                }

                if (!Guid.TryParse(userId, out var userGuid))
                {
                    Logger?.LogError($"Invalid user ID format: {userId}");
                    return new List<BaseItemDto>();
                }

                var response = await _apiClient.Shows.NextUp.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userGuid;
                    config.QueryParameters.SeriesId = new Guid(seriesId);
                    config.QueryParameters.Limit = limit;
                    config.QueryParameters.Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                }, cancellationToken).ConfigureAwait(false);

                return response?.Items ?? new List<BaseItemDto>();
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public async Task<IEnumerable<BaseItemDto>> GetLatestMoviesAsync(int limit = 0,
            CancellationToken cancellationToken = default)
        {
            limit = ValidateLimit(limit);
            if (limit > 100)
            {
                limit = 100;
            }

            return await GetCachedOrFetchAsync(
                $"LatestMovies_{limit}",
                async ct =>
                {
                    var context = CreateErrorContext("GetLatestMovies", ErrorCategory.Media);
                    try
                    {
                        var userId = _userProfileService?.CurrentUserId;
                        if (string.IsNullOrEmpty(userId))
                        {
                            return new List<BaseItemDto>();
                        }

                        if (_apiClient == null)
                        {
                            Logger?.LogError("SDK client not available");
                            return new List<BaseItemDto>();
                        }

                        if (!Guid.TryParse(userId, out var userIdGuid))
                        {
                            Logger?.LogError($"Invalid user ID format: {userId}");
                            return new List<BaseItemDto>();
                        }
                        var response = await _apiClient.Items.Latest.GetAsync(config =>
                        {
                            config.QueryParameters.UserId = userIdGuid;
                            config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie };
                            config.QueryParameters.Limit = limit;
                            config.QueryParameters.Fields =
                                new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                            config.QueryParameters.EnableImageTypes =
                                new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };
                        }, ct).ConfigureAwait(false);

                        return response ?? new List<BaseItemDto>();
                    }
                    catch (Exception ex)
                    {
                        return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
                    }
                },
                cancellationToken);
        }

        public async Task<IEnumerable<BaseItemDto>> GetLatestShowsAsync(int limit = 0,
            CancellationToken cancellationToken = default)
        {
            limit = ValidateLimit(limit);
            if (limit > 100)
            {
                limit = 100;
            }

            return await GetCachedOrFetchAsync(
                $"LatestShows_{limit}",
                async ct =>
                {
                    var context = CreateErrorContext("GetLatestShows", ErrorCategory.Media);
                    try
                    {
                        var userId = _userProfileService?.CurrentUserId;
                        if (string.IsNullOrEmpty(userId))
                        {
                            return new List<BaseItemDto>();
                        }

                        if (_apiClient == null)
                        {
                            Logger?.LogError("SDK client not available");
                            return new List<BaseItemDto>();
                        }

                        if (!Guid.TryParse(userId, out var userIdGuid))
                        {
                            Logger?.LogError($"Invalid user ID format: {userId}");
                            return new List<BaseItemDto>();
                        }
                        var response = await _apiClient.Items.Latest.GetAsync(config =>
                        {
                            config.QueryParameters.UserId = userIdGuid;
                            config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Series };
                            config.QueryParameters.Limit = limit;
                            config.QueryParameters.Fields =
                                new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                            config.QueryParameters.EnableImageTypes =
                                new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };
                        }, ct).ConfigureAwait(false);

                        return response ?? new List<BaseItemDto>();
                    }
                    catch (Exception ex)
                    {
                        return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
                    }
                },
                cancellationToken);
        }

        public async Task<IEnumerable<BaseItemDto>> GetFavoriteItemsAsync(int limit = 0,
            CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("GetFavoriteItems", ErrorCategory.Media);
            try
            {
                var userId = _userProfileService.CurrentUserId;
                if (string.IsNullOrEmpty(userId))
                {
                    return new List<BaseItemDto>();
                }

                var userIdGuid = Guid.Parse(userId);
                var response = await _apiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userIdGuid;
                    config.QueryParameters.IsFavorite = true;
                    config.QueryParameters.Limit = limit;
                    config.QueryParameters.Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                    config.QueryParameters.EnableImageTypes =
                        new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };
                    config.QueryParameters.Recursive = true;
                }, cancellationToken).ConfigureAwait(false);

                return response?.Items ?? new List<BaseItemDto>();
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public async Task<SearchHintResult> SearchMediaAsync(string searchTerm, int limit = 0,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                Logger?.LogWarning("Search term cannot be null or empty");
                return new SearchHintResult { SearchHints = new List<SearchHint>(), TotalRecordCount = 0 };
            }

            limit = ValidateLimit(limit);
            if (limit > 100)
            {
                limit = 100;
            }

            var context = CreateErrorContext("SearchMedia", ErrorCategory.Media);
            try
            {
                if (_apiClient == null)
                {
                    Logger?.LogError("SDK client not available");
                    return new SearchHintResult { SearchHints = new List<SearchHint>(), TotalRecordCount = 0 };
                }

                var userId = _userProfileService.CurrentUserId;
                if (string.IsNullOrEmpty(userId))
                {
                    Logger?.LogError("User ID not available");
                    return new SearchHintResult { SearchHints = new List<SearchHint>(), TotalRecordCount = 0 };
                }

                if (!Guid.TryParse(userId, out var userGuid))
                {
                    Logger?.LogError($"Invalid user ID format: {userId}");
                    return new SearchHintResult { SearchHints = new List<SearchHint>(), TotalRecordCount = 0 };
                }

                var response = await _apiClient.Search.Hints.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userGuid;
                    config.QueryParameters.SearchTerm = searchTerm;
                    config.QueryParameters.Limit = limit;
                    config.QueryParameters.IncludeArtists = true;
                    config.QueryParameters.IncludeGenres = true;
                    config.QueryParameters.IncludeMedia = true;
                    config.QueryParameters.IncludePeople = true;
                    config.QueryParameters.IncludeStudios = true;
                }, cancellationToken).ConfigureAwait(false);

                return response ?? new SearchHintResult { SearchHints = new List<SearchHint>(), TotalRecordCount = 0 };
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context,
                    new SearchHintResult { SearchHints = new List<SearchHint>(), TotalRecordCount = 0 });
            }
        }

        public async Task<IEnumerable<BaseItemDto>> GetByGenreAsync(string genreId, int limit = 0,
            CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("GetByGenre", ErrorCategory.Media);
            try
            {
                var userId = _userProfileService.CurrentUserId;
                if (string.IsNullOrEmpty(userId))
                {
                    return new List<BaseItemDto>();
                }

                var userIdGuid = Guid.Parse(userId);
                var response = await _apiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userIdGuid;
                    config.QueryParameters.GenreIds = new[] { (Guid?)new Guid(genreId) };
                    config.QueryParameters.Limit = limit;
                    config.QueryParameters.Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                    config.QueryParameters.EnableImageTypes =
                        new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };
                    config.QueryParameters.Recursive = true;
                }, cancellationToken).ConfigureAwait(false);

                return response?.Items ?? new List<BaseItemDto>();
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public async Task<IEnumerable<BaseItemDto>> GetSimilarItemsAsync(string itemId, int limit = 0,
            CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("GetSimilarItems", ErrorCategory.Media);
            try
            {
                var userId = _userProfileService.CurrentUserId;
                if (string.IsNullOrEmpty(userId))
                {
                    return new List<BaseItemDto>();
                }

                if (!Guid.TryParse(userId, out var userGuid))
                {
                    Logger?.LogError($"Invalid user ID format: {userId}");
                    return new List<BaseItemDto>();
                }

                var response = await _apiClient.Items[new Guid(itemId)].Similar.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userGuid;
                    config.QueryParameters.Limit = limit;
                    config.QueryParameters.Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                }, cancellationToken).ConfigureAwait(false);

                return response?.Items ?? new List<BaseItemDto>();
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public async Task<IEnumerable<BaseItemDto>> GetSuggestionsAsync(CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("GetSuggestions", ErrorCategory.Media);
            try
            {
                var userId = _userProfileService.CurrentUserId;
                if (string.IsNullOrEmpty(userId))
                {
                    return new List<BaseItemDto>();
                }

                var userIdGuid = Guid.Parse(userId);
                var response = await _apiClient.Items.Suggestions.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userIdGuid;
                    config.QueryParameters.MediaType = new[] { MediaType.Video };
                    config.QueryParameters.Type = new[] { BaseItemKind.Movie, BaseItemKind.Series };
                    config.QueryParameters.Limit = MediaConstants.DEFAULT_QUERY_LIMIT;
                }, cancellationToken).ConfigureAwait(false);

                return response?.Items ?? new List<BaseItemDto>();
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public async Task<IEnumerable<BaseItemDto>> GetNextUpAsync(CancellationToken cancellationToken = default)
        {
            return await GetCachedOrFetchAsync(
                "NextUp_Default",
                async ct =>
                {
                    var context = CreateErrorContext("GetNextUp", ErrorCategory.Media);
                    try
                    {
                        var userId = _userProfileService.CurrentUserId;
                        if (string.IsNullOrEmpty(userId))
                        {
                            return new List<BaseItemDto>();
                        }

                        if (!Guid.TryParse(userId, out var userGuid))
                        {
                            Logger?.LogError($"Invalid user ID format: {userId}");
                            return new List<BaseItemDto>();
                        }

                        var response = await _apiClient.Shows.NextUp.GetAsync(config =>
                        {
                            config.QueryParameters.UserId = userGuid;
                            config.QueryParameters.Limit = MediaConstants.DEFAULT_QUERY_LIMIT;
                            config.QueryParameters.Fields =
                                new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                        }, ct).ConfigureAwait(false);

                        var items = response?.Items ?? new List<BaseItemDto>();

                        // Filter out any items that have progress (shouldn't be necessary but ensures no overlap with Continue Watching)
                        return items.Where(item =>
                            item.UserData == null ||
                            item.UserData.PlaybackPositionTicks == null ||
                            item.UserData.PlaybackPositionTicks == 0
                        ).ToList();
                    }
                    catch (Exception ex)
                    {
                        return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
                    }
                },
                cancellationToken,
                TimeSpan.FromMinutes(MediaConstants.NEXT_UP_CACHE_MINUTES)); // Shorter cache for next up
        }

        public async Task<IEnumerable<BaseItemDto>> SearchAsync(string searchTerm, string[] includeItemTypes = null,
            string[] includeMediaTypes = null, bool includeGenres = true, bool includePeople = true,
            bool includeStudios = true, CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("Search", ErrorCategory.Media);
            try
            {
                var userId = _userProfileService.CurrentUserId;
                if (string.IsNullOrEmpty(userId) || string.IsNullOrWhiteSpace(searchTerm))
                {
                    return new List<BaseItemDto>();
                }

                var userIdGuid = Guid.Parse(userId);
                var response = await _apiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userIdGuid;
                    config.QueryParameters.SearchTerm = searchTerm;
                    config.QueryParameters.Limit = MediaConstants.LARGE_QUERY_LIMIT;
                    config.QueryParameters.Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                    config.QueryParameters.EnableImageTypes =
                        new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };
                    config.QueryParameters.Recursive = true;

                    if (includeItemTypes?.Length > 0)
                    {
                        var parsedTypes = includeItemTypes
                            .Select(t => Enum.TryParse<BaseItemKind>(t, out var kind) ? (BaseItemKind?)kind : null)
                            .Where(k => k.HasValue)
                            .Select(k => k.Value)
                            .ToArray();
                        if (parsedTypes.Length > 0)
                        {
                            config.QueryParameters.IncludeItemTypes = parsedTypes;
                        }
                    }

                    // Filter by current library if one is selected
                    var (libraryId, _, _) = _navigationStateService.GetLibrarySelection();
                    if (libraryId.HasValue && libraryId.Value != Guid.Empty)
                    {
                        config.QueryParameters.ParentId = libraryId.Value;
                    }
                }, cancellationToken).ConfigureAwait(false);

                var results = response?.Items ?? new List<BaseItemDto>();

                // Store search results
                if (_recentSearches.Count >= MAX_SEARCH_HISTORY)
                {
                    var oldestKey = _recentSearches.Keys.FirstOrDefault();
                    if (oldestKey != null)
                    {
                        _recentSearches.Remove(oldestKey);
                    }
                }

                _recentSearches[searchTerm] = results.ToArray();

                return results;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public async Task<IEnumerable<BaseItemDto>> GetPersonItemsAsync(string personId,
            string[] includeItemTypes = null, CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("GetPersonItems", ErrorCategory.Media);
            try
            {
                var userId = _userProfileService.CurrentUserId;
                if (string.IsNullOrEmpty(userId))
                {
                    return new List<BaseItemDto>();
                }

                var userIdGuid = Guid.Parse(userId);
                var response = await _apiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userIdGuid;
                    config.QueryParameters.PersonIds = new[] { (Guid?)new Guid(personId) };
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                    config.QueryParameters.EnableImageTypes =
                        new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };

                    if (includeItemTypes?.Length > 0)
                    {
                        var parsedTypes = includeItemTypes
                            .Select(t => Enum.TryParse<BaseItemKind>(t, out var kind) ? (BaseItemKind?)kind : null)
                            .Where(k => k.HasValue)
                            .Select(k => k.Value)
                            .ToArray();
                        if (parsedTypes.Length > 0)
                        {
                            config.QueryParameters.IncludeItemTypes = parsedTypes;
                        }
                    }

                    // Filter by current library if one is selected
                    var (libraryId, _, _) = _navigationStateService.GetLibrarySelection();
                    if (libraryId.HasValue && libraryId.Value != Guid.Empty)
                    {
                        config.QueryParameters.ParentId = libraryId.Value;
                    }
                }, cancellationToken).ConfigureAwait(false);

                return response?.Items ?? new List<BaseItemDto>();
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public async Task<IEnumerable<BaseItemDto>> GetLatestMediaAsync(string[] includeItemTypes = null,
            string parentId = null, int limit = 0, CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("GetLatestMedia", ErrorCategory.Media);
            try
            {
                var userId = _userProfileService.CurrentUserId;
                if (string.IsNullOrEmpty(userId))
                {
                    return new List<BaseItemDto>();
                }

                var userIdGuid = Guid.Parse(userId);
                var response = await _apiClient.Items.Latest.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userIdGuid;

                    if (includeItemTypes?.Length > 0)
                    {
                        var parsedTypes = includeItemTypes
                            .Select(t => Enum.TryParse<BaseItemKind>(t, out var kind) ? (BaseItemKind?)kind : null)
                            .Where(k => k.HasValue)
                            .Select(k => k.Value)
                            .ToArray();
                        if (parsedTypes.Length > 0)
                        {
                            config.QueryParameters.IncludeItemTypes = parsedTypes;
                        }
                    }

                    if (!string.IsNullOrEmpty(parentId))
                    {
                        if (Guid.TryParse(parentId, out var parentGuid))
                    {
                        config.QueryParameters.ParentId = parentGuid;
                    }
                    else
                    {
                        Logger?.LogWarning($"Invalid parent ID format: {parentId}");
                    }
                    }

                    config.QueryParameters.Limit = limit;
                    config.QueryParameters.Fields = new[] { ItemFields.Overview, ItemFields.PrimaryImageAspectRatio };
                    config.QueryParameters.EnableImageTypes =
                        new[] { ImageType.Primary, ImageType.Banner, ImageType.Thumb };
                }, cancellationToken).ConfigureAwait(false);

                return response ?? new List<BaseItemDto>();
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, new List<BaseItemDto>());
            }
        }

        public IEnumerable<KeyValuePair<string, BaseItemDto[]>> GetRecentSearches()
        {
            return _recentSearches.ToList();
        }

        public void ClearRecentSearches()
        {
            _recentSearches.Clear();
        }

        private int ValidateLimit(int limit)
        {
            return limit <= 0 ? MediaConstants.DEFAULT_QUERY_LIMIT : limit;
        }

        private async Task<T> GetCachedOrFetchAsync<T>(string cacheKey, Func<CancellationToken, Task<T>> fetchFunc,
            CancellationToken cancellationToken, TimeSpan? customExpiration = null) where T : class
        {
            var expiration = customExpiration ?? _cacheExpiration;

            // Prefix cache keys to avoid collisions with other services
            var fullCacheKey = $"MediaDiscovery_{cacheKey}";

            // Check cache
            var cachedData = _cacheManager?.Get<T>(fullCacheKey);
            if (cachedData != null)
            {
#if DEBUG
                Logger?.LogDebug($"Using cached data for {cacheKey}");
#endif
                return cachedData;
            }

            // Fetch fresh data
#if DEBUG
            Logger?.LogDebug($"Fetching fresh data for {cacheKey}");
#endif
            var data = await fetchFunc(cancellationToken).ConfigureAwait(false); if (data != null && _cacheManager != null)
            {
                _cacheManager.Set(fullCacheKey, data, expiration);
            }

            return data;
        }
    }
}
