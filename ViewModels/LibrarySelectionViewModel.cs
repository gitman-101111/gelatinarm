using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.ViewModels
{
    public class LibrarySelectionViewModel : BaseViewModel
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly IAuthenticationService _authenticationService;
        private readonly ILogger<LibrarySelectionViewModel> _logger;
        private readonly IUserProfileService _userProfileService;

        public LibrarySelectionViewModel(
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            ILogger<LibrarySelectionViewModel> logger,
            IAuthenticationService authenticationService)
            : base(logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _userProfileService = userProfileService ?? throw new ArgumentNullException(nameof(userProfileService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _authenticationService =
                authenticationService ?? throw new ArgumentNullException(nameof(authenticationService));
        }

        public ObservableCollection<BaseItemDto> Libraries { get; } = new();

        public async Task InitializeAsync()
        {
            // If we already have libraries loaded, don't reload
            if (Libraries.Any())
            {
                _logger.LogInformation("Libraries already loaded, skipping initialization");
                return;
            }

            // Use the standardized LoadDataAsync pattern
            await LoadDataAsync(true);
        }

        protected override async Task LoadDataCoreAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Loading user libraries for selection using SDK");

            // Check if we're authenticated and have a server URL
            if (!_authenticationService.IsAuthenticated || string.IsNullOrEmpty(_authenticationService.ServerUrl))
            {
                throw new InvalidOperationException("Please sign in to view your libraries.");
            }

            var userId = _userProfileService.CurrentUserId;
            if (string.IsNullOrEmpty(userId))
            {
                throw new InvalidOperationException("User not identified. Cannot load libraries.");
            }

            // Check if the API client is properly initialized
            if (_apiClient?.UserViews == null)
            {
                throw new InvalidOperationException(
                    "Unable to connect to server. Please check your connection settings.");
            }

            // Use API to get user views
            if (!Guid.TryParse(userId, out var userGuid))
            {
                Logger?.LogError($"Invalid user ID format: {userId}");
                throw new ArgumentException($"Invalid user ID format: {userId}", nameof(userId));
            }
            var userViews = await _apiClient.UserViews.GetAsync(config =>
            {
                config.QueryParameters.UserId = userGuid;
            }, cancellationToken).ConfigureAwait(false);

            if (userViews?.Items != null)
            {
                var validLibraries = userViews.Items
                    .Where(library => library.Id != null && !string.IsNullOrEmpty(library.Name))
                    .ToList();

                // UI collection updates must happen on UI thread
                await RunOnUIThreadAsync(() =>
                {
                    Libraries.ReplaceAll(validLibraries);
                });

                foreach (var library in validLibraries)
                {
                    _logger.LogInformation($"Added library: {library.Name} (Type: {library.CollectionType})");
                }
            }
            else
            {
                _logger.LogInformation("No libraries (views) returned from the server or userViews.Items was null.");
            }

            _logger.LogInformation($"Loaded {Libraries.Count} libraries");
        }

        protected override async Task RefreshDataCoreAsync()
        {
            // Clear and reload libraries on UI thread
            await RunOnUIThreadAsync(() =>
            {
                Libraries.Clear();
            });
            await LoadDataCoreAsync(DisposalCts.Token);
        }

        protected override async Task ClearDataCoreAsync()
        {
            await RunOnUIThreadAsync(() =>
            {
                Libraries.Clear();
            });
        }
    }
}
