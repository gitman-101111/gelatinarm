using System;
using Gelatinarm.Shared.Base;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.SignIn
{
    public interface IUserProfileService
    {
        Guid? GetCurrentUserGuid();
    }

    public class UserProfileService : BaseService, IUserProfileService
    {
        private readonly IAuthenticationService _authService;

        public UserProfileService(
            ILogger<UserProfileService> logger,
            IAuthenticationService authService) : base(logger)
        {
            _authService = authService;
        }

        public Guid? GetCurrentUserGuid()
        {
            var userIdString = _authService.UserId;
            if (string.IsNullOrEmpty(userIdString))
            {
                Logger.LogDebug("User ID is null or empty");
                return null;
            }

            if (!Guid.TryParse(userIdString, out var userIdGuid))
            {
                Logger.LogWarning("Invalid user ID format: {UserId}", userIdString);
                return null;
            }

            return userIdGuid;
        }
    }
}
