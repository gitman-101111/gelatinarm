using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Player;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Details
{
    public partial class SeasonDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        private readonly IEpisodeQueueService _episodeQueueService;

        [ObservableProperty] private string _airDate;

        [ObservableProperty] private BaseItemDto _currentSeason;

        [ObservableProperty] private string _episodeNumber;

        [ObservableProperty] private string _episodeOverview;

        [ObservableProperty] private ImageSource _episodeThumbnail;

        [ObservableProperty] private string _episodeTitle;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _episodes = new();

        [ObservableProperty] private bool _isAirDateSeparatorVisible;

        [ObservableProperty] private bool _isInitialLoadComplete;

        [ObservableProperty] private bool _isResolutionSeparatorVisible;

        // The series' own view (poster, overview, Shuffle) rather than a season's episodes
        [ObservableProperty] private bool _isSeriesOverview;

        [ObservableProperty] private bool _isRuntimeSeparatorVisible;

        [ObservableProperty] private string _markWatchedText = WatchedButtonLabel(false);

        [ObservableProperty] private string _resolution;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _seasons = new();

        [ObservableProperty] private BaseItemDto _selectedEpisode;

        [ObservableProperty] private int _selectedEpisodeIndex = -1;

        [ObservableProperty] private int _selectedSeasonIndex = -1;

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
            IUserDataService userDataService,
            IEpisodeQueueService episodeQueueService) : base(
            logger,
            apiClient,
            userProfileService,
            navigationService,
            imageLoadingService,
            userDataService)
        {
            _episodeQueueService = episodeQueueService;
        }

        public bool ShouldNavigateToOriginalSource => NavigationSourcePageForBack != null;
        public Type NavigationSourcePageForBack { get; private set; }

        public object NavigationSourceParameterForBack { get; private set; }

        public void ClearNavigationContext()
        {
            NavigationSourcePageForBack = null;
            NavigationSourceParameterForBack = null;
        }

        // Favorite marks the series, from its overview, a season or an episode alike
        protected override BaseItemDto FavoriteTarget => Series;

        partial void OnSeriesChanged(BaseItemDto value)
        {
            IsFavorite = value?.UserData?.IsFavorite == true;
        }

        public void ClearState()
        {
            Seasons.Clear();
            Episodes.Clear();

            Series = null;
            CurrentSeason = null;

            IsInitialLoadComplete = false;
            SelectedEpisode = null;
            SeriesName = string.Empty;
            EpisodeTitle = string.Empty;
            EpisodeNumber = string.Empty;
            EpisodeOverview = string.Empty;
            AirDate = string.Empty;
            Runtime = string.Empty;
            Resolution = string.Empty;
            CanResume = false;
            BackdropImage = null;
            EpisodeThumbnail = null;
            SeriesPoster = null;
            SelectedSeasonIndex = -1;
            SelectedEpisodeIndex = -1;

            IsSeriesOverview = false;
            UpdateDetailSeparators();
            WatchProgressPercentage = 0;
            MarkWatchedText = WatchedButtonLabel(false);

            CancelLoad();

            NavigationSourcePageForBack = null;
            NavigationSourceParameterForBack = null;
        }

        public override async Task InitializeAsync(object parameter)
        {
            ClearState();

            var context = CreateErrorContext("InitializeSeason");
            try
            {
                IsLoading = true;
                StartNewLoad();

                try
                {
                    if (parameter is EpisodeNavigationParameter episodeNavParam)
                    {
                        Logger.LogDebug(
                            "SeasonDetailsViewModel.InitializeAsync - EpisodeNavigationParameter for {EpisodeName}, FromEpisodesButton: {FromEpisodesButton}, OriginalSourcePage: {OriginalSourcePageName}",
                            episodeNavParam.Episode?.Name, episodeNavParam.FromEpisodesButton, episodeNavParam.OriginalSourcePage?.Name);

                        if (episodeNavParam.FromEpisodesButton)
                        {
                            NavigationSourcePageForBack = episodeNavParam.OriginalSourcePage;
                            NavigationSourceParameterForBack = episodeNavParam.OriginalSourceParameter;
                        }

                        await HandleItemNavigationAsync(episodeNavParam.Episode);
                    }
                    else if (parameter is MediaPlaybackParams playbackParams)
                    {
                        Logger.LogDebug(
                            "SeasonDetailsViewModel.InitializeAsync - Received MediaPlaybackParams, extracting NavigationSourceParameter");

                        var lastPlayedItemId = playbackParams.Item?.Id;
                        if (lastPlayedItemId.HasValue)
                        {
                            Logger.LogDebug(
                                "Will restore selection to last played episode: {LastPlayedItemId}", lastPlayedItemId);
                        }

                        if (playbackParams.NavigationSourceParameter != null)
                        {
                            await InitializeAsync(playbackParams.NavigationSourceParameter);

                            if (lastPlayedItemId.HasValue)
                            {
                                var episodeIndex = Episodes.ToList().FindIndex(e => e.Id == lastPlayedItemId.Value);
                                if (episodeIndex >= 0)
                                {
                                    Logger.LogDebug("Restoring selection to episode at index {EpisodeIndex}", episodeIndex);
                                    await RunOnUIThreadAsync(() => SelectedEpisodeIndex = episodeIndex);
                                    await SelectEpisodeAsync(Episodes[episodeIndex]);
                                }
                                else
                                {
                                    Logger.LogWarning(
                                        "Could not find episode {LastPlayedItemId} in current episode list", lastPlayedItemId);
                                }
                            }
                        }
                        else
                        {
                            Logger.LogWarning(
                                "MediaPlaybackParams.NavigationSourceParameter is null, cannot restore state");
                            NavigationService.GoBack();
                        }
                    }
                    else if (parameter is BaseItemDto item)
                    {
                        Logger.LogDebug(
                            "SeasonDetailsViewModel.InitializeAsync - Received BaseItemDto: {ItemName} (Type: {ItemType}, Id: {ItemId})", item.Name, item.Type, item.Id);
                        await HandleItemNavigationAsync(item);
                    }
                    else if (TryGetGuidFromParameter(parameter, out var itemGuid))
                    {
                        var loadedItem = await FetchItemAsync(itemGuid, CurrentLoadToken);

                        if (loadedItem != null)
                        {
                            await HandleItemNavigationAsync(loadedItem);
                        }
                    }
                    else
                    {
                        Logger.LogWarning(
                            "SeasonDetailsViewModel: Unexpected parameter type: {ParameterGetType}", parameter?.GetType()?.Name ?? "null");
                        NavigationService.GoBack();
                    }
                }
                finally
                {
                    await RunOnUIThreadAsync(() => IsLoading = false);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        private async Task HandleItemNavigationAsync(BaseItemDto item)
        {
            Logger.LogDebug("HandleItemNavigationAsync called with item type: {ItemType}, Name: {ItemName}", item.Type, item.Name);

            if (item.Type == BaseItemDto_Type.Season)
            {
                CurrentSeason = item;
                await LoadSeasonDataAsync();
            }
            else if (item.Type == BaseItemDto_Type.Episode)
            {
                Logger.LogDebug("Episode navigation - SeasonId: {ItemSeasonId}, SeriesId: {ItemSeriesId}", item.SeasonId, item.SeriesId);

                if (item.SeasonId.HasValue)
                {
                    Logger.LogDebug("Loading season data for season ID: {ItemSeasonId}", item.SeasonId.Value);
                    CurrentSeason = await FetchItemAsync(item.SeasonId.Value, CurrentLoadToken);
                    if (CurrentSeason != null)
                    {
                        Logger.LogDebug("Season loaded: {CurrentSeasonName}", CurrentSeason.Name);

                        // The parameter's UserData (watched, position) may be stale; the server's is shown
                        var freshEpisode = item;
                        if (item.Id.HasValue)
                        {
                            try
                            {
                                var refreshed = await FetchItemAsync(item.Id.Value, CurrentLoadToken);
                                if (refreshed != null)
                                {
                                    freshEpisode = refreshed;
                                }
                            }
                            catch (Exception ex)
                            {
                                // The cached episode stands in
                                ErrorHandler.HandleError(ex, CreateErrorContext("RefreshEpisode", ErrorCategory.Media, ErrorSeverity.Warning));
                            }
                        }

                        await LoadSeasonDataAsync(freshEpisode);
                    }
                    else
                    {
                        Logger.LogError("Failed to load season data");
                    }
                }
                else
                {
                    Logger.LogWarning("Episode has no SeasonId");
                }
            }
            else if (item.Type == BaseItemDto_Type.Series)
            {
                Series = item;
                CurrentSeason = null;
                SelectedEpisode = null;
                Seasons.Clear();
                Episodes.Clear();

                await LoadSeriesFirstSeasonAsync();
            }
        }

        private async Task LoadSeasonDataAsync(BaseItemDto episodeToSelect = null)
        {
            var context = CreateErrorContext("LoadSeasonData");
            try
            {
                Logger.LogDebug("LoadSeasonDataAsync started - CurrentSeason: {CurrentSeasonName}", CurrentSeason?.Name);
                IsLoading = true;
                try
                {
                    if (CurrentSeason?.SeriesId.HasValue == true)
                    {
                        var seriesId = CurrentSeason.SeriesId.Value;
                        Logger.LogDebug("Loading series data for ID: {SeriesId}", seriesId);

                        Series = await FetchItemAsync(seriesId, CurrentLoadToken);
                        if (Series != null)
                        {
                            SeriesName = Series.Name;
                            Logger.LogDebug("Series loaded: {SeriesName}", SeriesName);
                            await LoadBackdropImageAsync(Series);
                        }
                        else
                        {
                            Logger.LogWarning("Failed to load series data");
                        }

                        await LoadSeasonsAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        Logger.LogWarning("CurrentSeason is null or has no SeriesId");
                    }

                    await LoadEpisodesAsync();

                    Logger.LogDebug("Episodes loaded: {EpisodesCount} episodes", Episodes.Count);

                    if (Episodes.Count > 0)
                    {
                        var targetIndex = 0;
                        if (episodeToSelect?.Id != null)
                        {
                            targetIndex = Episodes.ToList().FindIndex(ep => ep.Id == episodeToSelect.Id);
                            if (targetIndex < 0)
                            {
                                Logger.LogWarning(
                                    "Target episode '{EpisodeToSelectName}' not found in list; selecting first", episodeToSelect.Name);
                                targetIndex = 0;
                            }
                            else if (episodeToSelect.UserData != null)
                            {
                                Episodes[targetIndex].UserData = episodeToSelect.UserData;
                            }
                        }

                        var target = Episodes[targetIndex];
                        await RunOnUIThreadAsync(() => SelectedEpisodeIndex = targetIndex);
                        await SelectEpisodeAsync(target);
                        await RunOnUIThreadAsync(() => IsInitialLoadComplete = true);
                        Logger.LogDebug("Selected episode: {TargetName}", target.Name);
                    }
                    else
                    {
                        Logger.LogWarning("No episodes found after loading");
                    }
                }
                finally
                {
                    await RunOnUIThreadAsync(() => IsLoading = false);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private async Task LoadSeasonsAsync()
        {
            var context = CreateErrorContext("LoadSeasons");
            try
            {
                if (Series?.Id == null)
                {
                    Logger.LogWarning("LoadSeasonsAsync: Series or Series.Id is null");
                    return;
                }

                Logger.LogDebug("LoadSeasonsAsync: Loading seasons for series {SeriesName}", Series.Name);

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
                }, CurrentLoadToken);

                if (seasonsResult?.Items != null)
                {
                    await RunOnUIThreadAsync(() => Seasons.ReplaceAll(seasonsResult.Items));

                    Logger.LogDebug("LoadSeasonsAsync: Loaded {SeasonsCount} seasons", Seasons.Count);

                    var currentSeasonIndex = Seasons.ToList().FindIndex(s => s.Id == CurrentSeason?.Id);
                    if (currentSeasonIndex >= 0)
                    {
                        SelectedSeasonIndex = currentSeasonIndex;
                        Logger.LogDebug("LoadSeasonsAsync: Selected season index {CurrentSeasonIndex}", currentSeasonIndex);
                    }
                    else if (CurrentSeason != null)
                    {
                        Logger.LogWarning("LoadSeasonsAsync: Current season not found in seasons list");
                    }
                }
                else
                {
                    Logger.LogWarning("LoadSeasonsAsync: No seasons returned from API");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
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

                IsSeriesOverview = false;

                var episodesResult = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.ParentId = CurrentSeason.Id.Value;
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Overview, ItemFields.PrimaryImageAspectRatio, ItemFields.MediaStreams
                    };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.IndexNumber };
                }, CurrentLoadToken);

                await RunOnUIThreadAsync(() => Episodes.ReplaceAll(episodesResult?.Items));
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public async Task SelectEpisodeAsync(BaseItemDto episode)
        {
            if (episode == null)
            {
                return;
            }

            Logger.LogDebug("SelectEpisodeAsync called for episode: {EpisodeName}", episode.Name);

            var episodeTitle = episode.Name;
            var episodeNumber = $"Episode {episode.IndexNumber}";
            var episodeOverview = episode.Overview ?? "No overview available.";

            var airDate = episode.PremiereDate?.ToString("MMM d, yyyy");
            var runtime = episode.RunTimeTicks.HasValue
                ? TimeFormattingHelper.FormatDuration(TimeSpan.FromTicks(episode.RunTimeTicks.Value))
                : null;
            var videoHeight = episode.MediaStreams?.FirstOrDefault(ms => ms.Type == MediaStream_Type.Video)?.Height;
            var resolution = videoHeight.HasValue ? MediaLabels.Resolution(videoHeight.Value) : null;

            var thumbnailSource = await ImageLoadingService
                .LoadImageAsync(episode, ImageConstants.ImageTypePrimary, 400, 225).ConfigureAwait(false);

            // The thumbnail's await leaves the UI thread; set everything together back on it
            await RunOnUIThreadAsync(() =>
            {
                SelectedEpisode = episode;
                EpisodeTitle = episodeTitle;
                EpisodeNumber = episodeNumber;
                EpisodeOverview = episodeOverview;
                AirDate = airDate;
                Runtime = runtime;
                Resolution = resolution;
                UpdateDetailSeparators();
                EpisodeThumbnail = thumbnailSource;
                UpdateButtonStates(episode);
            });
        }

        private static string SeriesWatchedLabel(bool watched)
        {
            return watched ? "Mark Series Unwatched" : "Mark Series Watched";
        }

        // A dot before each item of the detail row that follows another shown item
        private void UpdateDetailSeparators()
        {
            var anyBefore = !string.IsNullOrEmpty(EpisodeNumber);
            IsAirDateSeparatorVisible = anyBefore && !string.IsNullOrEmpty(AirDate);
            anyBefore |= !string.IsNullOrEmpty(AirDate);
            IsRuntimeSeparatorVisible = anyBefore && !string.IsNullOrEmpty(Runtime);
            anyBefore |= !string.IsNullOrEmpty(Runtime);
            IsResolutionSeparatorVisible = anyBefore && !string.IsNullOrEmpty(Resolution);
        }

        /// <summary>
        ///     Play, or Resume with Play from Beginning and the progress bar, for one episode.
        ///     Resume follows the saved position, not the Played flag, as in DetailsViewModel: a
        ///     re-watch, or an episode the server marked watched at the completion threshold, can
        ///     still have a position to resume from.
        /// </summary>
        private void UpdateButtonStates(BaseItemDto episode)
        {
            CanResume = episode.UserData?.PlaybackPositionTicks > 0;
            WatchProgressPercentage = CanResume ? episode.UserData.PlayedPercentage ?? 0 : 0;
            MarkWatchedText = WatchedButtonLabel(episode.UserData?.Played == true);
        }

        private Task LoadSeriesPosterAsync(BaseItemDto series)
        {
            return ImageLoadingService.LoadImageIntoTargetAsync(
                series, ImageConstants.ImageTypePrimary, image => SeriesPoster = image, 600, 900);
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
                    await RunOnUIThreadAsync(() => IsLoading = false);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        // The Info button. In place rather than by navigating to the series: once this page's
        // parameter is the series, a second navigation to it is skipped as a repeat, so the
        // button would work once; and no back entry is added for what is a view of the same page
        [RelayCommand]
        private async Task OpenSeriesOverviewAsync()
        {
            if (Series == null)
            {
                return;
            }

            // Any season tab must reload after this, the previously selected one included
            CurrentSeason = null;
            SelectedEpisode = null;
            await ShowSeriesOverviewAsync();
        }

        private async Task ShowSeriesOverviewAsync()
        {
            var context = CreateErrorContext("ShowSeriesOverview", ErrorCategory.User);
            try
            {
                await RunOnUIThreadAsync(() =>
                {
                    SelectedSeasonIndex = -1;

                    IsSeriesOverview = true;
                });

                await LoadSeriesPosterAsync(Series);

                await RunOnUIThreadAsync(() =>
                {
                    EpisodeTitle = Series.Name;

                    // The episode row's slots: the premiere year for the episode number, the
                    // genres for the runtime
                    EpisodeNumber = Series.PremiereDate.HasValue ? $"Premiered: {Series.PremiereDate.Value.Year}" : null;
                    AirDate = null;
                    Runtime = Series.Genres is { Count: > 0 } ? string.Join(", ", Series.Genres) : null;
                    Resolution = null;
                    UpdateDetailSeparators();

                    EpisodeOverview = Series.Overview ?? "No overview available.";

                    CanResume = false;
                    WatchProgressPercentage = 0;
                    MarkWatchedText = SeriesWatchedLabel(Series.UserData?.Played == true);
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        [RelayCommand]
        private async Task SelectSeasonAsync(BaseItemDto season)
        {
            if (season != null && (CurrentSeason == null || season.Id != CurrentSeason.Id))
            {
                CurrentSeason = season;

                var index = Seasons.ToList().FindIndex(s => s.Id == season.Id);
                if (index >= 0)
                {
                    SelectedSeasonIndex = index;
                }

                SelectedEpisode = null;
                await LoadEpisodesAsync();

                if (Episodes.Count > 0)
                {
                    await RunOnUIThreadAsync(() => SelectedEpisodeIndex = 0);
                    await SelectEpisodeAsync(Episodes[0]);
                }
            }
        }

        public override async Task PlayAsync()
        {
            if (SelectedEpisode?.Id != null)
            {
                await PlayEpisodeAsync(SelectedEpisode);
            }
            else if (CurrentSeason == null && Series != null && Seasons.Count > 0)
            {
                await PlayNextUpEpisodeAsync();
            }
        }

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

        protected override async Task ToggleWatchedCoreAsync()
        {
            var context = CreateErrorContext("ToggleWatched", ErrorCategory.User);
            try
            {
                if (SelectedEpisode == null && Series != null)
                {
                    var isWatched = Series.UserData?.Played == true;
                    var newWatchedStatus = !isWatched;

                    await UserDataService.ToggleWatchedAsync(Series.Id.Value, newWatchedStatus);

                    if (Series.UserData == null)
                    {
                        Series.UserData = new UserItemDataDto();
                    }

                    Series.UserData.Played = newWatchedStatus;

                    MarkWatchedText = SeriesWatchedLabel(newWatchedStatus);
                }
                else if (SelectedEpisode?.Id != null)
                {
                    // Capture the reference now so a concurrent selection change can't affect us.
                    var episodeToUpdate = SelectedEpisode;
                    var isWatched = episodeToUpdate.UserData?.Played == true;
                    var newWatchedStatus = !isWatched;

                    var updatedData =
                        await UserDataService.ToggleWatchedAsync(episodeToUpdate.Id.Value, newWatchedStatus);

                    if (episodeToUpdate.UserData == null)
                    {
                        episodeToUpdate.UserData = new UserItemDataDto();
                    }

                    if (updatedData != null)
                    {
                        // Use the authoritative server response so PlaybackPositionTicks is
                        // correctly zeroed when marking unwatched (otherwise UpdateButtonStates
                        // sees stale ticks > 0 and shows Resume instead of Play).
                        episodeToUpdate.UserData.Played = updatedData.Played;
                        episodeToUpdate.UserData.PlayedPercentage = updatedData.PlayedPercentage;
                        episodeToUpdate.UserData.PlaybackPositionTicks = updatedData.PlaybackPositionTicks;
                    }
                    else
                    {
                        episodeToUpdate.UserData.Played = newWatchedStatus;
                        episodeToUpdate.UserData.PlayedPercentage = newWatchedStatus ? 100 : 0;
                        // Also clear ticks when unwatching so Resume doesn't appear incorrectly.
                        if (!newWatchedStatus)
                        {
                            episodeToUpdate.UserData.PlaybackPositionTicks = 0;
                        }
                    }

                    UpdateButtonStates(episodeToUpdate);

                    var episodeIndex = Episodes.IndexOf(episodeToUpdate);
                    if (episodeIndex >= 0)
                    {
                        var currentSelectedIndex = SelectedEpisodeIndex;

                        // Replace the item in the collection to trigger UI update
                        Episodes[episodeIndex] = episodeToUpdate;

                        // Restore selection after update to prevent focus loss
                        if (currentSelectedIndex >= 0 && currentSelectedIndex < Episodes.Count)
                        {
                            await RunOnUIThreadAsync(() => SelectedEpisodeIndex = currentSelectedIndex);
                            Logger.LogDebug(
                                "Restored SelectedEpisodeIndex to {CurrentSelectedIndex} after episode update", currentSelectedIndex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        [RelayCommand]
        private async Task ShuffleAsync()
        {
            var context = CreateErrorContext("Shuffle", ErrorCategory.User);
            try
            {
                Logger.LogInformation("Shuffling all episodes for series: {SeriesName}", Series?.Name);

                if (Series?.Id == null)
                {
                    Logger.LogWarning("Cannot shuffle: no series ID");
                    return;
                }

                var shuffledQueue = await _episodeQueueService.BuildShuffledSeriesQueueAsync(Series.Id.Value);

                if (shuffledQueue is { Count: > 0 })
                {
                    var playbackParams = new MediaPlaybackParams
                    {
                        Item = shuffledQueue[0],
                        QueueItems = shuffledQueue,
                        StartIndex = 0,
                        NavigationSourcePage = typeof(SeasonDetailsPage),
                        NavigationSourceParameter = CurrentSeason ?? Series,
                        SubtitleStreamIndex = -1
                    };

                    Logger.LogDebug("Starting shuffled playback with {ShuffledQueueCount} episodes", shuffledQueue.Count);
                    NavigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
                }
                else
                {
                    Logger.LogWarning("No episodes found to shuffle");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        private async Task PlayEpisodeAsync(BaseItemDto episode, bool resume = false, bool fromBeginning = false)
        {
            // Continue Watching's copy of the episode may carry a stale position
            var refreshedEpisode = episode;
            if (resume && episode?.Id != null)
            {
                try
                {
                    Logger.LogDebug("Refreshing episode UserData before playback for: {EpisodeName}", episode.Name);
                    var refreshedItem = await FetchItemAsync(episode.Id.Value);

                    if (refreshedItem != null)
                    {
                        refreshedEpisode = refreshedItem;
                        var oldPosition = episode.UserData?.PlaybackPositionTicks ?? 0;
                        var newPosition = refreshedItem.UserData?.PlaybackPositionTicks ?? 0;
                        if (oldPosition != newPosition)
                        {
                            Logger.LogInformation(
                                "Resume position updated from {OldPosition:hh\\:mm\\:ss} to {NewPosition:hh\\:mm\\:ss}", TimeSpan.FromTicks(oldPosition), TimeSpan.FromTicks(newPosition));
                        }
                    }
                }
                catch (Exception ex)
                {
                    // The cached episode stands in
                    ErrorHandler.HandleError(ex, CreateErrorContext("RefreshEpisodeBeforePlay", ErrorCategory.Media, ErrorSeverity.Warning));
                }
            }

            // The queue service reports its own failures and returns no queue
            var (episodeQueue, startIndex) = await _episodeQueueService.BuildEpisodeQueueAsync(refreshedEpisode);
            if (episodeQueue == null)
            {
                Logger.LogWarning("Failed to build episode queue, proceeding with single episode playback");
            }
            else if (startIndex >= 0 && startIndex < episodeQueue.Count)
            {
                episodeQueue[startIndex] = refreshedEpisode;
            }

            object navigationParameter = CurrentSeason;

            // If we're in series overview mode and playing an episode, use the episode itself
            // so we can navigate back to its season
            if (CurrentSeason == null && refreshedEpisode.SeasonId.HasValue)
            {
                navigationParameter = refreshedEpisode;
            }

            var playbackParams = new MediaPlaybackParams
            {
                Item = refreshedEpisode,
                StartPositionTicks = (resume, fromBeginning) switch
                {
                    (true, _) => refreshedEpisode.UserData?.PlaybackPositionTicks,
                    (_, true) => 0,
                    _ => null
                },
                QueueItems = episodeQueue,
                StartIndex = startIndex,
                NavigationSourcePage = typeof(SeasonDetailsPage),
                NavigationSourceParameter = navigationParameter,
                SubtitleStreamIndex = -1
            };

            NavigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
        }

        /// <summary>
        ///     Play on the series overview: the server's Next Up for this series -- the episode after
        ///     the last one watched, with specials placed by the library's settings (rewatching on, so
        ///     a finished series still has one). Without one, the first episode outside Specials.
        /// </summary>
        private async Task PlayNextUpEpisodeAsync()
        {
            var nextUp = await ApiClient.Shows.NextUp.GetAsync(config =>
            {
                config.QueryParameters.UserId = UserIdGuid.Value;
                config.QueryParameters.SeriesId = Series.Id.Value;
                config.QueryParameters.Limit = 1;
                config.QueryParameters.EnableRewatching = true;
                config.QueryParameters.Fields = new[]
                {
                    ItemFields.Overview, ItemFields.PrimaryImageAspectRatio, ItemFields.MediaStreams
                };
            }, CancellationToken.None);

            var episode = nextUp?.Items?.FirstOrDefault();
            if (episode == null)
            {
                var all = await ApiClient.Shows[Series.Id.Value].Episodes.GetAsync(config =>
                {
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.Fields = new[]
                    {
                        ItemFields.Overview, ItemFields.PrimaryImageAspectRatio, ItemFields.MediaStreams
                    };
                }, CancellationToken.None);
                episode = all?.Items?.FirstOrDefault(e => e.ParentIndexNumber != 0) ?? all?.Items?.FirstOrDefault();
            }

            if (episode == null)
            {
                Logger.LogWarning("No episode to play for series {SeriesName}", Series.Name);
                return;
            }

            Logger.LogInformation("Series Play: {EpisodeName} (S{Season}E{Episode})", episode.Name,
                episode.ParentIndexNumber, episode.IndexNumber);

            // Show where it is before it plays, as choosing the episode by hand would
            var season = Seasons.FirstOrDefault(s => s.Id == episode.SeasonId);
            if (season != null)
            {
                await SelectSeasonAsync(season);
                var episodeIndex = Episodes.ToList().FindIndex(e => e.Id == episode.Id);
                if (episodeIndex >= 0)
                {
                    await SelectEpisodeAsync(Episodes[episodeIndex]);
                    await RunOnUIThreadAsync(() => SelectedEpisodeIndex = episodeIndex);
                }
            }

            IsSeriesOverview = false;

            await PlayEpisodeAsync(episode);
        }
    }
}
