using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.Views;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Media;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     ViewModel for the SeasonDetailsPage handling season, series, and episode data
    /// </summary>
    public partial class SeasonDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        // Additional Services
        private readonly IEpisodeQueueService _episodeQueueService;
        private readonly IPreferencesService _preferencesService;

        [ObservableProperty] private string _airDate;

        [ObservableProperty] private ImageSource _backdropImage;

        [ObservableProperty] private BaseItemDto _currentSeason;

        [ObservableProperty] private string _episodeNumber;

        [ObservableProperty] private string _episodeOverview;

        [ObservableProperty] private ImageSource _episodeThumbnail;

        [ObservableProperty] private string _episodeTitle;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _episodes = new();

        [ObservableProperty] private bool _isAirDateSeparatorVisible;

        [ObservableProperty] private bool _isAirDateVisible;

        [ObservableProperty] private bool _isDescriptionScrollable;

        [ObservableProperty] private bool _isEpisodeThumbnailVisible = true;

        [ObservableProperty] private bool _isEpisodesListVisible = true;

        [ObservableProperty] private bool _isInitialLoadComplete;

        [ObservableProperty] private bool _isMarkWatchedButtonVisible = true;

        [ObservableProperty] private bool _isPlayButtonVisible = true;

        [ObservableProperty] private bool _isPlayFromBeginningButtonVisible;

        [ObservableProperty] private bool _isProgressVisible;

        [ObservableProperty] private bool _isResolutionSeparatorVisible;

        [ObservableProperty] private bool _isResolutionVisible;

        [ObservableProperty] private bool _isResumeButtonVisible;

        [ObservableProperty] private bool _isRuntimeSeparatorVisible;

        [ObservableProperty] private bool _isRuntimeVisible;

        [ObservableProperty] private bool _isSeriesNameVisible = true;

        [ObservableProperty] private bool _isSeriesPosterVisible;

        [ObservableProperty] private bool _isShuffleButtonVisible;

        // State tracking
        private CancellationTokenSource _loadingCts;

        [ObservableProperty] private string _markWatchedText = "Mark Watched";

        // Navigation context for smart back navigation

        [ObservableProperty] private string _playButtonText = "Play";

        [ObservableProperty] private string _resolution;

        [ObservableProperty] private string _runtime;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _seasons = new();

        [ObservableProperty] private BaseItemDto _selectedEpisode;

        [ObservableProperty] private int _selectedEpisodeIndex = -1;

        [ObservableProperty] private int _selectedSeasonIndex = -1;

        // Observable properties
        [ObservableProperty] private BaseItemDto _series;

        [ObservableProperty] private string _seriesName;

        [ObservableProperty] private ImageSource _seriesPoster;

        [ObservableProperty] private double _watchProgressPercentage;

        public SeasonDetailsViewModel(
            ILogger<SeasonDetailsViewModel> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IMediaPlaybackService mediaPlaybackService,
            IUserDataService userDataService,
            IEpisodeQueueService episodeQueueService,
            IPreferencesService preferencesService) : base(
            logger,
            apiClient,
            userProfileService,
            navigationService,
            imageLoadingService,
            mediaPlaybackService,
            userDataService)
        {
            _episodeQueueService = episodeQueueService ?? throw new ArgumentNullException(nameof(episodeQueueService));
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
        }

        public bool ShouldNavigateToOriginalSource => NavigationSourcePageForBack != null;
        public Type NavigationSourcePageForBack { get; private set; }

        public object NavigationSourceParameterForBack { get; private set; }

        public void ClearNavigationContext()
        {
            NavigationSourcePageForBack = null;
            NavigationSourceParameterForBack = null;
        }

        public override async Task ToggleFavoriteAsync(bool isFavorite)
        {
            // For TV series, we need to use the Series object instead of CurrentItem
            if (Series?.Id == null)
            {
                Logger?.LogWarning("Cannot toggle favorite - missing series ID");
                return;
            }

            // Temporarily set CurrentItem to Series so base method works
            var originalCurrentItem = CurrentItem;
            CurrentItem = Series;

            try
            {
                await base.ToggleFavoriteAsync(isFavorite);

                // Update Series UserData
                if (Series.UserData != null && CurrentItem.UserData != null)
                {
                    Series.UserData.IsFavorite = CurrentItem.UserData.IsFavorite;
                }
            }
            finally
            {
                CurrentItem = originalCurrentItem;
            }
        }

        /// <summary>
        ///     Clear all state data
        /// </summary>
        public void ClearState()
        {
            Logger?.LogInformation("Clearing SeasonDetailsViewModel state"); Seasons.Clear();
            Episodes.Clear();

            // Clear current items
            Series = null;
            CurrentSeason = null;

            // Reset initial load flag
            IsInitialLoadComplete = false;
            SelectedEpisode = null; SeriesName = string.Empty;
            EpisodeTitle = string.Empty;
            EpisodeNumber = string.Empty;
            EpisodeOverview = string.Empty;
            AirDate = string.Empty;
            Runtime = string.Empty;
            Resolution = string.Empty;
            PlayButtonText = "Play";
            CanResume = false;
            BackdropImage = null;
            EpisodeThumbnail = null;
            SeriesPoster = null; SelectedSeasonIndex = -1;
            SelectedEpisodeIndex = -1;

            // Reset visibility flags
            IsEpisodesListVisible = true;
            IsSeriesPosterVisible = false;
            IsEpisodeThumbnailVisible = true;
            IsSeriesNameVisible = true;
            IsAirDateVisible = false;
            IsRuntimeVisible = false;
            IsResolutionVisible = false;
            IsAirDateSeparatorVisible = false;
            IsRuntimeSeparatorVisible = false;
            IsResolutionSeparatorVisible = false;
            IsPlayButtonVisible = true;
            IsResumeButtonVisible = false;
            IsPlayFromBeginningButtonVisible = false;
            IsShuffleButtonVisible = false;
            IsMarkWatchedButtonVisible = true;
            IsProgressVisible = false;
            WatchProgressPercentage = 0;
            MarkWatchedText = "Mark Watched";
            IsDescriptionScrollable = false;

            // Cancel any ongoing operations
            _loadingCts?.Cancel();

            // Clear navigation context
            NavigationSourcePageForBack = null;
            NavigationSourceParameterForBack = null;

            Logger?.LogInformation("SeasonDetailsViewModel state cleared");
        }

        /// <summary>
        ///     Initialize the ViewModel with navigation parameter
        /// </summary>
        public override async Task InitializeAsync(object parameter)
        {
            if (parameter == null)
            {
                Logger?.LogWarning("SeasonDetailsViewModel: Navigation parameter is null");
                NavigationService.GoBack();
                return;
            }

            // Clear previous state
            ClearState();

            BaseItemDto targetEpisode = null;

            var context = CreateErrorContext("InitializeSeason");
            try
            {
                IsLoading = true;
                _loadingCts?.Cancel();
                _loadingCts = new CancellationTokenSource();

                try
                {
                    if (parameter is EpisodeNavigationParameter episodeNavParam)
                    {
                        Logger?.LogInformation(
                            $"SeasonDetailsViewModel.InitializeAsync - Received EpisodeNavigationParameter for: {episodeNavParam.Episode?.Name}");
                        Logger?.LogInformation(
                            $"SeasonDetailsViewModel.InitializeAsync - FromEpisodesButton: {episodeNavParam.FromEpisodesButton}");
                        Logger?.LogInformation(
                            $"SeasonDetailsViewModel.InitializeAsync - OriginalSourcePage: {episodeNavParam.OriginalSourcePage?.Name}");

                        // Store the navigation context for smart back navigation
                        if (episodeNavParam.FromEpisodesButton)
                        {
                            NavigationSourcePageForBack = episodeNavParam.OriginalSourcePage;
                            NavigationSourceParameterForBack = episodeNavParam.OriginalSourceParameter;
                        }

                        await HandleItemNavigationAsync(episodeNavParam.Episode, targetEpisode);
                    }
                    else if (parameter is MediaPlaybackParams playbackParams)
                    {
                        // Handle back navigation from MediaPlayerPage
                        Logger?.LogInformation(
                            $"SeasonDetailsViewModel.InitializeAsync - Received MediaPlaybackParams, extracting NavigationSourceParameter");

                        // Use the NavigationSourceParameter which contains the original season/series data
                        if (playbackParams.NavigationSourceParameter != null)
                        {
                            await InitializeAsync(playbackParams.NavigationSourceParameter);
                        }
                        else
                        {
                            Logger?.LogWarning("MediaPlaybackParams.NavigationSourceParameter is null, cannot restore state");
                            NavigationService.GoBack();
                        }
                    }
                    else if (parameter is BaseItemDto item)
                    {
                        Logger?.LogInformation(
                            $"SeasonDetailsViewModel.InitializeAsync - Received BaseItemDto: {item.Name} (Type: {item.Type}, Id: {item.Id})");
                        await HandleItemNavigationAsync(item, targetEpisode);
                    }
                    else if (parameter is string itemId && Guid.TryParse(itemId, out var itemGuid))
                    {
                        var loadedItem = await ApiClient.Items[itemGuid].GetAsync(config =>
                        {
                            config.QueryParameters.UserId = UserIdGuid.Value;
                        }, _loadingCts.Token);

                        if (loadedItem != null)
                        {
                            await HandleItemNavigationAsync(loadedItem, targetEpisode);
                        }
                    }
                    else
                    {
                        Logger?.LogWarning(
                            $"SeasonDetailsViewModel: Unexpected parameter type: {parameter?.GetType()?.Name ?? "null"}");
                        NavigationService.GoBack();
                    }
                }
                finally
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        IsLoading = false;
                    });
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        private async Task HandleItemNavigationAsync(BaseItemDto item, BaseItemDto targetEpisode)
        {
            Logger?.LogInformation($"HandleItemNavigationAsync called with item type: {item.Type}, Name: {item.Name}");

            if (item.Type == BaseItemDto_Type.Season)
            {
                CurrentSeason = item;
                await LoadSeasonDataAsync();
            }
            else if (item.Type == BaseItemDto_Type.Episode)
            {
                targetEpisode = item;
                Logger?.LogInformation($"Episode navigation - SeasonId: {item.SeasonId}, SeriesId: {item.SeriesId}");

                if (item.SeasonId.HasValue)
                {
                    Logger?.LogInformation($"Loading season data for season ID: {item.SeasonId.Value}");
                    var response = await ApiClient.Items[item.SeasonId.Value].GetAsync(config =>
                    {
                        config.QueryParameters.UserId = UserIdGuid.Value;
                    }, _loadingCts.Token);

                    CurrentSeason = response;
                    if (CurrentSeason != null)
                    {
                        Logger?.LogInformation($"Season loaded: {CurrentSeason.Name}");
                        await LoadSeasonDataAsync();

                        // Small delay to ensure UI is ready, especially for shows with many seasons
                        await Task.Delay(100);

                        await SelectSpecificEpisodeAsync(targetEpisode);

                        // Ensure UI is properly updated on UI thread
                        await RunOnUIThreadAsync(() =>
                        {
                            OnPropertyChanged(nameof(IsEpisodesListVisible));
                            OnPropertyChanged(nameof(IsSeriesPosterVisible));
                            OnPropertyChanged(nameof(IsEpisodeThumbnailVisible));
                            OnPropertyChanged(nameof(IsSeriesNameVisible));
                        });
                    }
                    else
                    {
                        Logger?.LogError("Failed to load season data");
                    }
                }
                else
                {
                    Logger?.LogWarning("Episode has no SeasonId");
                }
            }
            else if (item.Type == BaseItemDto_Type.Series)
            {
                Series = item;
                CurrentSeason = null;
                SelectedEpisode = null;
                Seasons.Clear();
                Episodes.Clear();

                // Check if this is a grouped recently added item
                if (!string.IsNullOrEmpty(item.Overview) && item.Overview.StartsWith("__RecentlyAddedGrouped__"))
                {
                    Logger?.LogInformation($"Detected grouped recently added item for series: {item.Name}");
                    if (item.Id.HasValue)
                    {
                        var response = await ApiClient.Items[item.Id.Value].GetAsync(config =>
                        {
                            config.QueryParameters.UserId = UserIdGuid.Value;
                        }, _loadingCts.Token);

                        if (response != null)
                        {
                            Series = response;
                            Logger?.LogInformation($"Loaded full series data for: {Series.Name}");
                        }
                    }
                }

                await LoadSeriesFirstSeasonAsync();
            }
        }

        private async Task LoadSeasonDataAsync()
        {
            var context = CreateErrorContext("LoadSeasonData");
            try
            {
                Logger?.LogInformation($"LoadSeasonDataAsync started - CurrentSeason: {CurrentSeason?.Name}");
                IsLoading = true;
                try
                {
                    if (CurrentSeason?.SeriesId.HasValue == true)
                    {
                        var seriesId = CurrentSeason.SeriesId.Value;
                        Logger?.LogInformation($"Loading series data for ID: {seriesId}");

                        var response = await ApiClient.Items[seriesId].GetAsync(config =>
                        {
                            config.QueryParameters.UserId = UserIdGuid.Value;
                        }, _loadingCts.Token);

                        Series = response;
                        if (Series != null)
                        {
                            SeriesName = Series.Name;
                            Logger?.LogInformation($"Series loaded: {SeriesName}");
                            await LoadBackdropImageAsync(Series);
                        }
                        else
                        {
                            Logger?.LogWarning("Failed to load series data");
                        }

                        await LoadSeasonsAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        Logger?.LogWarning("CurrentSeason is null or has no SeriesId");
                    }

                    await LoadEpisodesAsync();

                    Logger?.LogInformation($"Episodes loaded: {Episodes.Count} episodes");
                    Logger?.LogInformation(
                        $"Current visibility states - EpisodesList: {IsEpisodesListVisible}, SeriesPoster: {IsSeriesPosterVisible}, EpisodeThumbnail: {IsEpisodeThumbnailVisible}");

                    if (Episodes.Any())
                    {
                        SelectedEpisodeIndex = 0;
                        var firstEpisode = Episodes.FirstOrDefault();
                        if (firstEpisode != null)
                        {
                            await SelectEpisodeAsync(firstEpisode);
                            Logger?.LogInformation($"Selected first episode: {firstEpisode.Name}");
                        }
                    }
                    else
                    {
                        Logger?.LogWarning("No episodes found after loading");
                    }
                }
                finally
                {
                    Logger?.LogInformation("LoadSeasonDataAsync completed - Setting IsLoading = false");
                    // Ensure UI property updates happen on UI thread
                    await RunOnUIThreadAsync(async () =>
                    {
                        // Small delay to ensure UI updates properly
                        await Task.Delay(50);
                        IsLoading = false;
                    });
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        private async Task LoadSeasonsAsync()
        {
            var context = CreateErrorContext("LoadSeasons");
            try
            {
                if (Series?.Id == null)
                {
                    Logger?.LogWarning("LoadSeasonsAsync: Series or Series.Id is null");
                    return;
                }

                Logger?.LogInformation($"LoadSeasonsAsync: Loading seasons for series {Series.Name}");

                var seasonsResult = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.ParentId = Series.Id.Value;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Season };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.ItemCounts, ItemFields.PrimaryImageAspectRatio, ItemFields.Overview
                    };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.IndexNumber };
                }, _loadingCts.Token);

                if (seasonsResult?.Items != null)
                {
                    var orderedSeasons = seasonsResult.Items.OrderBy(s => s.IndexNumber ?? int.MaxValue).ToList();
                    
                    // Ensure UI updates happen on UI thread
                    await RunOnUIThreadAsync(() =>
                    {
                        Seasons.Clear();
                        foreach (var season in orderedSeasons)
                        {
                            Seasons.Add(season);
                        }
                    });

                    Logger?.LogInformation($"LoadSeasonsAsync: Loaded {Seasons.Count} seasons");

                    var currentSeasonIndex = Seasons.ToList().FindIndex(s => s.Id == CurrentSeason?.Id);
                    if (currentSeasonIndex >= 0)
                    {
                        SelectedSeasonIndex = currentSeasonIndex;
                        Logger?.LogInformation($"LoadSeasonsAsync: Selected season index {currentSeasonIndex}");
                    }
                    else
                    {
                        Logger?.LogWarning("LoadSeasonsAsync: Current season not found in seasons list");
                    }
                }
                else
                {
                    Logger?.LogWarning("LoadSeasonsAsync: No seasons returned from API");
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        private async Task LoadEpisodesAsync()
        {
            var context = CreateErrorContext("LoadEpisodes");
            try
            {
                if (CurrentSeason?.Id == null)
                {
                    return;
                }

                IsEpisodesListVisible = true;
                IsSeriesPosterVisible = false;
                IsEpisodeThumbnailVisible = true;

                var episodesResult = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.ParentId = CurrentSeason.Id.Value;
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Overview, ItemFields.PrimaryImageAspectRatio, ItemFields.MediaStreams
                    };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.IndexNumber };
                }, _loadingCts.Token);

                if (episodesResult?.Items != null)
                {
                    // Ensure UI updates happen on UI thread
                    await RunOnUIThreadAsync(() =>
                    {
                        Episodes.Clear();
                        foreach (var episode in episodesResult.Items)
                        {
                            Episodes.Add(episode);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        private async Task SelectEpisodeAsync(BaseItemDto episode, bool setFocus = false)
        {
            if (episode == null)
            {
                return;
            }

            Logger?.LogInformation($"SelectEpisodeAsync called for episode: {episode.Name}");
            SelectedEpisode = episode;

            EpisodeTitle = episode.Name;
            EpisodeNumber = $"Episode {episode.IndexNumber}";
            EpisodeOverview = episode.Overview ?? "No overview available.";

            // Handle air date
            if (episode.PremiereDate.HasValue)
            {
                AirDate = episode.PremiereDate.Value.ToString("MMM d, yyyy");
                IsAirDateVisible = true;
                IsAirDateSeparatorVisible = true;
            }
            else
            {
                IsAirDateVisible = false;
                IsAirDateSeparatorVisible = false;
            }

            // Handle runtime
            if (episode.RunTimeTicks.HasValue)
            {
                Runtime = $"{(int)TimeSpan.FromTicks(episode.RunTimeTicks.Value).TotalMinutes} min";
                IsRuntimeVisible = true;
                IsRuntimeSeparatorVisible = episode.PremiereDate.HasValue;
            }
            else
            {
                IsRuntimeVisible = false;
                IsRuntimeSeparatorVisible = false;
            }

            // Handle resolution
            if (episode.MediaStreams?.Any(ms => ms.Type == MediaStream_Type.Video) == true)
            {
                var videoStream = episode.MediaStreams.FirstOrDefault(ms => ms.Type == MediaStream_Type.Video);
                if (videoStream?.Height != null)
                {
                    Resolution = GetResolutionText(videoStream.Height.Value);
                    IsResolutionVisible = true;
                    IsResolutionSeparatorVisible = IsAirDateVisible || IsRuntimeVisible;
                }
                else
                {
                    IsResolutionVisible = false;
                    IsResolutionSeparatorVisible = false;
                }
            }
            else
            {
                IsResolutionVisible = false;
                IsResolutionSeparatorVisible = false;
            }
            await LoadEpisodeThumbnailAsync(episode);

            UpdateButtonStates(episode);
            PlayButtonText = "Play";
        }

        private void UpdateButtonStates(BaseItemDto episode)
        {
            var userData = episode.UserData;

            // Hide shuffle button for individual episodes
            IsShuffleButtonVisible = false;
            IsMarkWatchedButtonVisible = true;

            if (userData?.PlayedPercentage > 0 &&
                userData.PlayedPercentage < MediaConstants.WATCHED_PERCENTAGE_THRESHOLD)
            {
                IsPlayButtonVisible = false;
                IsResumeButtonVisible = true;
                IsPlayFromBeginningButtonVisible = true;

                // Update progress bar
                WatchProgressPercentage = userData.PlayedPercentage ?? 0;
                IsProgressVisible = true;
            }
            else
            {
                IsPlayButtonVisible = true;
                IsResumeButtonVisible = false;
                IsPlayFromBeginningButtonVisible = false;
                IsProgressVisible = false;
            }

            MarkWatchedText = userData?.Played == true ? "Mark Unwatched" : "Mark Watched";
        }

        private string GetResolutionText(int height)
        {
            if (height >= 2160)
            {
                return "4K";
            }

            if (height >= 1080)
            {
                return "1080p";
            }

            if (height >= 720)
            {
                return "720p";
            }

            if (height >= 480)
            {
                return "480p";
            }

            return $"{height}p";
        }

        private async Task LoadBackdropImageAsync(BaseItemDto item)
        {
            if (item == null)
            {
                return;
            }

            await ImageLoadingService.LoadImageIntoTargetAsync(
                item,
                "Backdrop",
                imageSource => BackdropImage = imageSource,
                null, // Dispatcher handled internally by service
                1920,
                1080
            );
        }

        private async Task LoadEpisodeThumbnailAsync(BaseItemDto episode)
        {
            var context = CreateErrorContext("LoadEpisodeThumbnail", ErrorCategory.Media);
            try
            {
                var imageSource = await ImageHelper.GetImageSourceAsync(episode, "Primary", 400, 225)
                    .ConfigureAwait(false);
                await RunOnUIThreadAsync(() => EpisodeThumbnail = imageSource);
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        private async Task LoadSeriesPosterAsync(BaseItemDto series)
        {
            var context = CreateErrorContext("LoadSeriesPoster", ErrorCategory.Media);
            try
            {
                var imageSource = await ImageHelper.GetImageSourceAsync(series, "Primary", 600, 900)
                    .ConfigureAwait(false);
                await RunOnUIThreadAsync(() => SeriesPoster = imageSource);
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        private async Task LoadSeriesFirstSeasonAsync()
        {
            var context = CreateErrorContext("LoadSeriesFirstSeason");
            try
            {
                IsLoading = true;
                try
                {
                    if (Series != null)
                    {
                        await RunOnUIThreadAsync(() => SeriesName = Series.Name);
                        await LoadBackdropImageAsync(Series);
                        await LoadSeasonsAsync().ConfigureAwait(false);

                        await RunOnUIThreadAsync(() => SelectedSeasonIndex = -1);
                        await ShowSeriesOverviewAsync();
                    }
                }
                finally
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        IsLoading = false;
                    });
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        private async Task ShowSeriesOverviewAsync()
        {
            var context = CreateErrorContext("ShowSeriesOverview", ErrorCategory.User);
            try
            {
                // Ensure UI properties are updated on UI thread
                await RunOnUIThreadAsync(() =>
                {
                    SelectedSeasonIndex = -1;

                    IsEpisodesListVisible = false;
                    IsSeriesPosterVisible = true;
                    IsSeriesNameVisible = false; // Hide to avoid duplication
                    IsEpisodeThumbnailVisible = false;
                });

                await LoadSeriesPosterAsync(Series);

                await RunOnUIThreadAsync(() =>
                {
                    EpisodeTitle = Series.Name;

                    // Show premiered year instead of episode number
                    if (Series.PremiereDate.HasValue)
                    {
                        EpisodeNumber = $"Premiered: {Series.PremiereDate.Value.Year}";
                    }
                    else
                    {
                        EpisodeNumber = "";
                    }

                    EpisodeOverview = Series.Overview ?? "No overview available.";

                    // Hide air date for series overview
                    IsAirDateVisible = false;
                    IsAirDateSeparatorVisible = false;

                    // Show genres in runtime field
                    if (Series.Genres?.Any() == true)
                    {
                        Runtime = string.Join(", ", Series.Genres);
                        IsRuntimeVisible = true;
                        IsRuntimeSeparatorVisible = false;
                    }
                    else
                    {
                        IsRuntimeVisible = false;
                        IsRuntimeSeparatorVisible = false;
                    }

                    IsProgressVisible = false;
                    IsResumeButtonVisible = false;
                    IsPlayButtonVisible = true;
                    PlayButtonText = "Play";
                    IsShuffleButtonVisible = true;
                    IsMarkWatchedButtonVisible = true;
                    MarkWatchedText = Series?.UserData?.Played == true ? "Mark Series Unwatched" : "Mark Series Watched";

                    // Update favorite button
                    IsFavorite = Series?.UserData?.IsFavorite ?? false;
                    FavoriteButtonText = IsFavorite ? "Unfavorite" : "Favorite";
                });
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        private async Task SelectSpecificEpisodeAsync(BaseItemDto targetEpisode)
        {
            var context = CreateErrorContext("SelectSpecificEpisode", ErrorCategory.User);
            try
            {
                Logger?.LogInformation(
                    $"SelectSpecificEpisodeAsync - targetEpisode: {targetEpisode?.Name} (Id: {targetEpisode?.Id})");
                Logger?.LogInformation($"SelectSpecificEpisodeAsync - Episodes count: {Episodes?.Count ?? 0}");

                if (targetEpisode == null || !Episodes.Any())
                {
                    return;
                }

                var episodeIndex = Episodes.ToList().FindIndex(ep => ep.Id == targetEpisode.Id);
                Logger?.LogInformation($"SelectSpecificEpisodeAsync - Found episode at index: {episodeIndex}");

                if (episodeIndex >= 0)
                {
                    SelectedEpisodeIndex = episodeIndex;
                    await SelectEpisodeAsync(Episodes[episodeIndex], true);

                    // Force property change notification to ensure UI updates
                    OnPropertyChanged(nameof(SelectedEpisodeIndex));
                    Logger?.LogInformation($"SelectSpecificEpisodeAsync - Set SelectedEpisodeIndex to: {episodeIndex}");

                    // Signal that initial episode selection is complete
                    IsInitialLoadComplete = true;
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        public async Task RefreshEpisodesWatchedStatusAsync()
        {
            var context = CreateErrorContext("RefreshEpisodesWatchedStatus");
            try
            {
                if (CurrentSeason?.Id == null)
                {
                    return;
                }

                Logger?.LogInformation($"Refreshing episodes watched status for season {CurrentSeason.Name}");

                var episodesResult = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.ParentId = CurrentSeason.Id.Value;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.Episode };
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.ItemCounts, ItemFields.PrimaryImageAspectRatio, ItemFields.Overview,
                        ItemFields.MediaStreams
                    };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.IndexNumber };
                }, _loadingCts.Token);

                if (episodesResult?.Items != null)
                {
                    Episodes.Clear();
                    foreach (var episode in episodesResult.Items)
                    {
                        Episodes.Add(episode);
                    }

                    Logger?.LogInformation($"Refreshed {Episodes.Count} episodes with updated watched status");
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }


        private async Task SelectNextUnwatchedEpisodeAsync()
        {
            var context = CreateErrorContext("SelectNextUnwatchedEpisode", ErrorCategory.User);
            try
            {
                if (!Episodes.Any())
                {
                    return;
                }

                // Find current episode index
                var currentIndex = -1;
                for (var i = 0; i < Episodes.Count; i++)
                {
                    if (Episodes[i].Id == SelectedEpisode?.Id)
                    {
                        currentIndex = i;
                        break;
                    }
                }

                if (currentIndex == -1)
                {
                    return;
                }

                // Look for next unwatched episode
                for (var i = currentIndex + 1; i < Episodes.Count; i++)
                {
                    var episode = Episodes[i];
                    if (episode.UserData?.Played != true)
                    {
                        SelectedEpisodeIndex = i;
                        await SelectEpisodeAsync(episode, true);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        // Commands
        [RelayCommand]
        private async Task SelectSeasonAsync(BaseItemDto season)
        {
            if (season != null && (CurrentSeason == null || season.Id != CurrentSeason.Id))
            {
                CurrentSeason = season;

                // Show series name when viewing a season
                IsSeriesNameVisible = true;

                // Update selected index
                var index = Seasons.ToList().FindIndex(s => s.Id == season.Id);
                if (index >= 0)
                {
                    SelectedSeasonIndex = index;
                }

                SelectedEpisode = null;
                await LoadEpisodesAsync();

                if (Episodes.Any())
                {
                    SelectedEpisodeIndex = 0;
                    var firstEpisode = Episodes.FirstOrDefault();
                    if (firstEpisode != null)
                    {
                        await SelectEpisodeAsync(firstEpisode);
                    }
                }
            }
        }

        [RelayCommand]
        public async Task SelectEpisodeCommand(BaseItemDto episode)
        {
            if (episode != null)
            {
                await SelectEpisodeAsync(episode, true);
            }
        }

        [RelayCommand]
        public override async Task PlayAsync()
        {
            if (SelectedEpisode?.Id != null)
            {
                await PlayEpisodeAsync(SelectedEpisode);
            }
            else if (CurrentSeason == null && Series != null && Seasons.Any())
            {
                await PlayFirstUnwatchedEpisodeAsync();
            }
        }

        [RelayCommand]
        public override async Task ResumeAsync()
        {
            if (SelectedEpisode?.Id != null)
            {
                await PlayEpisodeAsync(SelectedEpisode, true);
            }
        }

        [RelayCommand]
        private async Task PlayFromBeginningAsync()
        {
            if (SelectedEpisode?.Id != null)
            {
                await PlayEpisodeAsync(SelectedEpisode, fromBeginning: true);
            }
        }

        [RelayCommand]
        private async Task ToggleWatchedAsync()
        {
            var context = CreateErrorContext("ToggleWatched", ErrorCategory.User);
            try
            {
                if (SelectedEpisode == null && Series != null)
                {
                    // Toggle series watched status
                    var isWatched = Series.UserData?.Played ?? false;
                    var newWatchedStatus = !isWatched;

                    var updatedData =
                        await UserDataService.ToggleWatchedAsync(Series.Id.Value, newWatchedStatus, UserIdGuid);

                    if (updatedData != null)
                    {
                        if (Series.UserData == null)
                        {
                            Series.UserData = new UserItemDataDto();
                        }

                        Series.UserData.Played = updatedData.Played;
                    }

                    MarkWatchedText = newWatchedStatus ? "Mark Series Unwatched" : "Mark Series Watched";
                }
                else if (SelectedEpisode?.Id != null)
                {
                    // Toggle episode watched status
                    var isWatched = SelectedEpisode.UserData?.Played ?? false;
                    var newWatchedStatus = !isWatched;

                    var updatedData =
                        await UserDataService.ToggleWatchedAsync(SelectedEpisode.Id.Value, newWatchedStatus,
                            UserIdGuid);

                    if (updatedData != null)
                    {
                        if (SelectedEpisode.UserData == null)
                        {
                            SelectedEpisode.UserData = new UserItemDataDto();
                        }

                        SelectedEpisode.UserData.Played = updatedData.Played;
                        SelectedEpisode.UserData.PlayedPercentage = newWatchedStatus ? 100 : 0;
                    }

                    UpdateButtonStates(SelectedEpisode);

                    // Find the episode in the list and trigger update
                    var episodeIndex = Episodes.IndexOf(SelectedEpisode);
                    if (episodeIndex >= 0)
                    {
                        // Store current selection before update
                        var currentSelectedIndex = SelectedEpisodeIndex;

                        // Replace the item in the collection to trigger UI update
                        Episodes[episodeIndex] = SelectedEpisode;

                        // Restore selection after update to prevent focus loss
                        if (currentSelectedIndex >= 0 && currentSelectedIndex < Episodes.Count)
                        {
                            SelectedEpisodeIndex = currentSelectedIndex;
                            Logger?.LogInformation($"Restored SelectedEpisodeIndex to {currentSelectedIndex} after episode update");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        [RelayCommand]
        private async Task ShuffleAsync()
        {
            var context = CreateErrorContext("Shuffle", ErrorCategory.User);
            try
            {
                Logger?.LogInformation($"Shuffling all episodes for series: {Series?.Name}");

                if (Series?.Id == null)
                {
                    Logger?.LogWarning("Cannot shuffle: no series ID");
                    return;
                }

                // Use the service to build a shuffled queue
                var (shuffledQueue, startIndex) =
                    await _episodeQueueService.BuildShuffledSeriesQueueAsync(Series.Id.Value);

                if (shuffledQueue != null && shuffledQueue.Any())
                {
                    // Get subtitle preferences
                    var preferences = await _preferencesService?.GetAppPreferencesAsync();

                    // Create playback params
                    if (shuffledQueue.Count == 0)
                    {
                        Logger?.LogError("Shuffled queue is empty");
                        return;
                    }
                    var playbackParams = new MediaPlaybackParams
                    {
                        ItemId = shuffledQueue[0].Id.ToString(),
                        QueueItems = shuffledQueue,
                        StartIndex = 0,
                        IsShuffled = true,
                        NavigationSourcePage = typeof(SeasonDetailsPage),
                        NavigationSourceParameter = CurrentSeason ?? Series,
                        SubtitleStreamIndex = preferences?.DefaultSubtitleStreamIndex ?? -1
                    };

                    Logger?.LogInformation($"Starting shuffled playback with {shuffledQueue.Count} episodes");
                    NavigationService?.Navigate(typeof(MediaPlayerPage), playbackParams);
                }
                else
                {
                    Logger?.LogWarning("No episodes found to shuffle");
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        [RelayCommand]
        private async Task NavigateToPreviousSeasonAsync()
        {
            var currentIndex = GetCurrentSeasonIndex();
            if (currentIndex > 0)
            {
                var previousSeason = Seasons[currentIndex - 1];
                await SelectSeasonAsync(previousSeason);
            }
            else if (currentIndex == 0)
            {
                CurrentSeason = null;
                await ShowSeriesOverviewAsync();
            }
        }

        [RelayCommand]
        private async Task NavigateToNextSeasonAsync()
        {
            var currentIndex = GetCurrentSeasonIndex();
            if (currentIndex == -1 && Seasons.Any())
            {
                var firstSeason = Seasons.FirstOrDefault();
                if (firstSeason != null)
                {
                    await SelectSeasonAsync(firstSeason);
                }
            }
            else if (currentIndex >= 0 && currentIndex < Seasons.Count - 1)
            {
                var nextSeason = Seasons[currentIndex + 1];
                await SelectSeasonAsync(nextSeason);
            }
        }

        private int GetCurrentSeasonIndex()
        {
            if (CurrentSeason == null)
            {
                return -1;
            }

            for (var i = 0; i < Seasons.Count; i++)
            {
                if (Seasons[i].Id == CurrentSeason.Id)
                {
                    return i;
                }
            }

            return -1;
        }

        private async Task PlayEpisodeAsync(BaseItemDto episode, bool resume = false, bool fromBeginning = false)
        {
            // Build episode queue for continuous playback
            List<BaseItemDto> episodeQueue = null;
            var startIndex = 0;

            var queueContext = CreateErrorContext("BuildEpisodeQueue");
            try
            {
                var (queue, index) = await _episodeQueueService.BuildEpisodeQueueAsync(episode);
                startIndex = index;
                episodeQueue = queue;
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    queueContext.Source = queueContext.Source ?? GetType().Name;
                    episodeQueue = await ErrorHandler.HandleErrorAsync<List<BaseItemDto>>(ex, queueContext, null);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{queueContext?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                    episodeQueue = null;
                }
            }

            if (episodeQueue == null)
            {
                Logger?.LogWarning("Failed to build episode queue, proceeding with single episode playback");
            }

            // Get subtitle preferences
            var preferences = await _preferencesService?.GetAppPreferencesAsync();

            // Create playback params
            // Determine the best navigation parameter to return to the correct view
            object navigationParameter = CurrentSeason;

            // If we're in series overview mode and playing an episode, use the episode itself
            // so we can navigate back to its season
            if (CurrentSeason == null && episode.SeasonId.HasValue)
            {
                navigationParameter = episode;
            }

            var playbackParams = new MediaPlaybackParams
            {
                ItemId = episode.Id.Value.ToString(),
                StartPositionTicks = resume ? episode.UserData?.PlaybackPositionTicks : fromBeginning ? 0 : null,
                QueueItems = episodeQueue,
                StartIndex = startIndex,
                NavigationSourcePage = typeof(SeasonDetailsPage),
                NavigationSourceParameter = navigationParameter,
                SubtitleStreamIndex = preferences?.DefaultSubtitleStreamIndex ?? -1
            };

            NavigationService?.Navigate(typeof(MediaPlayerPage), playbackParams);
        }

        private async Task PlayFirstUnwatchedEpisodeAsync()
        {
            BaseItemDto firstUnwatchedEpisode = null;
            var originalSeason = CurrentSeason;

            foreach (var season in Seasons)
            {
                if (season.SeriesId != Series.Id)
                {
                    Logger?.LogWarning($"Skipping season {season.Name} as it belongs to a different series");
                    continue;
                }

                // Load episodes for this season
                var episodesResult = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.ParentId = season.Id.Value;
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Overview, ItemFields.PrimaryImageAspectRatio, ItemFields.MediaStreams
                    };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.IndexNumber };
                }, CancellationToken.None);

                if (episodesResult?.Items?.Any() == true)
                {
                    // Find first unwatched episode
                    firstUnwatchedEpisode = episodesResult.Items.FirstOrDefault(ep =>
                        ep.UserData?.Played != true ||
                        (ep.UserData?.PlaybackPositionTicks > 0 && ep.UserData?.PlayedPercentage < 90));

                    if (firstUnwatchedEpisode != null)
                    {
                        // Update UI to show this episode
                        CurrentSeason = season;
                        Episodes.Clear();
                        foreach (var ep in episodesResult.Items)
                        {
                            Episodes.Add(ep);
                        }

                        var seasonIndex = Seasons.ToList().IndexOf(season);
                        if (seasonIndex >= 0)
                        {
                            SelectedSeasonIndex = seasonIndex;
                        }

                        await SelectEpisodeAsync(firstUnwatchedEpisode);

                        var episodeIndex = episodesResult.Items.ToList()
                            .FindIndex(ep => ep.Id == firstUnwatchedEpisode.Id);
                        if (episodeIndex >= 0)
                        {
                            SelectedEpisodeIndex = episodeIndex;
                        }

                        Logger?.LogInformation(
                            $"Found unwatched episode: {firstUnwatchedEpisode.Name} in season {season.Name}");
                        break;
                    }
                }
            }

            // If no unwatched episode found, use first episode of first season
            if (firstUnwatchedEpisode == null)
            {
                var firstSeasonOfSeries = Seasons.FirstOrDefault(s => s.SeriesId == Series.Id);
                if (firstSeasonOfSeries != null)
                {
                    await SelectSeasonAsync(firstSeasonOfSeries);
                    firstUnwatchedEpisode = Episodes?.FirstOrDefault();
                }
                else
                {
                    Logger?.LogError($"No seasons found for series {Series.Name}");
                    CurrentSeason = originalSeason;
                }
            }

            if (firstUnwatchedEpisode != null)
            {
                // Ensure we're showing the episode list UI
                IsEpisodesListVisible = true;
                IsSeriesPosterVisible = false;
                IsSeriesNameVisible = true;

                await PlayEpisodeAsync(firstUnwatchedEpisode);
            }
        }

        protected override void DisposeManaged()
        {
            _loadingCts?.Cancel();
            _loadingCts?.Dispose();
            base.DisposeManaged();
        }

        /// <summary>
        ///     Implementation of abstract method from DetailsViewModel
        /// </summary>
        protected override async Task LoadAdditionalDataAsync()
        {
            // Season-specific loading is handled in LoadSeasonDataAsync
            await Task.CompletedTask;
        }
    }
}
