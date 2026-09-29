using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Details;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Music
{
    public partial class AlbumDetailsViewModel : MusicDetailsViewModel
    {
        [ObservableProperty] private string _artistName;

        [ObservableProperty] private string _duration;

        [ObservableProperty] private string _genres;

        [ObservableProperty] private string _trackCount;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _tracks = new();

        public AlbumDetailsViewModel(
            ILogger<AlbumDetailsViewModel> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IUserDataService userDataService,
            IPlaybackQueueService playbackQueueService,
            IMusicPlayerService musicPlayerService) : base(
            logger,
            apiClient,
            userProfileService,
            navigationService,
            imageLoadingService,
            userDataService,
            playbackQueueService,
            musicPlayerService)
        {
        }

        public override Task InitializeAsync(object parameter)
        {
            return InitializeFromParameterAsync(parameter, "InitializeAlbum", LoadAlbumFromDtoAsync, LoadAlbumByIdAsync);
        }

        private async Task LoadAlbumFromDtoAsync(BaseItemDto dto, CancellationToken cancellationToken)
        {
            Logger.LogDebug("AlbumDetailsViewModel: Loading from BaseItemDto: {DtoName}", dto.Name);

            // A list's copy may lack the overview and images; the collection and artist pages refetch the same way
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
            Logger.LogDebug("AlbumDetailsViewModel: Loading album by ID: {ItemId}", itemId);

            CurrentItem = await FetchRequiredItemAsync(itemId, cancellationToken);
            await LoadAlbumDetailsAsync(cancellationToken);
        }

        private async Task LoadAlbumDetailsAsync(CancellationToken cancellationToken)
        {
            if (!UserIdGuid.HasValue)
            {
                return;
            }

            await RunOnUIThreadAsync(UpdateAlbumUi);

            await LoadTracksAsync(cancellationToken);

            Logger.LogDebug(
                "AlbumDetailsViewModel: Loaded album: {CurrentItemName} with {TracksCount} tracks", CurrentItem.Name, Tracks.Count);
        }

        private void UpdateAlbumUi()
        {
            Title = CurrentItem.Name ?? string.Empty;

            // Each line shows when it has text (NullableToVisibilityConverter)
            ArtistName = CurrentItem.AlbumArtists is { Count: > 0 }
                ? string.Join(", ", CurrentItem.AlbumArtists.Select(a => a.Name))
                : CurrentItem.AlbumArtist;
            Year = CurrentItem.ProductionYear?.ToString();
            Duration = CurrentItem.RunTimeTicks.HasValue
                ? TimeFormattingHelper.FormatTime(TimeSpan.FromTicks(CurrentItem.RunTimeTicks.Value))
                : null;
            Genres = CurrentItem.Genres is { Count: > 0 } ? string.Join(" • ", CurrentItem.Genres) : null;

            LoadPrimaryImage();

            UpdateFavoriteState();

            UpdatePlaybackState();
        }

        private async Task LoadTracksAsync(CancellationToken cancellationToken)
        {
            var context = CreateErrorContext("LoadTracks");
            try
            {
                var tracks = await FetchAlbumTracksAsync(CurrentItem.Id.Value, cancellationToken).ConfigureAwait(false);
                if (tracks != null)
                {
                    await RunOnUIThreadAsync(() =>
                    {
                        Tracks.ReplaceAll(tracks);
                        TrackCount = Tracks.Count > 0 ? $"{Tracks.Count} tracks" : null;
                    });
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public override Task PlayAsync()
        {
            return Tracks.Count == 0 ? Task.CompletedTask : MusicPlayerService.PlayItemsAsync(Tracks.ToList());
        }

        [RelayCommand]
        private Task ShuffleAsync()
        {
            return Tracks.Count == 0 ? Task.CompletedTask : MusicPlayerService.ShufflePlayAsync(Tracks.ToList());
        }

        [RelayCommand]
        private void NavigateToArtist()
        {
            var firstArtist = CurrentItem.AlbumArtists?.FirstOrDefault();
            if (firstArtist?.Id != null)
            {
                NavigationService.Navigate(typeof(ArtistDetailsPage), firstArtist.Id.Value);
            }
        }

        protected override Task PlayTrackCoreAsync(BaseItemDto track)
        {
            return MusicPlayerService.PlayItemsAsync(Tracks.ToList(), Math.Max(Tracks.IndexOf(track), 0));
        }
    }
}
