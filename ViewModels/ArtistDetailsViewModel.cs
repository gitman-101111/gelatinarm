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
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Media.Imaging;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     ViewModel for the ArtistDetailsPage handling artist data and music playback
    /// </summary>
    public partial class ArtistDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        private readonly IMusicPlayerService _musicPlayerService;

        private readonly IPlaybackQueueService _playbackQueueService;

        // Additional Services
        private readonly IPreferencesService _preferencesService;

        [ObservableProperty] private ObservableCollection<AlbumWithTracks> _albums = new();

        [ObservableProperty] private BitmapImage _artistImage;

        [ObservableProperty] private string _artistName;

        // Observable properties

        private CancellationTokenSource _loadCts;

        public ArtistDetailsViewModel(
            ILogger<ArtistDetailsViewModel> logger,
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

            var context = CreateErrorContext("InitializeArtist");
            try
            {
                IsLoading = true;
                try
                {
                    if (parameter is BaseItemDto dto)
                    {
                        await LoadArtistFromDtoAsync(dto, _loadCts.Token);
                    }
                    else if (parameter is string guidString && Guid.TryParse(guidString, out var itemId))
                    {
                        await LoadArtistByIdAsync(itemId, _loadCts.Token);
                    }
                    else if (parameter is Guid guid)
                    {
                        await LoadArtistByIdAsync(guid, _loadCts.Token);
                    }
                }
                finally
                {
                    IsLoading = false;
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

        private async Task LoadArtistFromDtoAsync(BaseItemDto dto, CancellationToken cancellationToken)
        {
            Logger?.LogInformation($"ArtistDetailsViewModel: Loading from BaseItemDto: {dto.Name}");
            CurrentItem = dto;
            await LoadArtistDetailsAsync(cancellationToken);
        }

        private async Task LoadArtistByIdAsync(Guid itemId, CancellationToken cancellationToken)
        {
            Logger?.LogInformation($"ArtistDetailsViewModel: Loading artist by ID: {itemId}");

            if (!UserIdGuid.HasValue)
            {
                throw new InvalidOperationException("User ID not available");
            }

            // Use Items API to get artist by ID
            var artist = await ApiClient.Items[itemId].GetAsync(config =>
            {
                config.QueryParameters.UserId = UserIdGuid.Value;
                // Note: The individual item endpoint should return all fields by default
            }, cancellationToken).ConfigureAwait(false);

            if (artist == null)
            {
                throw new Exception($"Failed to load artist with ID {itemId}");
            }

            CurrentItem = artist;
            await LoadArtistDetailsAsync(cancellationToken);
        }

        private async Task LoadArtistDetailsAsync(CancellationToken cancellationToken)
        {
            if (CurrentItem == null || !UserIdGuid.HasValue)
            {
                return;
            }
            await RunOnUIThreadAsync(() => UpdateArtistUI()); await LoadAlbumsAsync(cancellationToken);

            Logger?.LogInformation(
                $"ArtistDetailsViewModel: Loaded artist: {CurrentItem?.Name ?? CurrentItem?.Id?.ToString() ?? "Unknown"} with {Albums.Count} albums");
        }

        private void UpdateArtistUI()
        {
            if (CurrentItem == null)
            {
                return;
            }

            // Artist name
            ArtistName = CurrentItem.Name ?? string.Empty;

            // Artist bio is shown using Overview property from base class

            // Load artist image
            LoadArtistImage();

            // Update favorite button
            UpdateFavoriteButton();
        }


        private void LoadArtistImage()
        {
            if (CurrentItem?.Id == null)
            {
                return;
            }

            var context = CreateErrorContext("LoadArtistImage", ErrorCategory.Media);
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
                        ArtistImage = new BitmapImage(new Uri(imageUrl));
                    }

                    await Task.CompletedTask;
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
            });
        }

        private async Task LoadAlbumsAsync(CancellationToken cancellationToken)
        {
            if (CurrentItem?.Id == null || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("LoadAlbums");
            try
            {
                // Get all albums by this artist
                var response = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.ArtistIds = new Guid?[] { CurrentItem.Id.Value };
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.MusicAlbum };
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.SortBy = new[] { ItemSortBy.ProductionYear, ItemSortBy.SortName };
                    config.QueryParameters.SortOrder = new[] { SortOrder.Descending };
                }, cancellationToken).ConfigureAwait(false);

                if (response?.Items != null)
                {
                    await RunOnUIThreadAsync(async () =>
                    {
                        Albums.Clear();

                        foreach (var album in response.Items)
                        {
                            var albumWithTracks = new AlbumWithTracks { Album = album };
                            Albums.Add(albumWithTracks);

                            // Load tracks for each album asynchronously
                            AsyncHelper.FireAndForget(async () =>
                                await LoadAlbumTracksAsync(albumWithTracks, CancellationToken.None));
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

        private async Task LoadAlbumTracksAsync(AlbumWithTracks albumWithTracks, CancellationToken cancellationToken)
        {
            if (albumWithTracks?.Album?.Id == null || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("LoadAlbumTracks");
            try
            {
                var response = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.ParentId = albumWithTracks.Album.Id.Value;
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.SortBy = new ItemSortBy[] { ItemSortBy.ParentIndexNumber, ItemSortBy.IndexNumber };
                }, cancellationToken).ConfigureAwait(false);

                if (response?.Items != null)
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        albumWithTracks.Tracks.Clear();

                        // Check if album has multiple discs
                        var discNumbers = response.Items.Select(t => t.ParentIndexNumber ?? 1).Distinct().ToList();
                        var hasMultipleDiscs = discNumbers.Count > 1;
                        var previousDiscNumber = -1;

                        foreach (var track in response.Items)
                        {
                            var currentDiscNumber = track.ParentIndexNumber ?? 1;

                            // Store track number in Overview for display
                            if (track.IndexNumber.HasValue)
                            {
                                if (hasMultipleDiscs)
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

                            albumWithTracks.Tracks.Add(track);
                            previousDiscNumber = currentDiscNumber;
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

        // Commands
        [RelayCommand]
        public override async Task PlayAsync()
        {
            if (!Albums.Any() || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("PlayAllTracks", ErrorCategory.Media);
            try
            {
                // Create queue from all tracks from all albums
                var allTracks = Albums.SelectMany(a => a.Tracks).ToList();

                if (!allTracks.Any())
                {
                    Logger?.LogWarning("No tracks available to play");
                    return;
                }

                // Use MusicPlayerService for music playback
                await _musicPlayerService.PlayItems(allTracks);
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
            if (!Albums.Any() || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("ShufflePlay", ErrorCategory.Media);
            try
            {
                // Create queue from all tracks from all albums
                var allTracks = Albums.SelectMany(a => a.Tracks).ToList();

                if (!allTracks.Any())
                {
                    Logger?.LogWarning("No tracks available to shuffle");
                    return;
                }

                // Enable shuffle mode
                _musicPlayerService.SetShuffle(true);

                // Start playing from a random track
                var random = new Random();
                var randomStartIndex = random.Next(allTracks.Count);

                // Use MusicPlayerService for music playback with shuffle enabled
                await _musicPlayerService.PlayItems(allTracks, randomStartIndex);
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
        private void NavigateToAlbum(BaseItemDto album)
        {
            if (album == null)
            {
                return;
            }

            NavigationService.NavigateToItemDetails(album);
        }

        [RelayCommand]
        private async Task PlayAlbumAsync(AlbumWithTracks albumWithTracks)
        {
            if (albumWithTracks?.Tracks?.Any() != true || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("PlayAlbum", ErrorCategory.Media);
            try
            {
                // Use MusicPlayerService for music playback
                await _musicPlayerService.PlayItems(albumWithTracks.Tracks.ToList());
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
        private async Task PlayTrackAsync(BaseItemDto track)
        {
            if (track == null || !Albums.Any() || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("PlayTrack", ErrorCategory.Media);
            try
            {
                // Create queue from all tracks from all albums
                var allTracks = Albums.SelectMany(a => a.Tracks).ToList();

                // Find index of clicked track in the complete list
                var clickedIndex = allTracks.IndexOf(track);
                if (clickedIndex < 0)
                {
                    clickedIndex = 0;
                }

                // Reorder the queue: tracks from clicked index to end, then tracks from beginning to clicked index
                var reorderedTracks = allTracks.Skip(clickedIndex).Concat(allTracks.Take(clickedIndex)).ToList();

                // Use MusicPlayerService for music playback with reordered tracks, starting at index 0
                await _musicPlayerService.PlayItems(reorderedTracks);
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
        private async Task InstantMixAsync()
        {
            if (CurrentItem?.Id == null || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("InstantMix", ErrorCategory.Media);
            try
            {
                // Get instant mix for the artist
                var instantMix = await ApiClient.Items[CurrentItem.Id.Value].InstantMix.GetAsync(config =>
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
                    Logger?.LogWarning("No instant mix items returned");
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
            // Artist-specific loading is handled in LoadArtistDetailsAsync
            await Task.CompletedTask;
        }
    }

    // Helper class to group albums with their tracks
    public class AlbumWithTracks
    {
        public BaseItemDto Album { get; set; }
        public ObservableCollection<BaseItemDto> Tracks { get; } = new();
    }
}
