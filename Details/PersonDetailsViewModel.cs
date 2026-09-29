using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Details
{
    public partial class PersonDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        [ObservableProperty] private string _birthDate;

        [ObservableProperty] private string _birthPlace;

        [ObservableProperty] private bool _isMoviesSectionVisible;

        [ObservableProperty] private bool _isTvShowsSectionVisible;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _movies = new();

        [ObservableProperty] private string _personName;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _tvShows = new();

        public PersonDetailsViewModel(
            ILogger<PersonDetailsViewModel> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IUserDataService userDataService) : base(
            logger,
            apiClient,
            userProfileService,
            navigationService,
            imageLoadingService,
            userDataService)
        {
        }

        public override Task InitializeAsync(object parameter)
        {
            return InitializeFromParameterAsync(parameter, "InitializePerson",
                (dto, loadToken) => dto.Type == BaseItemDto_Type.Person && dto.Id.HasValue
                    ? LoadPersonDetailsAsync(dto.Id.Value, loadToken)
                    : throw new ArgumentException("Invalid person DTO"),
                LoadPersonDetailsAsync);
        }

        private async Task LoadPersonDetailsAsync(Guid personId, CancellationToken cancellationToken)
        {
            if (!UserIdGuid.HasValue)
            {
                return;
            }

            // Items, not Persons: the Persons endpoint looks people up by name. A failure here
            // reaches InitializeFromParameterAsync, which reports it; each credit row reports its own.
            var person = await FetchRequiredItemAsync(personId, cancellationToken).ConfigureAwait(false);

            await RunOnUIThreadAsync(() =>
            {
                CurrentItem = person;
                UpdatePersonUi();
            });

            await Task.WhenAll(LoadMoviesAsync(personId, cancellationToken), LoadTvShowsAsync(personId, cancellationToken))
                .ConfigureAwait(false);

            Logger.LogDebug(
                "PersonDetailsViewModel: Loaded person: {CurrentItemName} with {MoviesCount} movies and {TvShowsCount} TV shows", CurrentItem.Name, Movies.Count, TvShows.Count);
        }

        private void UpdatePersonUi()
        {
            PersonName = CurrentItem.Name ?? string.Empty;

            // Each line shows when it has text (NullableToVisibilityConverter)
            Overview = CurrentItem.Overview;
            ClampOverview(CurrentItem.Overview, 500, 200);

            BirthDate = CurrentItem.PremiereDate.HasValue
                ? $"Born: {CurrentItem.PremiereDate.Value:MMMM d, yyyy}"
                : null;

            var firstLocation = CurrentItem.ProductionLocations?.FirstOrDefault();
            BirthPlace = string.IsNullOrEmpty(firstLocation) ? null : $"Birthplace: {firstLocation}";

            LoadPrimaryImage();
        }

        private Task LoadMoviesAsync(Guid personId, CancellationToken cancellationToken)
        {
            return LoadCreditsAsync(personId, BaseItemKind.Movie, Movies,
                visible => IsMoviesSectionVisible = visible, "LoadPersonMovies", cancellationToken);
        }

        private Task LoadTvShowsAsync(Guid personId, CancellationToken cancellationToken)
        {
            return LoadCreditsAsync(personId, BaseItemKind.Series, TvShows,
                visible => IsTvShowsSectionVisible = visible, "LoadPersonTVShows", cancellationToken);
        }

        /// <summary>
        ///     Fills one of the person's credit rows (newest first) and shows it when it has items
        /// </summary>
        private async Task LoadCreditsAsync(Guid personId, BaseItemKind kind, ObservableCollection<BaseItemDto> target,
            Action<bool> setVisible, string operation, CancellationToken cancellationToken)
        {
            var context = CreateErrorContext(operation);
            try
            {
                var response = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.PersonIds = new Guid?[] { personId };
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.IncludeItemTypes = new[] { kind };
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.Fields = new[] { ItemFields.PrimaryImageAspectRatio };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.ProductionYear, ItemSortBy.SortName };
                    config.QueryParameters.SortOrder = new[] { SortOrder.Descending };
                }, cancellationToken).ConfigureAwait(false);

                await RunOnUIThreadAsync(() =>
                {
                    target.ReplaceAll(response?.Items);
                    setVisible(target.Count > 0);
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }
    }
}
