using System;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Gelatinarm.Services
{
    public interface IPlaybackRestartService
    {
        Task RestartPlaybackAsync(PlaybackRestartRequest request);
    }

    public sealed class PlaybackRestartService : IPlaybackRestartService
    {
        private readonly ILogger<PlaybackRestartService> _logger;
        private readonly IMediaControlService _mediaControlService;
        private readonly IMediaPlaybackService _mediaPlaybackService;

        public PlaybackRestartService(
            ILogger<PlaybackRestartService> logger,
            IMediaControlService mediaControlService,
            IMediaPlaybackService mediaPlaybackService)
        {
            _logger = logger;
            _mediaControlService = mediaControlService;
            _mediaPlaybackService = mediaPlaybackService;
        }

        public async Task RestartPlaybackAsync(PlaybackRestartRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.MediaPlayer?.PlaybackSession == null)
            {
                _logger.LogWarning($"Cannot restart playback for {request.RestartReason} - media player not initialized");
                return;
            }

            if (request.ItemToReload == null)
            {
                _logger.LogError($"Cannot restart playback for {request.RestartReason} - no current item available");
                throw new InvalidOperationException("No current item available for playback restart");
            }

            // Save current position and state
            var currentPosition = request.MediaPlayer.PlaybackSession.Position;
            var wasPlaying = request.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

            _logger.LogInformation(
                $"Restarting playback for {request.RestartReason}. Current position: {currentPosition:hh\\:mm\\:ss\\.fff}, " +
                $"AudioStreamIndex={request.AudioStreamIndex?.ToString() ?? request.PlaybackParams?.AudioStreamIndex?.ToString() ?? "null"}, " +
                $"SubtitleStreamIndex={request.SubtitleStreamIndex?.ToString() ?? request.PlaybackParams?.SubtitleStreamIndex?.ToString() ?? "null"}");

            await ReportPlaybackStopAsync(request.PlaySessionId, currentPosition.Ticks, request.RestartReason);

            _mediaControlService.Stop();

            if (request.PlaybackParams != null)
            {
                request.PlaybackParams.StartPositionTicks = currentPosition.Ticks;

                if (request.AudioStreamIndex.HasValue)
                {
                    request.PlaybackParams.AudioStreamIndex = request.AudioStreamIndex.Value;
                }

                if (request.SubtitleStreamIndex.HasValue)
                {
                    request.PlaybackParams.SubtitleStreamIndex = request.SubtitleStreamIndex.Value;
                }
            }

            var playbackInfo = await request.GetPlaybackInfoAsync(request.ItemToReload, request.MaxBitrate);
            var mediaSource = await request.CreateMediaSourceAsync(playbackInfo);
            await request.StartPlaybackAsync(mediaSource, currentPosition.Ticks);

            await ReportPlaybackStartAsync(request.PlaySessionId, currentPosition.Ticks, request.RestartReason);

            if (wasPlaying)
            {
                _mediaControlService.Play();
            }

            _logger.LogInformation($"Successfully restarted playback for {request.RestartReason}");
        }

        private async Task ReportPlaybackStopAsync(string playSessionId, long positionTicks, string restartReason)
        {
            if (string.IsNullOrEmpty(playSessionId))
            {
                return;
            }

            try
            {
                if (_mediaPlaybackService is IMediaSessionService sessionService)
                {
                    await sessionService.ReportPlaybackStoppedAsync(playSessionId, positionTicks);
                    _logger.LogInformation($"Reported playback stop for {restartReason}");
                }
                else
                {
                    _logger.LogWarning("MediaPlaybackService does not implement IMediaSessionService - cannot report playback stop");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to report playback stop before restart");
            }
        }

        private async Task ReportPlaybackStartAsync(string playSessionId, long positionTicks, string restartReason)
        {
            if (string.IsNullOrEmpty(playSessionId))
            {
                return;
            }

            if (_mediaPlaybackService is IMediaSessionService sessionService)
            {
                await sessionService.ReportPlaybackStartAsync(playSessionId, positionTicks);
                _logger.LogInformation($"Reported playback start for {restartReason} with session {playSessionId}");
            }
            else
            {
                _logger.LogWarning("MediaPlaybackService does not implement IMediaSessionService - cannot report playback start");
            }
        }
    }

    public sealed class PlaybackRestartRequest
    {
        public string RestartReason { get; set; } = "stream change";
        public MediaPlayer MediaPlayer { get; set; }
        public MediaPlaybackParams PlaybackParams { get; set; }
        public BaseItemDto ItemToReload { get; set; }
        public string PlaySessionId { get; set; }
        public int? AudioStreamIndex { get; set; }
        public int? SubtitleStreamIndex { get; set; }
        public int? MaxBitrate { get; set; }
        public Func<BaseItemDto, int?, Task<PlaybackInfoResponse>> GetPlaybackInfoAsync { get; set; }
        public Func<PlaybackInfoResponse, Task<MediaSource>> CreateMediaSourceAsync { get; set; }
        public Func<MediaSource, long?, Task> StartPlaybackAsync { get; set; }
    }
}
