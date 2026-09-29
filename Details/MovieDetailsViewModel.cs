using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Gelatinarm.Playback;
using Gelatinarm.Player;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Ui;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;

namespace Gelatinarm.Details
{
    public partial class MovieDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        [ObservableProperty] private ObservableCollection<AudioTrack> _availableAudioTracks = new();

        [ObservableProperty] private ObservableCollection<SubtitleTrack> _availableSubtitleTracks = new();

        [ObservableProperty] private ObservableCollection<MovieVersion> _availableVersions = new();

        [ObservableProperty] private ObservableCollection<BaseItemPerson> _cast = new();

        [ObservableProperty] private float? _communityRating;

        [ObservableProperty] private float? _criticRating;

        [ObservableProperty] private string _director;

        [ObservableProperty] private string _genresText;

        [ObservableProperty] private bool _hasCast;

        [ObservableProperty] private bool _hasSimilarItems;

        [ObservableProperty] private string _collectionSectionTitle;

        [ObservableProperty] private bool _hasCollectionSiblings;

        [ObservableProperty] private ObservableCollection<BaseItemDto> _collectionSiblings = new();

        [ObservableProperty] private AudioTrack _selectedAudioTrack;

        [ObservableProperty] private SubtitleTrack _selectedSubtitleTrack;

        [ObservableProperty] private MovieVersion _selectedVersion;

        // The pickers follow the chosen version: from the streams the item already carries, or,
        // for a version whose source lists none, from a fetch of that version
        partial void OnSelectedVersionChanged(MovieVersion value)
        {
            if (value == null)
            {
                return;
            }

            if (value.SourceInfo?.MediaStreams is { Count: > 0 })
            {
                PopulateTracks(value.SourceInfo.MediaStreams);
            }
            else if (AvailableVersions.Count == 1 && CurrentItem.MediaStreams is { Count: > 0 })
            {
                PopulateTracks(CurrentItem.MediaStreams);
            }
            else
            {
                FireAndForget(() => LoadVersionDetailsAsync(value));
            }
        }

        [ObservableProperty] private ObservableCollection<BaseItemDto> _similarItems = new();

        // The height's label, which names a version when its stream has no display title
        private string _videoQuality;

        [ObservableProperty] private string _writers;

        // The collection reverse lookup has no generated SDK builder, so it is sent
        // through the adapter directly. See LoadCollectionSiblingsAsync.
        private readonly JellyfinRequestAdapter _requestAdapter;

        public MovieDetailsViewModel(
            ILogger<MovieDetailsViewModel> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IUserDataService userDataService,
            JellyfinRequestAdapter requestAdapter)
            : base(logger, apiClient, userProfileService, navigationService, imageLoadingService,
                userDataService)
        {
            _requestAdapter = requestAdapter;
        }

        public override Task PlayAsync()
        {
            NavigateToPlayer(CanResume ? CurrentItem?.UserData?.PlaybackPositionTicks : null);
            return Task.CompletedTask;
        }

        public override Task RestartAsync()
        {
            NavigateToPlayer(0);
            return Task.CompletedTask;
        }

        /// <summary>
        ///     Opens the player with the chosen version, audio track and subtitle
        /// </summary>
        private void NavigateToPlayer(long? startPositionTicks)
        {
            if (CurrentItem == null)
            {
                return;
            }

            var mediaSourceId = SelectedVersion?.SourceInfo?.Id;
            var playbackParams = new MediaPlaybackParams
            {
                Item = CurrentItem,
                MediaSourceId = string.IsNullOrEmpty(mediaSourceId) ? null : mediaSourceId,
                AudioStreamIndex = SelectedAudioTrack?.ServerStreamIndex,
                SubtitleStreamIndex =
                    SelectedSubtitleTrack?.IsNoneOption == true ? -1 : SelectedSubtitleTrack?.ServerStreamIndex,
                StartPositionTicks = startPositionTicks
            };

            NavigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
        }

        protected override async Task LoadAdditionalDataAsync()
        {
            LoadPrimaryImage();
            FireAndForget(() => LoadBackdropImageAsync(CurrentItem));

            ClampOverview(CurrentItem.Overview, 300, 80);

            if (CurrentItem.ProductionYear.HasValue)
            {
                Year = CurrentItem.ProductionYear.Value.ToString();
            }
            else if (CurrentItem.PremiereDate.HasValue)
            {
                Year = CurrentItem.PremiereDate.Value.Year.ToString();
            }

            if (CurrentItem.RunTimeTicks.HasValue)
            {
                Runtime = TimeFormattingHelper.FormatDuration(TimeSpan.FromTicks(CurrentItem.RunTimeTicks.Value));
            }

            if (!string.IsNullOrEmpty(CurrentItem.OfficialRating))
            {
                Rating = CurrentItem.OfficialRating;
            }

            CommunityRating = CurrentItem.CommunityRating;
            CriticRating = CurrentItem.CriticRating;

            LoadCastAndCrew();
            HasCast = Cast.Count > 0;
            GenresText = CurrentItem.Genres is { Count: > 0 } ? string.Join(", ", CurrentItem.Genres) : string.Empty;

            DetermineVideoQuality();

            LoadMediaStreams();

            await Task.WhenAll(LoadSimilarMoviesAsync(), LoadCollectionSiblingsAsync());
        }

        private void LoadCastAndCrew()
        {
            Cast.ReplaceAll(CurrentItem.People?.Where(p => p.Type == BaseItemPerson_Type.Actor).Take(20));

            var director = CurrentItem.People?.FirstOrDefault(p => p.Type == BaseItemPerson_Type.Director);
            if (director != null)
            {
                Director = director.Name;
            }

            var writers = CurrentItem.People?.Where(p => p.Type == BaseItemPerson_Type.Writer).Select(p => p.Name).ToList();
            if (writers is { Count: > 0 })
            {
                Writers = string.Join(", ", writers);
            }
        }

        private void DetermineVideoQuality()
        {
            var videoStream = CurrentItem.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
            if (videoStream?.Height != null)
            {
                _videoQuality = MediaLabels.Resolution(videoStream.Height.Value);
            }
        }

        private async Task LoadSimilarMoviesAsync()
        {
            var context = CreateErrorContext("LoadSimilarMovies");
            try
            {
                var response = await ApiClient.Items[CurrentItem.Id.Value].Similar.GetAsync(config =>
                {
                    config.QueryParameters.UserId = UserIdGuid;
                    config.QueryParameters.Limit = 12;
                    config.QueryParameters.Fields = new[] { ItemFields.PrimaryImageAspectRatio };
                });

                await RunOnUIThreadAsync(() =>
                {
                    SimilarItems.ReplaceAll(response?.Items);
                    HasSimilarItems = SimilarItems.Count > 0;
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private async Task LoadCollectionSiblingsAsync()
        {
            if (!UserIdGuid.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("LoadCollectionSiblings");
            try
            {
                // Ask the server which collections contain this item. Do not try to find
                // them in the item's ancestors: Jellyfin stores collection membership as
                // LinkedChildren on the BoxSet, so an item's ancestors are only its
                // library folders and a BoxSet never appears there.
                // Jellyfin exposes this reverse lookup at GET /Items/{itemId}/Collections,
                // but Jellyfin.Sdk 2025.10.21 generates no request builder for it, so the
                // call goes through the SDK adapter to reuse its base URL and auth.
                var collectionsRequest = new RequestInformation(Method.GET,
                    "{+baseurl}/Items/{itemId}/Collections{?userId}",
                    new Dictionary<string, object>
                    {
                        { "baseurl", _requestAdapter.BaseUrl },
                        { "itemId", CurrentItem.Id.Value.ToString("N") }
                    });
                collectionsRequest.QueryParameters.Add("userId", UserIdGuid.Value.ToString("N"));

                var collections = await _requestAdapter
                    .SendAsync(collectionsRequest, BaseItemDtoQueryResult.CreateFromDiscriminatorValue)
                    .ConfigureAwait(false);

                var collection = collections?.Items?.FirstOrDefault();
                if (collection?.Id == null)
                {
                    Logger.LogDebug("Collection row skipped - '{CurrentItemName}' is in no collection", CurrentItem.Name);
                    return;
                }

                var response = await ApiClient.Items.GetAsync(config =>
                {
                    config.QueryParameters.ParentId = collection.Id.Value;
                    config.QueryParameters.UserId = UserIdGuid.Value;
                    config.QueryParameters.Fields = new[] { ItemFields.PrimaryImageAspectRatio };
                    config.QueryParameters.SortBy = new[] { ItemSortBy.ProductionYear };
                });

                await RunOnUIThreadAsync(() =>
                {
                    CollectionSiblings.ReplaceAll(response?.Items?.Where(i => i.Id != CurrentItem.Id));

                    CollectionSectionTitle = $"More from {collection.Name}";
                    HasCollectionSiblings = CollectionSiblings.Count > 0;
                    Logger.LogDebug(
                        "Collection row - found collection '{CollectionName}' with " +
                        "{CollectionSiblingsCount} sibling(s); row visible: {HasCollectionSiblings}", collection.Name, CollectionSiblings.Count, HasCollectionSiblings);
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private void LoadMediaStreams()
        {
            AvailableVersions.Clear();
            AvailableAudioTracks.Clear();
            AvailableSubtitleTracks.Clear();

            if (CurrentItem.MediaSources is { Count: > 0 })
            {
                foreach (var source in CurrentItem.MediaSources)
                {
                    var videoStream = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
                    AvailableVersions.Add(new MovieVersion
                    {
                        Id = source.Id ?? CurrentItem.Id.Value.ToString(),
                        Name = GetQualityDisplayName(videoStream, source),
                        SourceInfo = source
                    });
                }
            }
            else
            {
                var videoStream = CurrentItem.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
                AvailableVersions.Add(new MovieVersion
                {
                    Id = CurrentItem.Id.Value.ToString(),
                    Name = videoStream?.DisplayTitle ?? _videoQuality ?? "Direct Play"
                });
            }

            SelectedVersion = AvailableVersions[0];
        }

        /// <summary>
        ///     Fills the audio and subtitle pickers from one version's streams, selecting the
        ///     file's default audio and subtitle ("None" when no subtitle is the default)
        /// </summary>
        private void PopulateTracks(IEnumerable<MediaStream> streams)
        {
            AvailableAudioTracks.ReplaceAll(AudioTrack.ListFrom(streams));
            SelectedAudioTrack = AvailableAudioTracks.FirstOrDefault(a => a.IsDefault)
                                 ?? AvailableAudioTracks.FirstOrDefault();

            AvailableSubtitleTracks.ReplaceAll(SubtitleTrack.ListFrom(streams));
            SelectedSubtitleTrack = AvailableSubtitleTracks.FirstOrDefault(s => s.IsDefault)
                                    ?? AvailableSubtitleTracks[0];
        }

        private async Task LoadVersionDetailsAsync(MovieVersion version)
        {
            try
            {
                if (!TryGetGuidFromParameter(version.Id, out var versionGuid) || !UserIdGuid.HasValue)
                {
                    Logger.LogError("Invalid ID format - Version: {VersionId}, User: {UserGuid}", version.Id, UserIdGuid);
                    return;
                }

                var item = await FetchItemAsync(versionGuid);
                if (item?.MediaStreams == null)
                {
                    return;
                }

                PopulateTracks(item.MediaStreams);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("LoadVersionDetails", ErrorCategory.User), false);
            }
        }

        private static string GetQualityDisplayName(MediaStream videoStream, MediaSourceInfo source)
        {
            if (!string.IsNullOrEmpty(videoStream?.DisplayTitle))
            {
                return videoStream.DisplayTitle;
            }

            if (!string.IsNullOrEmpty(source?.Name))
            {
                return source.Name;
            }

            return "Direct Play";
        }
    }
}
