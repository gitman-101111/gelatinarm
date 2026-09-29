using System;
using System.Threading.Tasks;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Details
{
    /// <summary>
    ///     The signed-in user's favorite and watched flags. Each call returns the item's user data
    ///     as the server has it afterwards (null when no one is signed in or that read fails); a
    ///     failed change throws.
    /// </summary>
    public interface IUserDataService
    {
        Task<UserItemDataDto> ToggleFavoriteAsync(Guid itemId, bool isFavorite);

        Task<UserItemDataDto> ToggleWatchedAsync(Guid itemId, bool isWatched);
    }

    public class UserDataService : BaseService, IUserDataService
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly IUserProfileService _userProfileService;

        public UserDataService(
            ILogger<UserDataService> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService) : base(logger)
        {
            _apiClient = apiClient;
            _userProfileService = userProfileService;
        }

        public Task<UserItemDataDto> ToggleFavoriteAsync(Guid itemId, bool isFavorite)
        {
            return SetUserFlagAsync("favorite", itemId, isFavorite,
                user => _apiClient.UserFavoriteItems[itemId].PostAsync(config => config.QueryParameters.UserId = user),
                user => _apiClient.UserFavoriteItems[itemId].DeleteAsync(config => config.QueryParameters.UserId = user));
        }

        public Task<UserItemDataDto> ToggleWatchedAsync(Guid itemId, bool isWatched)
        {
            return SetUserFlagAsync("watched", itemId, isWatched,
                user => _apiClient.UserPlayedItems[itemId].PostAsync(config => config.QueryParameters.UserId = user),
                user => _apiClient.UserPlayedItems[itemId].DeleteAsync(config => config.QueryParameters.UserId = user));
        }

        /// <summary>
        ///     Sets or clears one of the user's flags on an item (favorite, watched) and returns the
        ///     item's user data as the server now has it. A failed change reaches the caller, which
        ///     reports it (the details pages with a dialog, the player without one).
        /// </summary>
        private async Task<UserItemDataDto> SetUserFlagAsync(string flag, Guid itemId, bool value,
            Func<Guid, Task> set, Func<Guid, Task> clear)
        {
            var userGuid = _userProfileService.GetCurrentUserGuid();
            if (!userGuid.HasValue)
            {
                Logger.LogWarning("Cannot toggle {Flag} - no valid user ID", flag);
                return null;
            }

            Logger.LogInformation("Toggling {Flag} for item {ItemId} to {Value} for user {UserGuid}", flag, itemId, value, userGuid.Value);
            await (value ? set : clear)(userGuid.Value).ConfigureAwait(false);
            return await GetUserDataAsync(itemId, userGuid.Value).ConfigureAwait(false);
        }

        private async Task<UserItemDataDto> GetUserDataAsync(Guid itemId, Guid userId)
        {
            var context = CreateErrorContext("GetUserData", ErrorCategory.User);
            try
            {
                var item = await _apiClient.Items[itemId]
                    .GetAsync(config => config.QueryParameters.UserId = userId)
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
