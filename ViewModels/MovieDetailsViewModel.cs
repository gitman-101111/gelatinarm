using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.Views;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     Movie details view model
    /// </summary>
    public partial class MovieDetailsViewModel : DetailsViewModel<BaseItemDto>
    {
        [ObservableProperty] private ObservableCollection<AudioTrack> _availableAudioTracks = new();

        [ObservableProperty] private ObservableCollection<SubtitleTrack> _availableSubtitleTracks = new();

        [ObservableProperty] private ObservableCollection<MovieVersion> _availableVersions = new();

        [ObservableProperty] private ObservableCollection<BaseItemPerson> _cast = new();

        [ObservableProperty] private float? _communityRating;

        [ObservableProperty] private float? _criticRating;

        [ObservableProperty] private string _director;

        [ObservableProperty] private ObservableCollection<string> _genres = new();

        [ObservableProperty] private bool _hasCast;

        [ObservableProperty] private bool _hasSimilarItems;

        [ObservableProperty] private string _releaseDate;

        [ObservableProperty] private AudioTrack _selectedAudioTrack;

        [ObservableProperty] private SubtitleTrack _selectedSubtitleTrack;

        [ObservableProperty] private MovieVersion _selectedVersion;

        partial void OnSelectedVersionChanged(MovieVersion value)
        {
            if (value != null)
            {
                _ = LoadVersionDetailsAsync(value);
            }
        }

        [ObservableProperty] private ObservableCollection<BaseItemDto> _similarItems = new();

        [ObservableProperty] private ObservableCollection<string> _studios = new();

        [ObservableProperty] private string _videoQuality;

        [ObservableProperty] private string _writers;

        public MovieDetailsViewModel(
            ILogger<MovieDetailsViewModel> logger,
            JellyfinApiClient apiClient,
            IUserProfileService userProfileService,
            INavigationService navigationService,
            IImageLoadingService imageLoadingService,
            IMediaPlaybackService mediaPlaybackService,
            IUserDataService userDataService)
            : base(logger, apiClient, userProfileService, navigationService, imageLoadingService, mediaPlaybackService,
                userDataService)
        {
        }

        /// <summary>
        ///     Override PlayAsync to pass selected audio/subtitle tracks
        /// </summary>
        public override async Task PlayAsync()
        {
            if (CurrentItem == null)
            {
                return;
            }

            var context = CreateErrorContext("PlayMedia", ErrorCategory.Media);
            try
            {
                // Only pass MediaSourceId if it's different from the item ID (indicates a specific version)
                string mediaSourceId = null;
                if (SelectedVersion?.SourceInfo != null && !string.IsNullOrEmpty(SelectedVersion.SourceInfo.Id))
                {
                    mediaSourceId = SelectedVersion.SourceInfo.Id;
                }

                var playbackParams = new MediaPlaybackParams
                {
                    Item = CurrentItem,
                    ItemId = CurrentItem.Id?.ToString(),
                    MediaSourceId = mediaSourceId,
                    AudioStreamIndex = SelectedAudioTrack?.ServerStreamIndex,
                    SubtitleStreamIndex = SelectedSubtitleTrack?.IsNoneOption == true ? -1 : SelectedSubtitleTrack?.ServerStreamIndex,
                    StartPositionTicks = CanResume && CurrentItem.UserData?.PlaybackPositionTicks.HasValue == true
                        ? CurrentItem.UserData.PlaybackPositionTicks
                        : null
                };

                NavigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, true);
            }
        }

        /// <summary>
        ///     Override ResumeAsync to pass selected audio/subtitle tracks
        /// </summary>
        public override async Task ResumeAsync()
        {
            await PlayAsync(); // ResumeAsync is the same as PlayAsync with resume position
        }

        /// <summary>
        ///     Override RestartAsync to pass selected audio/subtitle tracks
        /// </summary>
        public override async Task RestartAsync()
        {
            if (CurrentItem == null)
            {
                return;
            }

            var context = CreateErrorContext("RestartMedia", ErrorCategory.Media);
            try
            {
                // Only pass MediaSourceId if it's different from the item ID (indicates a specific version)
                string mediaSourceId = null;
                if (SelectedVersion?.SourceInfo != null && !string.IsNullOrEmpty(SelectedVersion.SourceInfo.Id))
                {
                    mediaSourceId = SelectedVersion.SourceInfo.Id;
                }

                var playbackParams = new MediaPlaybackParams
                {
                    Item = CurrentItem,
                    ItemId = CurrentItem.Id?.ToString(),
                    MediaSourceId = mediaSourceId,
                    AudioStreamIndex = SelectedAudioTrack?.ServerStreamIndex,
                    SubtitleStreamIndex = SelectedSubtitleTrack?.IsNoneOption == true ? -1 : SelectedSubtitleTrack?.ServerStreamIndex,
                    StartPositionTicks = 0 // Start from beginning
                };

                NavigationService.Navigate(typeof(MediaPlayerPage), playbackParams);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, true);
            }
        }

        /// <summary>
        ///     Load additional movie-specific data
        /// </summary>
        protected override async Task LoadAdditionalDataAsync()
        {
            if (CurrentItem == null)
            {
                return;
            }

            // LoadAdditionalDataAsync started

            // Set year from production year or premiere date
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
                var runtime = TimeSpan.FromTicks(CurrentItem.RunTimeTicks.Value);
                if (runtime.TotalHours >= 1)
                {
                    if (runtime.Minutes == 0)
                    {
                        Runtime = $"{(int)runtime.TotalHours}h";
                    }
                    else
                    {
                        Runtime = $"{(int)runtime.TotalHours}h {runtime.Minutes}min";
                    }
                }
                else
                {
                    Runtime = $"{runtime.Minutes}min";
                }
            }
            if (!string.IsNullOrEmpty(CurrentItem.OfficialRating))
            {
                Rating = CurrentItem.OfficialRating;
            }

            // Set community rating
            if (CurrentItem.CommunityRating.HasValue)
            {
                CommunityRating = CurrentItem.CommunityRating.Value;
                // Community rating set
            }
            else if (CurrentItem.CriticRating.HasValue)
            {
                // Use critic rating as fallback
                CriticRating = CurrentItem.CriticRating.Value;
                CommunityRating = CriticRating; // Use critic rating if no community rating
                // Using critic rating as fallback
            }

            // No rating available
            // Update progress bar
            UpdateProgressBar();

            // Load cast and crew
            LoadCastAndCrew();
            HasCast = Cast.Count > 0; LoadGenres(); LoadStudios();

            // Format release date
            if (CurrentItem.PremiereDate.HasValue)
            {
                ReleaseDate = CurrentItem.PremiereDate.Value.ToString("MMMM d, yyyy");
            }

            // Determine video quality
            DetermineVideoQuality();

            // Load media streams
            LoadMediaStreams();

            // Load similar movies
            await LoadSimilarMoviesAsync();

            // LoadAdditionalDataAsync completed
        }

        private void LoadCastAndCrew()
        {
            Cast.Clear();

            if (CurrentItem.People?.Any() == true)
            {
                var actors = CurrentItem.People
                    .Where(p => p.Type == BaseItemPerson_Type.Actor)
                    .Take(20);

                foreach (var actor in actors)
                {
                    Cast.Add(actor);
                }
                var director = CurrentItem.People.FirstOrDefault(p => p.Type == BaseItemPerson_Type.Director);
                if (director != null)
                {
                    Director = director.Name;
                }
                var writers = CurrentItem.People
                    .Where(p => p.Type == BaseItemPerson_Type.Writer)
                    .Select(p => p.Name)
                    .ToList();

                if (writers.Any())
                {
                    Writers = string.Join(", ", writers);
                    // Writers set
                }
                // No writers found
            }
        }

        private void LoadGenres()
        {
            Genres.Clear();

            if (CurrentItem.Genres?.Any() == true)
            {
                foreach (var genre in CurrentItem.Genres)
                {
                    Genres.Add(genre);
                }
            }

            // Update genres text
            GenresText = Genres.Count > 0 ? string.Join(", ", Genres) : string.Empty;
        }

        private void LoadStudios()
        {
            Studios.Clear();

            if (CurrentItem.Studios?.Any() == true)
            {
                foreach (var studio in CurrentItem.Studios)
                {
                    Studios.Add(studio.Name);
                }
            }
        }

        private void DetermineVideoQuality()
        {
            if (CurrentItem.MediaStreams?.Any() == true)
            {
                var videoStream = CurrentItem.MediaStreams.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
                if (videoStream?.Height != null)
                {
                    VideoQuality = videoStream.Height.Value switch
                    {
                        >= 2160 => "4K",
                        >= 1080 => "1080p",
                        >= 720 => "720p",
                        >= 480 => "480p",
                        _ => $"{videoStream.Height}p"
                    };
                }
            }
        }

        private async Task LoadSimilarMoviesAsync()
        {
            if (CurrentItem?.Id == null)
            {
                return;
            }

            var context = CreateErrorContext("LoadSimilarMovies");
            try
            {
                var response = await ApiClient.Items[CurrentItem.Id.Value].Similar.GetAsync(config =>
                {
                    config.QueryParameters.UserId = UserIdGuid;
                    config.QueryParameters.Limit = 12;
                    config.QueryParameters.Fields = new[] { ItemFields.PrimaryImageAspectRatio };
                });

                // Ensure UI updates happen on UI thread
                await RunOnUIThreadAsync(() =>
                {
                    SimilarItems.Clear();

                    if (response?.Items != null)
                    {
                        foreach (var item in response.Items)
                        {
                            SimilarItems.Add(item);
                        }

                        HasSimilarItems = SimilarItems.Count > 0;
                    }
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private void UpdateProgressBar()
        {
            if (CurrentItem?.UserData != null && CurrentItem.UserData.PlaybackPositionTicks > 0 &&
                CurrentItem.RunTimeTicks > 0)
            {
                ShowProgress = true;
                var percentage = (double)CurrentItem.UserData.PlaybackPositionTicks.Value /
                    CurrentItem.RunTimeTicks.Value * 100;
                ProgressWidth = percentage * 5; // 500 max width * percentage / 100

                var position = TimeSpan.FromTicks(CurrentItem.UserData.PlaybackPositionTicks ?? 0);
                var duration = TimeSpan.FromTicks(CurrentItem.RunTimeTicks ?? 0);

                if (position.TotalHours >= 1 || duration.TotalHours >= 1)
                {
                    ProgressText = $"{position:h\\:mm\\:ss} / {duration:h\\:mm\\:ss} • {percentage:F0}% watched";
                }
                else
                {
                    ProgressText = $"{position:mm\\:ss} / {duration:mm\\:ss} • {percentage:F0}% watched";
                }

                // Don't override PlayButtonText here - it's already set correctly by UpdatePlaybackState in the base class
            }
            else
            {
                ShowProgress = false;
                PlayButtonText = "Play";
            }
        }

        private void LoadMediaStreams()
        {
            // Clear existing collections
            AvailableVersions.Clear();
            AvailableAudioTracks.Clear();
            AvailableSubtitleTracks.Clear();

            Logger.LogInformation($"LoadMediaStreams called - CurrentItem: {CurrentItem?.Name}");
            Logger.LogInformation($"CurrentItem.MediaSources count: {CurrentItem?.MediaSources?.Count ?? 0}");
            Logger.LogInformation($"CurrentItem.MediaStreams count: {CurrentItem?.MediaStreams?.Count ?? 0}");

            if (CurrentItem != null)
            {
                // Check if we have MediaSources
                if (CurrentItem.MediaSources?.Any() == true)
                {
                    // Use MediaSources for quality options
                    foreach (var source in CurrentItem.MediaSources)
                    {
                        var videoStream = source.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);

                        // Extract quality info from video stream
                        var displayName = GetQualityDisplayName(videoStream, source);

                        var version = new MovieVersion
                        {
                            Id = source.Id ?? CurrentItem.Id.Value.ToString(),
                            Name = displayName,
                            SourceInfo = source
                        };
                        AvailableVersions.Add(version);
                    }
                }
                else if (CurrentItem.MediaStreams?.Any() == true)
                {
                    // Fallback to using MediaStreams directly
                    var videoStream = CurrentItem.MediaStreams.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
                    var displayName = videoStream?.DisplayTitle ?? VideoQuality ?? "Direct Play";

                    var defaultVersion = new MovieVersion
                    {
                        Id = CurrentItem.Id.Value.ToString(),
                        Name = displayName,
                        SourceInfo = null
                    };
                    AvailableVersions.Add(defaultVersion);
                }
                else
                {
                    // Final fallback
                    var defaultVersion = new MovieVersion
                    {
                        Id = CurrentItem.Id.Value.ToString(),
                        Name = VideoQuality ?? "Direct Play",
                        SourceInfo = null
                    };
                    AvailableVersions.Add(defaultVersion);
                }

                // Select the first version as default
                if (AvailableVersions.Any())
                {
                    SelectedVersion = AvailableVersions.FirstOrDefault();
                }
            }

            // Try to get MediaStreams - prefer CurrentItem.MediaStreams as it's more reliably populated
            List<MediaStream> mediaStreams = null;
            if (CurrentItem?.MediaStreams != null && CurrentItem.MediaStreams.Any())
            {
                mediaStreams = CurrentItem.MediaStreams;
                Logger.LogInformation($"Using MediaStreams from CurrentItem: {mediaStreams.Count} streams");
            }
            else if (SelectedVersion?.SourceInfo?.MediaStreams != null && SelectedVersion.SourceInfo.MediaStreams.Any())
            {
                mediaStreams = SelectedVersion.SourceInfo.MediaStreams;
                Logger.LogInformation($"Using MediaStreams from selected version: {mediaStreams.Count} streams");
            }
            else
            {
                Logger.LogWarning("No MediaStreams available from either CurrentItem or selected version");
                return;
            }


            // Load audio tracks
            var audioStreams = mediaStreams.Where(s => s.Type == MediaStream_Type.Audio).ToList();
            if (audioStreams.Any())
            {
                foreach (var stream in audioStreams)
                {
                    var language = stream.Language ?? "Unknown";
                    var codec = stream.Codec ?? "Unknown";
                    var channels = stream.Channels ?? 2;

                    var channelLayout = channels switch
                    {
                        1 => "Mono",
                        2 => "Stereo",
                        6 => "5.1",
                        8 => "7.1",
                        _ => $"{channels}ch"
                    };

                    var displayCodec = codec.ToLower() switch
                    {
                        "ac3" => "Dolby Digital",
                        "eac3" => "Dolby Digital+",
                        "truehd" => "Dolby TrueHD",
                        "dts" => "DTS",
                        _ => codec.ToUpper()
                    };

                    var audioTrack = new AudioTrack
                    {
                        ServerStreamIndex = stream.Index ?? 0,
                        Language = language,
                        DisplayName = $"{language} - {displayCodec} {channelLayout}",
                        IsDefault = stream.IsDefault ?? false
                    };

                    AvailableAudioTracks.Add(audioTrack);
                }

                SelectedAudioTrack = AvailableAudioTracks.FirstOrDefault(a => a.IsDefault)
                                     ?? AvailableAudioTracks.FirstOrDefault();
            }

            // Load subtitle tracks
            // Add "None" option
            var noneSubtitle = new SubtitleTrack { IsNoneOption = true, DisplayTitle = "None", ServerStreamIndex = -1 };
            AvailableSubtitleTracks.Add(noneSubtitle);

            var subtitleStreams = mediaStreams.Where(s => s.Type == MediaStream_Type.Subtitle).ToList();
            if (subtitleStreams.Any())
            {
                foreach (var stream in subtitleStreams)
                {
                    var language = stream.Language ?? "Unknown";
                    var title = stream.Title ?? "";
                    var isForced = stream.IsForced ?? false;
                    var isDefault = stream.IsDefault ?? false;

                    var displayTitle = language;
                    if (!string.IsNullOrEmpty(title))
                    {
                        displayTitle = $"{language} - {title}";
                    }

                    if (isForced)
                    {
                        displayTitle += " (Forced)";
                    }

                    if (isDefault)
                    {
                        displayTitle += " (Default)";
                    }

                    var subtitleTrack = new SubtitleTrack
                    {
                        ServerStreamIndex = stream.Index ?? 0,
                        Language = language,
                        DisplayTitle = displayTitle,
                        IsDefault = isDefault
                    };

                    AvailableSubtitleTracks.Add(subtitleTrack);
                }
            }

            SelectedSubtitleTrack = AvailableSubtitleTracks.FirstOrDefault(s => s.IsDefault)
                                    ?? noneSubtitle;
        }

        private async Task LoadVersionDetailsAsync(MovieVersion version)
        {
            try
            {
                // Load the specific version's details
                if (!Guid.TryParse(version.Id, out var versionGuid) ||
                    !Guid.TryParse(UserProfileService.CurrentUserId, out var userGuid))
                {
                    Logger?.LogError($"Invalid ID format - Version: {version.Id}, User: {UserProfileService.CurrentUserId}");
                    return;
                }
                var item = await ApiClient.Items[versionGuid].GetAsync(config =>
                {
                    config.QueryParameters.UserId = userGuid;
                });
                if (item?.MediaStreams == null)
                {
                    return;
                }

                // Clear existing tracks
                AvailableAudioTracks.Clear();
                AvailableSubtitleTracks.Clear();

                // Load audio tracks for this version
                var audioStreams = item.MediaStreams.Where(s => s.Type == MediaStream_Type.Audio).ToList();
                if (audioStreams.Any())
                {
                    foreach (var stream in audioStreams)
                    {
                        var displayName = new List<string>();

                        if (!string.IsNullOrEmpty(stream.Language))
                        {
                            displayName.Add(stream.Language);
                        }

                        if (!string.IsNullOrEmpty(stream.Codec))
                        {
                            displayName.Add(stream.Codec.ToUpperInvariant());
                        }

                        if (stream.Channels.HasValue)
                        {
                            displayName.Add($"{stream.Channels}ch");
                        }

                        var track = new AudioTrack
                        {
                            ServerStreamIndex = stream.Index ?? 0,
                            Language = stream.Language ?? "Unknown",
                            DisplayName = string.Join(" - ", displayName),
                            IsDefault = stream.IsDefault ?? false
                        };

                        AvailableAudioTracks.Add(track);
                    }

                    SelectedAudioTrack = AvailableAudioTracks.FirstOrDefault(a => a.IsDefault)
                                         ?? AvailableAudioTracks.FirstOrDefault();
                }

                // Load subtitle tracks for this version
                var noneSubtitle = new SubtitleTrack { IsNoneOption = true, DisplayTitle = "None", ServerStreamIndex = -1 };
                AvailableSubtitleTracks.Add(noneSubtitle);

                var subtitleStreams = item.MediaStreams.Where(s => s.Type == MediaStream_Type.Subtitle).ToList();
                if (subtitleStreams.Any())
                {
                    foreach (var stream in subtitleStreams)
                    {
                        var parts = new List<string>();

                        if (!string.IsNullOrEmpty(stream.Language))
                        {
                            parts.Add(stream.Language);
                        }

                        if (!string.IsNullOrEmpty(stream.Title) && stream.Title != stream.Language)
                        {
                            parts.Add(stream.Title);
                        }

                        if (!string.IsNullOrEmpty(stream.Codec))
                        {
                            parts.Add($"[{stream.Codec.ToUpperInvariant()}]");
                        }

                        var track = new SubtitleTrack
                        {
                            ServerStreamIndex = stream.Index ?? 0,
                            Language = stream.Language ?? "Unknown",
                            DisplayTitle = string.Join(" ", parts),
                            IsDefault = stream.IsDefault ?? false,
                            IsNoneOption = false
                        };

                        AvailableSubtitleTracks.Add(track);
                    }
                }

                SelectedSubtitleTrack = AvailableSubtitleTracks.FirstOrDefault(s => s.IsDefault)
                                        ?? noneSubtitle;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to load version details for {VersionName}", version.Name);
            }
        }

        private string GetQualityDisplayName(MediaStream videoStream, MediaSourceInfo source)
        {
            // Priority: 
            // 1. Use video stream DisplayTitle if available (most accurate)
            // 2. Use source name if it looks like a quality indicator
            // 3. Fallback to source name

            if (videoStream != null && !string.IsNullOrEmpty(videoStream.DisplayTitle))
            {
                return videoStream.DisplayTitle;
            }

            // If source name contains quality info, use it
            if (!string.IsNullOrEmpty(source?.Name))
            {
                return source.Name;
            }

            // Last resort
            return "Direct Play";
        }
    }
}
