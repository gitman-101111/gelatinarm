using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Media.Imaging;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     ViewModel for the PersonDetailsPage handling person data and related content
    /// </summary>
    public partial class PersonDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        [ObservableProperty] private string _birthDate;

        [ObservableProperty] private string _birthPlace;

        // Observable properties
        [ObservableProperty] private string _expandBioButtonText = "Show More";

        [ObservableProperty] private bool _isBioExpanded;

        [ObservableProperty] private bool _isBirthDateVisible;

        [ObservableProperty] private bool _isBirthPlaceVisible;

        [ObservableProperty] private bool _isExpandBioButtonVisible;

        [ObservableProperty] private bool _isMoviesSectionVisible;

        [ObservableProperty] private bool _isOverviewVisible;

        [ObservableProperty] private bool _isTVShowsSectionVisible;

        private CancellationTokenSource _loadCts;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _movies = new();

        [ObservableProperty] private double _overviewMaxHeight = 200;

        [ObservableProperty] private BitmapImage _personImage;

        [ObservableProperty] private string _personName;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _tvShows = new();

        public PersonDetailsViewModel(
            ILogger<PersonDetailsViewModel> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IMediaPlaybackService mediaPlaybackService,
            IUserDataService userDataService) : base(
            logger,
            apiClient,
            userProfileService,
            navigationService,
            imageLoadingService,
            mediaPlaybackService,
            userDataService)
        {
        }

        /// <summary>
        ///     Initialize the ViewModel with navigation parameter
        /// </summary>
        public override async Task InitializeAsync(object parameter)
        {
            if (parameter == null)
            {
                return;
            }

            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();

            var context = CreateErrorContext("InitializePerson");
            IsLoading = true;
            try
            {
                Guid personId;

                if (parameter is BaseItemDto dto)
                {
                    if (dto.Type == BaseItemDto_Type.Person && dto.Id.HasValue)
                    {
                        personId = dto.Id.Value;
                    }
                    else
                    {
                        throw new ArgumentException("Invalid person DTO");
                    }
                }
                else if (parameter is string guidString && Guid.TryParse(guidString, out personId))
                {
                    // Valid GUID from string
                }
                else if (parameter is Guid guid)
                {
                    personId = guid;
                }
                else
                {
                    throw new ArgumentException("Invalid parameter type for PersonDetailsViewModel");
                }

                await LoadPersonDetailsAsync(personId, _loadCts.Token);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task LoadPersonDetailsAsync(Guid personId, CancellationToken cancellationToken)
        {
            if (!UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("LoadPersonDetails");
            try
            {
                // Load person info using Items endpoint (Persons endpoint is for name-based lookup)
                var person = await ApiClient.Items[personId].GetAsync(config =>
                {
                    config.QueryParameters.UserId = UserIdGuid.Value;
                }, cancellationToken).ConfigureAwait(false);

                if (person == null)
                {
                    throw new Exception("Failed to load person details");
                }

                await RunOnUIThreadAsync(() =>
                {
                    CurrentItem = person;
                    UpdatePersonUI();
                });

                // Load related content in parallel
                var tasks = new List<Task>
                {
                    LoadMoviesAsync(personId, cancellationToken), LoadTVShowsAsync(personId, cancellationToken)
                };

                await Task.WhenAll(tasks).ConfigureAwait(false);

                Logger?.LogInformation(
                    $"PersonDetailsViewModel: Loaded person: {CurrentItem.Name} with {Movies.Count} movies and {TvShows.Count} TV shows");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private void UpdatePersonUI()
        {
            if (CurrentItem == null)
            {
                return;
            }

            // Person name
            PersonName = CurrentItem.Name ?? string.Empty;

            // Overview/Biography
            if (!string.IsNullOrEmpty(CurrentItem.Overview))
            {
                Overview = CurrentItem.Overview;
                IsOverviewVisible = true;

                // Check if biography needs expansion (simplified for ViewModel)
                // In a real implementation, this would be handled by the View
                if (CurrentItem.Overview.Length > 500)
                {
                    OverviewMaxHeight = 200;
                    IsExpandBioButtonVisible = true;
                }
                else
                {
                    OverviewMaxHeight = double.PositiveInfinity;
                    IsExpandBioButtonVisible = false;
                }
            }
            else
            {
                IsOverviewVisible = false;
                IsExpandBioButtonVisible = false;
            }

            // Birth date
            if (CurrentItem.PremiereDate.HasValue)
            {
                BirthDate = $"Born: {CurrentItem.PremiereDate.Value:MMMM d, yyyy}";
                IsBirthDateVisible = true;
            }
            else
            {
                IsBirthDateVisible = false;
            }

            // Birth place
            var firstLocation = CurrentItem.ProductionLocations?.FirstOrDefault();
            if (!string.IsNullOrEmpty(firstLocation))
            {
                BirthPlace = $"Birthplace: {firstLocation}";
                IsBirthPlaceVisible = true;
            }
            else
            {
                IsBirthPlaceVisible = false;
            }

            // Load person image
            LoadPersonImage();
        }

        private void LoadPersonImage()
        {
            if (CurrentItem?.Id == null)
            {
                return;
            }

            var context = CreateErrorContext("LoadPersonImage", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    string imageTag = null;
                    if (CurrentItem.ImageTags?.AdditionalData?.ContainsKey("Primary") == true)
                    {
                        imageTag = CurrentItem.ImageTags.AdditionalData["Primary"]?.ToString();
                    }

                    var imageUrl = ImageHelper.BuildImageUrl(CurrentItem.Id.Value, "Primary", 400, null, imageTag);
                    if (!string.IsNullOrEmpty(imageUrl))
                    {
                        await RunOnUIThreadAsync(() =>
                        {
                            PersonImage = new BitmapImage(new Uri(imageUrl));
                        });
                    }
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private async Task LoadMoviesAsync(Guid personId, CancellationToken cancellationToken)
        {
            if (!UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("LoadPersonMovies");
            try
            {
                var response = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.PersonIds = new Guid?[] { personId };
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Movie };
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.Fields = new[] { ItemFields.PrimaryImageAspectRatio };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.ProductionYear, ItemSortBy.SortName };
                    config.QueryParameters.SortOrder = new[] { SortOrder.Descending };
                }, cancellationToken).ConfigureAwait(false);

                if (response?.Items != null)
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        Movies.Clear();
                        foreach (var movie in response.Items)
                        {
                            Movies.Add(movie);
                        }

                        IsMoviesSectionVisible = Movies.Any();
                    });
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private async Task LoadTVShowsAsync(Guid personId, CancellationToken cancellationToken)
        {
            if (!UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("LoadPersonTVShows");
            try
            {
                var response = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.PersonIds = new Guid?[] { personId };
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Series };
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.Fields = new[] { ItemFields.PrimaryImageAspectRatio };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.ProductionYear, ItemSortBy.SortName };
                    config.QueryParameters.SortOrder = new[] { SortOrder.Descending };
                }, cancellationToken).ConfigureAwait(false);

                if (response?.Items != null)
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        TvShows.Clear();
                        foreach (var show in response.Items)
                        {
                            TvShows.Add(show);
                        }

                        IsTVShowsSectionVisible = TvShows.Any();
                    });
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        // Commands
        [RelayCommand]
        private void ToggleBioExpansion()
        {
            IsBioExpanded = !IsBioExpanded;

            if (IsBioExpanded)
            {
                OverviewMaxHeight = double.PositiveInfinity;
                ExpandBioButtonText = "Show Less";
            }
            else
            {
                OverviewMaxHeight = 200;
                ExpandBioButtonText = "Show More";
            }
        }

        [RelayCommand]
        private void NavigateToMovie(BaseItemDto movie)
        {
            if (movie != null)
            {
                NavigationService.NavigateToItemDetails(movie);
            }
        }

        [RelayCommand]
        private void NavigateToTVShow(BaseItemDto show)
        {
            if (show != null)
            {
                NavigationService.NavigateToItemDetails(show);
            }
        }

        [RelayCommand]
        private void NavigateToMediaItem(BaseItemDto item)
        {
            if (item == null)
            {
                return;
            }

            NavigationService.NavigateToItemDetails(item);
        }

        protected override void DisposeManaged()
        {
            base.DisposeManaged();
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        }

        /// <summary>
        ///     Implementation of abstract method from DetailsViewModel
        /// </summary>
        protected override async Task LoadAdditionalDataAsync()
        {
            // Person-specific loading is handled in InitializeAsync
            await Task.CompletedTask;
        }
    }
}
