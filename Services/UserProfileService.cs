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
    ///     SDK-based implementation of the user profile service
    /// </summary>
    public class UserProfileService : BaseService, IUserProfileService
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly IAuthenticationService _authService;

        private UserDto _currentUser;

        public UserProfileService(
            ILogger<UserProfileService> logger,
            JellyfinApiClient apiClient,
            IAuthenticationService authService) : base(logger)
        {
            _apiClient = apiClient;
            _authService = authService;
        }

        public string CurrentUserId => _currentUser?.Id?.ToString() ?? _authService.UserId;
        public string CurrentUserName => _currentUser?.Name ?? _authService.Username;
        public bool IsAdmin => _currentUser?.Policy?.IsAdministrator ?? false;

        /// <summary>
        ///     Gets the current user ID as a Guid, or null if invalid
        /// </summary>
        public Guid? GetCurrentUserGuid()
        {
            var userIdString = CurrentUserId;
            if (string.IsNullOrEmpty(userIdString))
            {
                Logger?.LogDebug("User ID is null or empty");
                return null;
            }

            if (!TryParseUserGuid(userIdString, out var userIdGuid))
            {
                return null;
            }

            return userIdGuid;
        }

        public async Task<bool> LoadUserProfileAsync(CancellationToken cancellationToken = default)
        {
            if (_authService == null || !_authService.IsAuthenticated)
            {
                Logger?.LogWarning("Cannot load user profile - not authenticated");
                return false;
            }

            if (_apiClient == null)
            {
                Logger?.LogError("API client not available");
                return false;
            }

            var context = CreateErrorContext("LoadUserProfile", ErrorCategory.User);
            try
            {
                _currentUser = await RetryAsync(
                    async () => await _apiClient.Users.Me.GetAsync(null, cancellationToken).ConfigureAwait(false),
                    Logger,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);

                if (_currentUser != null)
                {
                    Logger.LogInformation($"User profile loaded successfully: {_currentUser.Name}");
                    return true;
                }

                Logger.LogError("Failed to load user profile - null response");
                return false;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, false, false);
            }
        }

        public async Task<UserDto> GetUserAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(userId))
            {
                Logger?.LogWarning("GetUserAsync: userId cannot be null or empty");
                return null;
            }

            if (!TryParseUserGuid(userId, out var userGuid))
            {
                return null;
            }

            if (_apiClient == null)
            {
                Logger?.LogError("API client not available");
                return null;
            }

            var context = CreateErrorContext("GetUser", ErrorCategory.User);
            try
            {
                return await RetryAsync(
                    async () => await _apiClient.Users[userGuid].GetAsync(null, cancellationToken)
                        .ConfigureAwait(false),
                    Logger,
                    cancellationToken: cancellationToken
                ).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<UserDto>(ex, context, null);
            }
        }

        public async Task<UserDto> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        {
            // If we already have the current user cached, return it
            if (_currentUser != null)
            {
                return _currentUser;
            }

            var context = CreateErrorContext("GetCurrentUser", ErrorCategory.User);
            try
            {
                // Otherwise, load it
                await LoadUserProfileAsync(cancellationToken).ConfigureAwait(false);
                return _currentUser;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync<UserDto>(ex, context, null);
            }
        }


        public void ClearUserData()
        {
            _currentUser = null;
            Logger.LogInformation("User data cleared");
        }

        private bool TryParseUserGuid(string userId, out Guid userGuid)
        {
            userGuid = Guid.Empty;
            if (!Guid.TryParse(userId, out userGuid))
            {
                Logger?.LogWarning($"Invalid user ID format: {userId}");
                return false;
            }

            return true;
        }
    }
}
