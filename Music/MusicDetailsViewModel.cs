using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Details;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Music
{
    /// <summary>
    ///     What the album and artist pages share: the track list and the track commands
    /// </summary>
    public abstract partial class MusicDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        protected MusicDetailsViewModel(
            ILogger logger,
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
            userDataService)
        {
            PlaybackQueueService = playbackQueueService;
            MusicPlayerService = musicPlayerService;
        }

        protected IMusicPlayerService MusicPlayerService { get; }
        private IPlaybackQueueService PlaybackQueueService { get; }

        /// <summary>
        ///     An album's tracks in disc and track order, or null if the request returned nothing.
        ///     Each track's Overview carries the number the track list shows: "07", or "02-07" when
        ///     the album has more than one disc.
        /// </summary>
        protected async Task<List<BaseItemDto>> FetchAlbumTracksAsync(Guid albumId, CancellationToken cancellationToken)
        {
            var response = await ApiClient.Items.GetAsync(config =>
            {
                config.QueryParameters.ParentId = albumId;
                config.QueryParameters.UserId = UserIdGuid.Value;
                config.QueryParameters.SortBy = new[] { ItemSortBy.ParentIndexNumber, ItemSortBy.IndexNumber };
            }, cancellationToken).ConfigureAwait(false);

            var tracks = response?.Items;
            if (tracks == null)
            {
                return null;
            }

            var hasMultipleDiscs = tracks.Select(t => t.ParentIndexNumber ?? 1).Distinct().Count() > 1;
            foreach (var track in tracks)
            {
                if (!track.IndexNumber.HasValue)
                {
                    track.Overview = null;
                }
                else if (hasMultipleDiscs)
                {
                    track.Overview = $"{track.ParentIndexNumber ?? 1:D2}-{track.IndexNumber:D2}";
                }
                else
                {
                    track.Overview = $"{track.IndexNumber:D2}";
                }
            }

            return tracks;
        }

        // Each page plays a track from its own list: the album's tracks, or every album's on the artist page
        [RelayCommand]
        private Task PlayTrackAsync(BaseItemDto track)
        {
            return PlayTrackCoreAsync(track);
        }

        protected abstract Task PlayTrackCoreAsync(BaseItemDto track);

        [RelayCommand]
        private void PlayNext(BaseItemDto track)
        {
            PlaybackQueueService.AddToQueueNext(track);
            Logger.LogInformation("Added track '{TrackName}' to play next", track.Name);
        }

        [RelayCommand]
        private void AddToQueue(BaseItemDto track)
        {
            PlaybackQueueService.AddToQueue(track);
            Logger.LogInformation("Added track '{TrackName}' to queue", track.Name);
        }

        [RelayCommand]
        private Task StartInstantMixAsync(BaseItemDto track)
        {
            return MusicPlayerService.PlayInstantMixAsync(track);
        }
    }
}
