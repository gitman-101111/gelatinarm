using System;
using System.Collections.Generic;
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
    public partial class ArtistDetailsViewModel : MusicDetailsViewModel
    {
        [ObservableProperty] private ObservableCollection<AlbumWithTracks> _albums = new();

        [ObservableProperty] private string _artistName;

        public ArtistDetailsViewModel(
            ILogger<ArtistDetailsViewModel> logger,
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
            return InitializeFromParameterAsync(parameter, "InitializeArtist", LoadArtistFromDtoAsync, LoadArtistByIdAsync);
        }

        private async Task LoadArtistFromDtoAsync(BaseItemDto dto, CancellationToken cancellationToken)
        {
            Logger.LogDebug("ArtistDetailsViewModel: Loading from BaseItemDto: {DtoName}", dto.Name);

            // A list's copy of the artist lacks the biography; the album page refetches the same way
            if (dto.Id.HasValue)
            {
                await LoadArtistByIdAsync(dto.Id.Value, cancellationToken);
                return;
            }

            await RunOnUIThreadAsync(() => CurrentItem = dto);
            await LoadArtistDetailsAsync(cancellationToken);
        }

        private async Task LoadArtistByIdAsync(Guid itemId, CancellationToken cancellationToken)
        {
            Logger.LogDebug("ArtistDetailsViewModel: Loading artist by ID: {ItemId}", itemId);

            var artist = await FetchRequiredItemAsync(itemId, cancellationToken);
            await RunOnUIThreadAsync(() => CurrentItem = artist);
            await LoadArtistDetailsAsync(cancellationToken);
        }

        private async Task LoadArtistDetailsAsync(CancellationToken cancellationToken)
        {
            if (!UserIdGuid.HasValue)
            {
                return;
            }

            await RunOnUIThreadAsync(UpdateArtistUi);
            await LoadAlbumsAsync(cancellationToken);

            Logger.LogDebug(
                "ArtistDetailsViewModel: Loaded artist: {CurrentItemName} with {AlbumsCount} albums", CurrentItem.Name, Albums.Count);
        }

        private void UpdateArtistUi()
        {
            ArtistName = CurrentItem.Name ?? string.Empty;
            Overview = CurrentItem.Overview;

            LoadPrimaryImage();

            UpdateFavoriteState();
        }

        private async Task LoadAlbumsAsync(CancellationToken cancellationToken)
        {
            var context = CreateErrorContext("LoadAlbums");
            try
            {
                var response = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.ArtistIds = new Guid?[] { CurrentItem.Id.Value };
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.IncludeItemTypes = new[] { BaseItemKind.MusicAlbum };
                    config.QueryParameters.Recursive = true;
                    config.QueryParameters.SortBy = new[] { ItemSortBy.ProductionYear, ItemSortBy.SortName };
                    config.QueryParameters.SortOrder = new[] { SortOrder.Descending };
                }, cancellationToken).ConfigureAwait(false);

                await RunOnUIThreadAsync(() =>
                {
                    Albums.Clear();

                    foreach (var album in response?.Items ?? Enumerable.Empty<BaseItemDto>())
                    {
                        var albumWithTracks = new AlbumWithTracks { Album = album };
                        Albums.Add(albumWithTracks);

                        FireAndForget(() => LoadAlbumTracksAsync(albumWithTracks, cancellationToken));
                    }
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private async Task LoadAlbumTracksAsync(AlbumWithTracks albumWithTracks, CancellationToken cancellationToken)
        {
            if (albumWithTracks.Album.Id == null || !UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("LoadAlbumTracks");
            try
            {
                var tracks = await FetchAlbumTracksAsync(albumWithTracks.Album.Id.Value, cancellationToken)
                    .ConfigureAwait(false);
                if (tracks != null)
                {
                    await RunOnUIThreadAsync(() => albumWithTracks.Tracks.ReplaceAll(tracks));
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public override Task PlayAsync()
        {
            return PlayAllTracksAsync(tracks => MusicPlayerService.PlayItemsAsync(tracks));
        }

        [RelayCommand]
        private Task ShuffleAsync()
        {
            return PlayAllTracksAsync(MusicPlayerService.ShufflePlayAsync);
        }

        /// <summary>
        ///     Plays every track on the page, starting at <paramref name="track" />: Previous reaches
        ///     the ones before it, as on the album page and in Jellyfin's web client.
        /// </summary>
        protected override Task PlayTrackCoreAsync(BaseItemDto track)
        {
            return PlayAllTracksAsync(tracks => MusicPlayerService.PlayItemsAsync(tracks, Math.Max(tracks.IndexOf(track), 0)));
        }

        /// <summary>
        ///     Hands the tracks of every album on the page, in page order, to <paramref name="play" />.
        /// </summary>
        private Task PlayAllTracksAsync(Func<List<BaseItemDto>, Task> play)
        {
            var allTracks = Albums.SelectMany(a => a.Tracks).ToList();
            if (allTracks.Count == 0)
            {
                Logger.LogWarning("No tracks available to play");
                return Task.CompletedTask;
            }

            return play(allTracks);
        }

        [RelayCommand]
        private Task InstantMixAsync()
        {
            return MusicPlayerService.PlayInstantMixAsync(CurrentItem);
        }
    }

    public class AlbumWithTracks
    {
        public BaseItemDto Album { get; set; }
        public ObservableCollection<BaseItemDto> Tracks { get; } = new();
    }
}
