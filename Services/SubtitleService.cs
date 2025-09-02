using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Models;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Service for managing subtitle tracks and selection
    /// </summary>
    public class SubtitleService : BaseService, ISubtitleService
    {
        private readonly IMediaControlService _mediaControlService;
        private readonly IPlaybackControlService _playbackControlService;
        private readonly IPreferencesService _preferencesService;
        private readonly IAuthenticationService _authService;
        private readonly Jellyfin.Sdk.JellyfinApiClient _apiClient;
        private MediaSourceInfo _currentMediaSource;
        private SubtitleTrack _currentSubtitle;
        private volatile bool _isDisposed = false;

        private MediaPlayer _mediaPlayer;
        private MediaPlaybackParams _playbackParams;
        private List<SubtitleTrack> _subtitleTracks;

        public SubtitleService(
            ILogger<SubtitleService> logger,
            IPlaybackControlService playbackControlService,
            IPreferencesService preferencesService,
            IMediaControlService mediaControlService,
            IAuthenticationService authService,
            Jellyfin.Sdk.JellyfinApiClient apiClient) : base(logger)
        {
            _playbackControlService =
                playbackControlService ?? throw new ArgumentNullException(nameof(playbackControlService));
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
            _mediaControlService = mediaControlService ?? throw new ArgumentNullException(nameof(mediaControlService));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        }

        public event EventHandler<SubtitleTrack> SubtitleChanged;

        public async Task InitializeAsync(MediaPlayer mediaPlayer, MediaPlaybackParams playbackParams)
        {
            _mediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer));
            _playbackParams = playbackParams ?? throw new ArgumentNullException(nameof(playbackParams));

            await Task.CompletedTask;
        }


        public async Task<List<SubtitleTrack>> GetSubtitleTracksAsync(PlaybackInfoResponse playbackInfo)
        {
            try
            {
                _subtitleTracks = new List<SubtitleTrack>
                {
                    new()
                    {
                        ServerStreamIndex = -1,
                        Language = MediaConstants.SUBTITLE_NONE_OPTION,
                        DisplayTitle = MediaConstants.SUBTITLE_NONE_OPTION,
                        IsNoneOption = true,
                        IsDefault = false
                    }
                };

                if (playbackInfo?.MediaSources?.FirstOrDefault()?.MediaStreams != null)
                {
                    _currentMediaSource = playbackInfo.MediaSources.FirstOrDefault();
                    var subtitleStreams = _currentMediaSource.MediaStreams
                        .Where(s => s.Type == MediaStream_Type.Subtitle)
                        .ToList();

                    foreach (var stream in subtitleStreams)
                    {
                        var track = new SubtitleTrack
                        {
                            ServerStreamIndex = stream.Index ?? 0,
                            Language = stream.Language ?? "Unknown",
                            DisplayTitle = GenerateSubtitleDisplayTitle(stream),
                            IsDefault = stream.IsDefault ?? false,
                            IsNoneOption = false
                        };

                        _subtitleTracks.Add(track);
                    }
                }

                if (_playbackParams.SubtitleStreamIndex.HasValue)
                {
                    _currentSubtitle = _subtitleTracks.FirstOrDefault(s =>
                        s.ServerStreamIndex == _playbackParams.SubtitleStreamIndex.Value);
                }
                else
                {
                    _currentSubtitle = _subtitleTracks.FirstOrDefault(s => s.IsDefault) ??
                                       _subtitleTracks.FirstOrDefault();
                }

                return await Task.FromResult(_subtitleTracks);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to get subtitle tracks");
                return new List<SubtitleTrack>();
            }
        }

        public async Task ChangeSubtitleTrackAsync(SubtitleTrack subtitle)
        {
            try
            {
                if (subtitle == null || subtitle == _currentSubtitle)
                {
                    return;
                }

                Logger.LogInformation($"Changing subtitle to: {subtitle.DisplayTitle}");

                if (_playbackParams != null)
                {
                    _playbackParams.SubtitleStreamIndex = subtitle.IsNoneOption ? -1 : subtitle.ServerStreamIndex;
                }

                // For burned-in subtitles, we always need to reload the stream
                // whether enabling or disabling subtitles
                await ReloadMediaWithSubtitle(subtitle);
                _currentSubtitle = subtitle;
                SubtitleChanged?.Invoke(this, subtitle);

                Logger.LogInformation($"Subtitle changed successfully to: {subtitle.DisplayTitle}");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to change subtitle track");
                throw;
            }
        }

        public async Task DisableSubtitlesAsync()
        {
            try
            {
                Logger.LogInformation("Disabling subtitles");

                _currentSubtitle = _subtitleTracks.FirstOrDefault(s => s.IsNoneOption);
                SubtitleChanged?.Invoke(this, _currentSubtitle);

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to disable subtitles");
                throw;
            }
        }

        public SubtitleTrack GetCurrentSubtitle()
        {
            return _currentSubtitle;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _subtitleTracks?.Clear();
            _isDisposed = true;
        }

        private string GenerateSubtitleDisplayTitle(MediaStream stream)
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

            var flags = new List<string>();
            if (stream.IsDefault ?? false)
            {
                flags.Add("Default");
            }

            if (stream.IsForced ?? false)
            {
                flags.Add("Forced");
            }

            if (stream.IsExternal ?? false)
            {
                flags.Add("External");
            }

            if (flags.Any())
            {
                parts.Add($"({string.Join(", ", flags)})");
            }

            if (!parts.Any())
            {
                parts.Add($"Subtitle Track {stream.Index}");
            }

            return string.Join(" ", parts);
        }

        private bool IsEmbeddedSubtitle(SubtitleTrack subtitle)
        {
            if (_currentMediaSource?.MediaStreams == null)
            {
                return true;
            }

            var stream = _currentMediaSource.MediaStreams
                .FirstOrDefault(s => s.Index == subtitle.ServerStreamIndex);

            return stream == null || !(stream.IsExternal ?? false);
        }

        private async Task ReloadMediaWithSubtitle(SubtitleTrack subtitle)
        {
            Logger.LogInformation($"Reloading media for subtitle change to: {subtitle.DisplayTitle}");

            // Update local playback params
            var subtitleIndex = subtitle.IsNoneOption ? -1 : subtitle.ServerStreamIndex;
            _playbackParams.SubtitleStreamIndex = subtitleIndex;

            // Use shared restart method, passing the subtitle index
            // -1 means no subtitles (for "None" option)
            await _playbackControlService.RestartPlaybackWithCurrentPositionAsync(
                restartReason: $"subtitle change to {subtitle.DisplayTitle}",
                subtitleStreamIndex: subtitleIndex);
        }


    }
}
