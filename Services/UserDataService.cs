using System;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Service for managing user data operations like favorites and watched status
    /// </summary>
    public class UserDataService : BaseService, IUserDataService
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly IUserProfileService _userProfileService;

        public UserDataService(
            ILogger<UserDataService> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService) : base(logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
        }

        /// <inheritdoc />
        public async Task<UserItemDataDto> ToggleFavoriteAsync(Guid itemId, bool isFavorite, Guid? userId = null,
            CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("ToggleFavorite", ErrorCategory.User);
            try
            {
                var userGuid = userId ?? _userProfileService.GetCurrentUserGuid();
                if (!userGuid.HasValue)
                {
                    Logger.LogWarning("Cannot toggle favorite - no valid user ID");
                    return null;
                }

                Logger.LogInformation($"Toggling favorite for item {itemId} to {isFavorite} for user {userGuid.Value}");

                if (isFavorite)
                {
                    await _apiClient.UserFavoriteItems[itemId]
                        .PostAsync(config => config.QueryParameters.UserId = userGuid.Value, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _apiClient.UserFavoriteItems[itemId]
                        .DeleteAsync(config => config.QueryParameters.UserId = userGuid.Value, cancellationToken)
                        .ConfigureAwait(false);
                }
                return await GetUserDataAsync(itemId, userGuid, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<UserItemDataDto> ToggleWatchedAsync(Guid itemId, bool isWatched, Guid? userId = null,
            CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("ToggleWatched", ErrorCategory.User);
            try
            {
                var userGuid = userId ?? _userProfileService.GetCurrentUserGuid();
                if (!userGuid.HasValue)
                {
                    Logger.LogWarning("Cannot toggle watched - no valid user ID");
                    return null;
                }

                Logger.LogInformation($"Toggling watched for item {itemId} to {isWatched} for user {userGuid.Value}");

                if (isWatched)
                {
                    await _apiClient.UserPlayedItems[itemId]
                        .PostAsync(config => config.QueryParameters.UserId = userGuid.Value, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await _apiClient.UserPlayedItems[itemId]
                        .DeleteAsync(config => config.QueryParameters.UserId = userGuid.Value, cancellationToken)
                        .ConfigureAwait(false);
                }
                return await GetUserDataAsync(itemId, userGuid, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
                throw;
            }
        }

        /// <inheritdoc />
        public async Task<UserItemDataDto> GetUserDataAsync(Guid itemId, Guid? userId = null,
            CancellationToken cancellationToken = default)
        {
            var context = CreateErrorContext("GetUserData", ErrorCategory.User);
            try
            {
                var userGuid = userId ?? _userProfileService.GetCurrentUserGuid();
                if (!userGuid.HasValue)
                {
                    Logger.LogWarning("Cannot get user data - no valid user ID");
                    return null;
                }
                var item = await _apiClient.Items[itemId]
                    .GetAsync(config => config.QueryParameters.UserId = userGuid.Value, cancellationToken)
                    .ConfigureAwait(false);

                return item?.UserData;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<UserItemDataDto>(ex, context, null);
            }
        }


    }
}
