using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Library
{
    public class LibrarySelectionViewModel : BaseViewModel
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly IAuthenticationService _authenticationService;
        private readonly IUserProfileService _userProfileService;

        public LibrarySelectionViewModel(
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            ILogger<LibrarySelectionViewModel> logger,
            IAuthenticationService authenticationService)
            : base(logger)
        {
            _apiClient = apiClient;
            _userProfileService = userProfileService;
            _authenticationService = authenticationService;
        }

        // The user the loaded Libraries belong to. This view model is a singleton, so after a
        // profile switch the list still holds the previous user's libraries until reloaded.
        private Guid? _librariesUserId;

        public ObservableCollection<BaseItemDto> Libraries { get; } = new();

        public async Task InitializeAsync()
        {
            if (Libraries.Count > 0 && _librariesUserId == _userProfileService.GetCurrentUserGuid())
            {
                Logger.LogDebug("Libraries already loaded, skipping initialization");
                return;
            }

            await LoadDataAsync(true);
        }

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            Logger.LogDebug("Loading user libraries for selection using SDK");

            if (!_authenticationService.IsAuthenticated || string.IsNullOrEmpty(_authenticationService.ServerUrl))
            {
                throw new InvalidOperationException("Please sign in to view your libraries.");
            }

            var userGuid = _userProfileService.GetCurrentUserGuid();
            if (!userGuid.HasValue)
            {
                throw new InvalidOperationException("User not identified. Cannot load libraries.");
            }

            var userViews = await _apiClient.UserViews.GetAsync(config => config.QueryParameters.UserId = userGuid.Value, cancellationToken)
                .ConfigureAwait(false);

            var validLibraries = userViews?.Items?
                .Where(library => library.Id != null && !string.IsNullOrEmpty(library.Name))
                .ToList();
            await RunOnUIThreadAsync(() =>
            {
                Libraries.ReplaceAll(validLibraries);
                _librariesUserId = userGuid;
            });

            Logger.LogInformation("Loaded {LibrariesCount} libraries: {Libraries}", Libraries.Count,
                string.Join(", ", Libraries.Select(library => $"{library.Name} ({library.CollectionType})")));
        }
    }
}
