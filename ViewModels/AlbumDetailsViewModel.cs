using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.Views;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Media.Imaging;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     ViewModel for the AlbumDetailsPage handling album data and track playback
    /// </summary>
    public partial class AlbumDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        private readonly IMusicPlayerService _musicPlayerService;

        private readonly IPlaybackQueueService _playbackQueueService;

        // Additional Services
        private readonly IPreferencesService _preferencesService;

        [ObservableProperty] private string _artistName;

        [ObservableProperty] private BitmapImage _coverImage;

        [ObservableProperty] private string _duration;

        [ObservableProperty] private string _genres;

        [ObservableProperty] private bool _isArtistVisible;

        [ObservableProperty] private bool _isDurationVisible;

        [ObservableProperty] private bool _isGenresVisible;

        [ObservableProperty] private bool _isTrackCountVisible;

        [ObservableProperty] private bool _isYearVisible;

        private CancellationTokenSource _loadCts;

        [ObservableProperty] private string _playButtonText = "Play";

        [ObservableProperty] private BaseItemDto _selectedTrack;

        [ObservableProperty] private string _trackCount;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _tracks = new();

        private bool _hasMultipleDiscs = false;
        public bool HasMultipleDiscs => _hasMultipleDiscs;

        [ObservableProperty] private string _year;

        public AlbumDetailsViewModel(
            ILogger<AlbumDetailsViewModel> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IMediaPlaybackService mediaPlaybackService,
            IUserDataService userDataService,
            IPreferencesService preferencesService,
            IPlaybackQueueService playbackQueueService,
            IMusicPlayerService musicPlayerService) : base(
            logger,
            apiClient,
            userProfileService,
            navigationService,
            imageLoadingService,
            mediaPlaybackService,
            userDataService)
        {
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
            _playbackQueueService =
                playbackQueueService ?? throw new ArgumentNullException(nameof(playbackQueueService));
            _musicPlayerService = musicPlayerService ?? throw new ArgumentNullException(nameof(musicPlayerService));
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

            var context = CreateErrorContext("InitializeAlbum");
            try
            {
                IsLoading = true;
                try
                {
                    if (parameter is BaseItemDto dto)
                    {
                        await LoadAlbumFromDtoAsync(dto, _loadCts.Token);
                    }
                    else if (parameter is string guidString && Guid.TryParse(guidString, out var itemId))
                    {
                        await LoadAlbumByIdAsync(itemId, _loadCts.Token);
                    }
                    else if (parameter is Guid guid)
                    {
                        await LoadAlbumByIdAsync(guid, _loadCts.Token);
                    }
                }
                finally
                {
                    IsLoading = false;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        private async Task LoadAlbumFromDtoAsync(BaseItemDto dto, CancellationToken cancellationToken)
        {
            Logger?.LogInformation($"AlbumDetailsViewModel: Loading from BaseItemDto: {dto.Name}");

            // If we have an ID, fetch the full album details to ensure we have all metadata including images
            if (dto.Id.HasValue)
            {
                await LoadAlbumByIdAsync(dto.Id.Value, cancellationToken);
            }
            else
            {
                CurrentItem = dto;
                await LoadAlbumDetailsAsync(cancellationToken);
            }
        }

        private async Task LoadAlbumByIdAsync(Guid itemId, CancellationToken cancellationToken)
        {
            Logger?.LogInformation($"AlbumDetailsViewModel: Loading album by ID: {itemId}");

            if (!UserIdGuid.HasValue)
            {
                throw new InvalidOperationException("User ID not available");
            }

            var response = await ApiClient.Items[itemId].GetAsync(config =>
            {
                config.QueryParameters.UserId = UserIdGuid.Value;
                // Note: The individual item endpoint returns all fields by default, Fields parameter is not supported
            }, cancellationToken).ConfigureAwait(false);

            if (response == null)
            {
                throw new Exception($"Failed to load album with ID {itemId}");
            }

            CurrentItem = response;
            await LoadAlbumDetailsAsync(cancellationToken);
        }

        private async Task LoadAlbumDetailsAsync(CancellationToken cancellationToken)
        {
            if (CurrentItem == null || !UserIdGuid.HasValue)
            {
                return;
            }

            // Update UI on UI thread
            await RunOnUIThreadAsync(() => UpdateAlbumUI());

            // Load tracks
            await LoadTracksAsync(cancellationToken);

            Logger?.LogInformation(
                $"AlbumDetailsViewModel: Loaded album: {CurrentItem.Name} with {Tracks.Count} tracks");
        }

        private void UpdateAlbumUI()
        {
            if (CurrentItem == null)
            {
                return;
            }

            // Album title
            Title = CurrentItem.Name ?? string.Empty;

            // Artist name
            if (CurrentItem.AlbumArtists?.Any() == true)
            {
                ArtistName = string.Join(", ", CurrentItem.AlbumArtists.Select(a => a.Name));
                IsArtistVisible = true;
            }
            else if (!string.IsNullOrEmpty(CurrentItem.AlbumArtist))
            {
                ArtistName = CurrentItem.AlbumArtist;
                IsArtistVisible = true;
            }
            else
            {
                IsArtistVisible = false;
            }

            // Year
            if (CurrentItem.ProductionYear.HasValue)
            {
                Year = CurrentItem.ProductionYear.Value.ToString();
                IsYearVisible = true;
            }
            else
            {
                IsYearVisible = false;
            }

            // Runtime
            if (CurrentItem.RunTimeTicks.HasValue)
            {
                var runtime = TimeSpan.FromTicks(CurrentItem.RunTimeTicks.Value);
                Duration = TimeFormattingHelper.FormatTime(runtime);
                IsDurationVisible = true;
            }
            else
            {
                IsDurationVisible = false;
            }

            // Genres
            if (CurrentItem.Genres?.Any() == true)
            {
                Genres = string.Join(" • ", CurrentItem.Genres);
                IsGenresVisible = true;
            }
            else
            {
                IsGenresVisible = false;
            }

            // Load album art
            Logger?.LogInformation($"Loading album art for: {CurrentItem.Name} (ID: {CurrentItem.Id})");
            LoadPrimaryImage(image => CoverImage = image, "LoadAlbumArt");

            // Update favorite button
            UpdateFavoriteButton();

            // Update play button based on playback status
            UpdatePlayButton();
        }


        private void UpdatePlayButton()
        {
            if (CurrentItem?.UserData?.PlaybackPositionTicks > 0)
            {
                PlayButtonText = "Resume";
            }
            else
            {
                PlayButtonText = "Play";
            }
        }


        private async Task LoadTracksAsync(CancellationToken cancellationToken)
        {
            if (CurrentItem?.Id == null || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("LoadTracks");
            try
            {
                var response = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.ParentId = CurrentItem.Id.Value;
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.SortBy = new ItemSortBy[] { ItemSortBy.ParentIndexNumber, ItemSortBy.IndexNumber };
                }, cancellationToken).ConfigureAwait(false);

                if (response?.Items != null)
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        Tracks.Clear();

                        // Check if we have multiple discs
                        var discNumbers = response.Items
                            .Select(t => t.ParentIndexNumber ?? 1)
                            .Distinct()
                            .ToList();
                        _hasMultipleDiscs = discNumbers.Count > 1;

                        // Add tracks, marking first track of each disc
                        int? previousDiscNumber = null;
                        foreach (var track in response.Items)
                        {
                            var currentDiscNumber = track.ParentIndexNumber ?? 1;

                            // Store track number in Overview for display
                            if (track.IndexNumber.HasValue)
                            {
                                if (_hasMultipleDiscs)
                                {
                                    // Multi-disc: show disc-track format with leading zeros
                                    track.Overview = $"{currentDiscNumber:D2}-{track.IndexNumber:D2}";
                                }
                                else
                                {
                                    // Single disc: show track number with leading zeros
                                    track.Overview = $"{track.IndexNumber:D2}";
                                }
                            }
                            else
                            {
                                track.Overview = null;
                            }

                            Tracks.Add(track);
                            previousDiscNumber = currentDiscNumber;
                        }

                        // Update track count
                        if (Tracks.Any())
                        {
                            TrackCount = $"{Tracks.Count} tracks";
                            IsTrackCountVisible = true;
                        }
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
        public override async Task PlayAsync()
        {
            if (CurrentItem?.Id == null || !UserIdGuid.HasValue || !Tracks.Any())
            {
                return;
            }

            var context = CreateErrorContext("PlayAlbum", ErrorCategory.Media);
            try
            {
                // Use MusicPlayerService for music playback
                await _musicPlayerService.PlayItems(Tracks.ToList());
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        [RelayCommand]
        private async Task ShuffleAsync()
        {
            if (!Tracks.Any() || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("ShuffleAlbum", ErrorCategory.Media);
            try
            {
                // Shuffle the tracks
                var random = new Random();
                var shuffledTracks = Tracks.OrderBy(x => random.Next()).ToList();

                // Use MusicPlayerService for music playback
                await _musicPlayerService.PlayItems(shuffledTracks);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        [RelayCommand]
        private async Task ToggleFavoriteAsync()
        {
            if (CurrentItem?.Id == null || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("ToggleFavorite", ErrorCategory.User);
            try
            {
                var newFavoriteStatus = !IsFavorite;

                if (newFavoriteStatus)
                {
                    await ApiClient.UserFavoriteItems[CurrentItem.Id.Value]
                        .PostAsync(config => config.QueryParameters.UserId = UserIdGuid.Value);
                }
                else
                {
                    await ApiClient.UserFavoriteItems[CurrentItem.Id.Value]
                        .DeleteAsync(config => config.QueryParameters.UserId = UserIdGuid.Value);
                }

                IsFavorite = newFavoriteStatus;
                FavoriteButtonText = newFavoriteStatus ? "Remove from Favorites" : "Add to Favorites";

                if (CurrentItem.UserData == null)
                {
                    CurrentItem.UserData = new UserItemDataDto();
                }

                CurrentItem.UserData.IsFavorite = newFavoriteStatus;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        [RelayCommand]
        private void NavigateToArtist()
        {
            if (CurrentItem?.AlbumArtists?.Any() == true)
            {
                var firstArtist = CurrentItem.AlbumArtists.FirstOrDefault();
                if (firstArtist?.Id != null)
                {
                    NavigationService.Navigate(typeof(ArtistDetailsPage), firstArtist.Id.Value);
                }
            }
        }

        [RelayCommand]
        private async Task PlayTrackAsync(BaseItemDto track)
        {
            if (track == null || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("PlayTrack", ErrorCategory.Media);
            try
            {
                // Find index of clicked track
                var startIndex = Tracks.IndexOf(track);
                if (startIndex < 0)
                {
                    startIndex = 0;
                }

                // Use MusicPlayerService for music playback
                await _musicPlayerService.PlayItems(Tracks.ToList(), startIndex);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        [RelayCommand]
        private async Task PlayNextAsync(BaseItemDto track)
        {
            if (track?.Id == null)
            {
                return;
            }

            var context = CreateErrorContext("PlayNext", ErrorCategory.Media);
            try
            {
                _playbackQueueService.AddToQueueNext(track);
                Logger?.LogInformation($"Added track '{track.Name}' to play next");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        [RelayCommand]
        private async Task AddToQueueAsync(BaseItemDto track)
        {
            if (track?.Id == null)
            {
                return;
            }

            var context = CreateErrorContext("AddToQueue", ErrorCategory.Media);
            try
            {
                _playbackQueueService.AddToQueue(track);
                Logger?.LogInformation($"Added track '{track.Name}' to queue");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        [RelayCommand]
        private async Task StartInstantMixAsync(BaseItemDto track)
        {
            if (track?.Id == null || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("StartInstantMix", ErrorCategory.Media);
            try
            {
                // Get instant mix for the track
                var instantMix = await ApiClient.Items[track.Id.Value].InstantMix.GetAsync(config =>
                {
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.Limit = 100;
                    config.QueryParameters.Fields = new[] { ItemFields.MediaSources };
                }).ConfigureAwait(false);

                if (instantMix?.Items != null && instantMix.Items.Any())
                {
                    // Use MusicPlayerService for music playback
                    await _musicPlayerService.PlayItems(instantMix.Items.ToList());
                }
                else
                {
                    Logger?.LogWarning("No instant mix items returned for track");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        // Helper methods

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
            // Album-specific loading is handled in LoadAlbumFromDtoAsync
            await Task.CompletedTask;
        }
    }
}
