using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Gelatinarm.Playback;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Device;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.Shared.Server;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using AudioTrack = Gelatinarm.Playback.AudioTrack;

namespace Gelatinarm.Player
{
    public interface IPlaybackControlService : IDisposable
    {
        /// <summary>
        ///     Where the player's current HLS manifest starts in the item: the server restarts a
        ///     transcode at a large seek or track change, and the player then counts from zero
        /// </summary>
        TimeSpan HlsManifestOffset { get; set; }

        void Initialize(MediaPlayer mediaPlayer, MediaPlaybackParams playbackParams);

        Task<PlaybackInfoResponse> GetPlaybackInfoAsync();

        Task<MediaSource> CreateMediaSourceAsync(PlaybackInfoResponse playbackInfo);

        void StartPlayback(MediaSource mediaSource, long? startPositionTicks);

        void CancelPendingResume(string reason);

        void EndResume();

        Task HandleResumeOnPlaybackStartAsync(
            PlaybackSessionState sessionState,
            MediaPlaybackParams playbackParams,
            Func<TimeSpan> getCurrentPosition,
            Action onHlsResumeFixCompleted,
            Func<Task> onResumeFailedAsync);

        List<AudioTrack> GetAudioTracks(PlaybackInfoResponse playbackInfo);

        /// <summary>
        ///     The stream's subtitles, after a "None" entry
        /// </summary>
        List<SubtitleTrack> GetSubtitleTracks(PlaybackInfoResponse playbackInfo);

        /// <summary>
        ///     Selects the track inside a direct-played file, where the player has every track
        ///     and needs no restart. False when the stream is the server's (it carries one track)
        ///     or the player lists no such track; then <see cref="ChangeAudioTrackAsync" />.
        /// </summary>
        bool TrySelectPlayerAudioTrack(AudioTrack audioTrack);

        /// <summary>
        ///     Applies the track chosen before playback inside a direct-played file once the player
        ///     has opened it: the server approved direct play for that track, while the player
        ///     starts on the file's default.
        /// </summary>
        void SelectChosenAudioTrack();

        /// <summary>
        ///     Reopens the stream with the server applying the track
        /// </summary>
        Task ChangeAudioTrackAsync(AudioTrack audioTrack);

        Task ChangeSubtitleTrackAsync(SubtitleTrack subtitle);

        MediaSourceInfo GetCurrentMediaSource();

        /// <summary>
        ///     Pauses the player and releases the stream it was playing
        /// </summary>
        void StopStream();

        /// <summary>
        ///     Forgets <paramref name="player" /> when the player page is done with it, so this
        ///     singleton does not keep it (and its video resources) alive until the next video
        /// </summary>
        void ReleasePlayer(MediaPlayer player);

        /// <summary>
        ///     The current stream as a playback report at <paramref name="positionTicks" />; null
        ///     until a stream has been opened
        /// </summary>
        PlaybackReport CreateReport(long positionTicks);

        /// <summary>
        ///     Restarts the current item as a server stream (direct play disabled for the rest of
        ///     this item), at the player's position or, if playback never started, the resume point.
        ///     For a direct play the Xbox could not open.
        /// </summary>
        Task RetryAsServerStreamAsync();

        /// <summary>
        ///     Reopens the stream with the current tracks, at <paramref name="position" /> or where
        ///     playback is now
        /// </summary>
        Task RestartStreamAsync(string restartReason, TimeSpan? position);
    }

    public class PlaybackControlService : BaseService, IPlaybackControlService
    {
        private readonly IMediaOptimizationService _mediaOptimizationService;
        private readonly IMediaPlaybackService _mediaPlaybackService;
        private readonly IDeviceProfileService _deviceProfileService;
        private readonly IPreferencesService _preferencesService;
        private readonly IMediaSessionService _mediaSessionService;
        private readonly PlaybackResumeCoordinator _resumeCoordinator;
        private readonly PlaybackSourceResolver _sourceResolver;
        private bool _directPlayDisabledForSession;
        private MediaSourceInfo _currentMediaSource;
        private PlaybackProgressInfo_PlayMethod _playMethod = PlaybackProgressInfo_PlayMethod.DirectPlay;

        private MediaPlayer _mediaPlayer;
        private MediaSource _openStream;
        private string _playSessionId;
        private MediaPlaybackParams _playbackParams;
        private TimeSpan? _pendingResumePosition;
        private CancellationTokenSource _resumeLoop = new CancellationTokenSource();
        private ResumeProfile _resumeProfile = ResumeProfile.DirectPlay;

        public PlaybackControlService(
            ILogger<PlaybackControlService> logger,
            JellyfinApiClient apiClient,
            IAuthenticationService authService,
            IMediaPlaybackService mediaPlaybackService,
            IDeviceProfileService deviceProfileService,
            IMediaOptimizationService mediaOptimizationService,
            IPreferencesService preferencesService,
            IMediaSessionService mediaSessionService,
            IUnifiedDeviceService deviceService) : base(logger)
        {
            _mediaPlaybackService = mediaPlaybackService;
            _deviceProfileService = deviceProfileService;
            _mediaOptimizationService = mediaOptimizationService;
            _preferencesService = preferencesService;
            _mediaSessionService = mediaSessionService;
            _resumeCoordinator = new PlaybackResumeCoordinator(logger);
            _sourceResolver = new PlaybackSourceResolver(logger, apiClient, authService, deviceService);
        }

        public TimeSpan HlsManifestOffset { get; set; }

        public void Initialize(MediaPlayer mediaPlayer, MediaPlaybackParams playbackParams)
        {
            _mediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer));
            _playbackParams = playbackParams ?? throw new ArgumentNullException(nameof(playbackParams));
            _directPlayDisabledForSession = false;

            // A singleton: until this playback opens its stream, the last one's would still answer
            _currentMediaSource = null;
            _playSessionId = null;

            EndResume();
            ResetResumeTracking();
        }

        public async Task<PlaybackInfoResponse> GetPlaybackInfoAsync()
        {
            var item = _playbackParams?.Item;
            if (item?.Id.HasValue != true)
            {
                throw new InvalidOperationException("Cannot get playback info - no item with an ID");
            }

            var preferences = await _preferencesService.GetAppPreferencesAsync();

            // Stereo or less is always copied, to spare an unnecessary transcode
            var shouldAllowAudioStreamCopy = preferences.AllowAudioStreamCopy;

            var audioStream = MediaStreamHelper.AudioStream(item.MediaStreams, _playbackParams.AudioStreamIndex);
            if (audioStream?.Channels <= 2)
            {
                shouldAllowAudioStreamCopy = true;
                Logger.LogDebug(
                    "Detected {AudioStreamChannels} channel audio - forcing AllowAudioStreamCopy=true", audioStream.Channels.Value);
            }

            // Name the media source. The server applies AudioStreamIndex and
            // SubtitleStreamIndex only when MediaSourceId matches the source; without it,
            // our "no subtitle" (-1) was ignored and the server chose the file's default
            // subtitle, whose burn-in requirement ruled out direct play (reported as
            // DirectPlayError). A single-version item's source Id is its item Id.
            var mediaSourceId = _playbackParams.MediaSourceId
                                ?? item.MediaSources?.FirstOrDefault()?.Id
                                ?? item.Id.Value.ToString("N");

            // An item listed without its sources (an episode row) still carries its streams
            var source = item.MediaSources?.FirstOrDefault(s => s.Id == mediaSourceId)
                         ?? item.MediaSources?.FirstOrDefault()
                         ?? new MediaSourceInfo { MediaStreams = item.MediaStreams };
            var hevcVideoCopyExpected = _deviceProfileService.ExpectsVideoCopy(source, _playbackParams.SubtitleStreamIndex,
                preferences.MaxStreamingBitrateMbps);

            LogPlaybackInfoRequest(preferences, shouldAllowAudioStreamCopy, mediaSourceId, hevcVideoCopyExpected);

            var response = await _mediaPlaybackService.GetPlaybackInfoAsync(item.Id.Value, request =>
            {
                request.MediaSourceId = mediaSourceId;
                request.AudioStreamIndex = _playbackParams.AudioStreamIndex;
                request.SubtitleStreamIndex = _playbackParams.SubtitleStreamIndex;
                // Always send StartTimeTicks - server may use it even for HLS
                request.StartTimeTicks = _playbackParams.StartPositionTicks;
                request.EnableDirectPlay = preferences.EnableDirectPlay && !_directPlayDisabledForSession;
                request.EnableDirectStream = true;
                request.EnableTranscoding = true;
                request.AllowAudioStreamCopy = shouldAllowAudioStreamCopy;
                request.AllowVideoStreamCopy = true;
            }, hevcVideoCopyExpected);
            if (response == null)
            {
                throw new InvalidOperationException($"Failed to get playback info for {item.Name}");
            }

            _playSessionId = response.PlaySessionId;
            return response;
        }

        // A failure reaches the caller, which reports it (playback start, a track change, a restart)
        public Task<MediaSource> CreateMediaSourceAsync(PlaybackInfoResponse playbackInfo)
        {
            _currentMediaSource = _sourceResolver.SelectBestMediaSource(playbackInfo.MediaSources)
                                  ?? throw new InvalidOperationException("No valid media source found");

            return _currentMediaSource.SupportsDirectPlay == true
                ? CreateDirectPlayMediaSourceAsync(playbackInfo)
                : CreateStreamingMediaSourceAsync(playbackInfo);
        }

        public void StartPlayback(MediaSource mediaSource, long? startPositionTicks)
        {
            Logger.LogDebug("[PLAYBACK-START] MediaSource State: {MediaSourceState}, IsOpen: {MediaSourceIsOpen}, " +
                                  "Duration: {DurationTotalSeconds}s", mediaSource?.State, mediaSource?.IsOpen,
                mediaSource?.Duration?.TotalSeconds);
            var playbackItem = new MediaPlaybackItem(mediaSource);

            // TIERED RESUME APPROACH:
            // Tier 1: StartTimeTicks was already sent to server in GetPlaybackInfoAsync
            // Tier 2: If server doesn't honor it, apply client-side seek
            // Tier 3: For HLS, track manifest offset if server creates new manifest at seek position

            var shouldResume = startPositionTicks > 0;
            var resumePosition = shouldResume ? TimeSpan.FromTicks(startPositionTicks.Value) : TimeSpan.Zero;

            PrepareResumeTracking(shouldResume, resumePosition);

            var replacedStream = _openStream;
            _openStream = mediaSource;
            _mediaPlayer.Source = playbackItem;
            // A track-change restart replaces the stream; the old one is closed, not left to the GC
            if (replacedStream != null && replacedStream != mediaSource)
            {
                replacedStream.Dispose();
            }

            // AutoPlay rather than Play(): Play() before the player has initialized the streams
            // starts audio and video out of sync
            _mediaPlayer.AutoPlay = true;
        }

        private bool ApplyPendingResumePosition()
        {
            var originalTarget = _playbackParams?.StartPositionTicks.HasValue == true
                ? TimeSpan.FromTicks(_playbackParams.StartPositionTicks.Value)
                : (TimeSpan?)null;

            return _resumeCoordinator.ApplyPendingResumePosition(
                _mediaPlayer,
                ref _pendingResumePosition,
                _resumeProfile,
                originalTarget);
        }

        public Task HandleResumeOnPlaybackStartAsync(
            PlaybackSessionState sessionState,
            MediaPlaybackParams playbackParams,
            Func<TimeSpan> getCurrentPosition,
            Action onHlsResumeFixCompleted,
            Func<Task> onResumeFailedAsync)
        {
            return _resumeCoordinator.HandleResumeOnPlaybackStartAsync(new ResumeCoordinatorContext
            {
                SessionState = sessionState,
                PlaybackParams = playbackParams,
                Profile = _resumeProfile,
                GetCurrentPosition = getCurrentPosition,
                ApplyResumeIfNeeded = () =>
                {
                    var result = ApplyPendingResumePosition();
                    if (result)
                    {
                        sessionState.LastSeekTime = DateTime.UtcNow;
                    }

                    return result;
                },
                IsResumePending = () => _resumeCoordinator.IsInProgress(_pendingResumePosition.HasValue),
                GetManifestOffset = () => HlsManifestOffset,
                OnHlsResumeFixCompleted = onHlsResumeFixCompleted,
                OnResumeFailedAsync = onResumeFailedAsync,
                Cancellation = _resumeLoop.Token
            });
        }

        /// <summary>
        ///     Ends this playback's resume: its retry loop works on this shared service, so one left
        ///     running seeks the next playback's player to the old target and then reports that
        ///     resume as failed. A track-change restart keeps it; it is the same playback.
        /// </summary>
        public void EndResume()
        {
            AsyncHelper.Supersede(ref _resumeLoop);
            _pendingResumePosition = null;
        }

        public void CancelPendingResume(string reason)
        {
            _resumeCoordinator.CancelPendingResume(ref _pendingResumePosition, reason);
        }

        public List<AudioTrack> GetAudioTracks(PlaybackInfoResponse playbackInfo)
        {
            return AudioTrack.ListFrom(StreamsOf(playbackInfo));
        }

        public List<SubtitleTrack> GetSubtitleTracks(PlaybackInfoResponse playbackInfo)
        {
            return SubtitleTrack.ListFrom(StreamsOf(playbackInfo));
        }

        // From the response, not _currentMediaSource: that is only set later, by
        // CreateMediaSourceAsync, so here it is null or the previous item's source.
        private static IEnumerable<MediaStream> StreamsOf(PlaybackInfoResponse playbackInfo)
        {
            return playbackInfo?.MediaSources?.FirstOrDefault()?.MediaStreams ?? Enumerable.Empty<MediaStream>();
        }

        private void ResetResumeTracking()
        {
            _resumeProfile = ResumeProfile.DirectPlay;
            HlsManifestOffset = TimeSpan.Zero;
            _resumeCoordinator.Reset();
        }

        private Task<MediaSource> CreateDirectPlayMediaSourceAsync(PlaybackInfoResponse playbackInfo)
        {
            Logger.LogDebug("Direct Play is available - using direct HTTP streaming");

            // Clear the TranscodingUrl since we're using Direct Play
            // This ensures playback stats report "Direct playing"
            _currentMediaSource.TranscodingUrl = null;

            var directPlayUrl = _sourceResolver.BuildStreamUrl(_currentMediaSource, _playbackParams,
                playbackInfo.PlaySessionId);

            _resumeProfile = ResumeProfile.DirectPlay;
            _playMethod = PlaybackProgressInfo_PlayMethod.DirectPlay;

            return _mediaOptimizationService.CreateStreamSourceAsync(directPlayUrl);
        }

        private Task<MediaSource> CreateStreamingMediaSourceAsync(PlaybackInfoResponse playbackInfo)
        {
            var streamUrl = _sourceResolver.BuildStreamUrl(_currentMediaSource, _playbackParams,
                playbackInfo.PlaySessionId);

            // Everything that is not direct play is the server's transcoding URL: an HLS
            // remux or transcode, which Jellyfin itself reports as Transcode.
            _playMethod = PlaybackProgressInfo_PlayMethod.Transcode;

            UpdateResumeProfileFromMediaSource();

            return _mediaOptimizationService.CreateStreamSourceAsync(streamUrl);
        }

        private void UpdateResumeProfileFromMediaSource()
        {
            _resumeProfile = UrlHelper.IsHls(_currentMediaSource.TranscodingUrl)
                ? ResumeProfile.Hls
                : ResumeProfile.DirectPlay;
        }

        private void PrepareResumeTracking(bool shouldResume, TimeSpan resumePosition)
        {
            if (!shouldResume)
            {
                return;
            }

            // A track change restarts the stream within the same item; without this a
            // resume that succeeded on the previous stream would short-circuit this one.
            _resumeCoordinator.Reset();
            _pendingResumePosition = resumePosition;

            Logger.LogInformation(
                "[RESUME] To {ResumePosition:hh\\:mm\\:ss} ({Stream}): start sent to the server, seeking client-side if it " +
                "is not honoured", resumePosition, _resumeProfile.Label);
        }

        private void LogPlaybackInfoRequest(AppPreferences preferences, bool shouldAllowAudioStreamCopy, string mediaSourceId,
            bool hevcVideoCopyExpected)
        {
            Logger.LogInformation(
                "Requesting playback info: MediaSourceId {MediaSourceId}, audio {AudioStreamIndex}, subtitle " +
                "{SubtitleStreamIndex}, start {StartTime:hh\\:mm\\:ss}, EnableDirectPlay {EnableDirectPlay}{Reason}, " +
                "AllowAudioStreamCopy {AllowAudioStreamCopy} (direct stream, transcoding and video copy always allowed), " +
                "HEVC video copy expected {HevcVideoCopyExpected}",
                mediaSourceId, _playbackParams.AudioStreamIndex, _playbackParams.SubtitleStreamIndex,
                TimeSpan.FromTicks(_playbackParams.StartPositionTicks ?? 0),
                preferences.EnableDirectPlay && !_directPlayDisabledForSession,
                _directPlayDisabledForSession ? " (direct play failed earlier in this session)" : string.Empty,
                shouldAllowAudioStreamCopy, hevcVideoCopyExpected);
        }

        public bool TrySelectPlayerAudioTrack(AudioTrack audioTrack)
        {
            if (_playMethod != PlaybackProgressInfo_PlayMethod.DirectPlay
                || !SelectPlayerAudioTrack(_mediaPlayer?.Source as MediaPlaybackItem, audioTrack.ServerStreamIndex))
            {
                return false;
            }

            // The progress reports name the track the server is not applying
            _playbackParams.AudioStreamIndex = audioTrack.ServerStreamIndex;
            return true;
        }

        // On MediaOpened, not on the item's AudioTracksChanged: on the console that event never
        // fired for a two-track MKV, while the tracks were listed once the player had opened it
        public void SelectChosenAudioTrack()
        {
            if (_playMethod != PlaybackProgressInfo_PlayMethod.DirectPlay
                || !(_mediaPlayer?.Source is MediaPlaybackItem playbackItem))
            {
                return;
            }

            var tracks = playbackItem.AudioTracks;
            Logger.LogDebug("Player audio tracks at open: {TrackCount}, selected {SelectedIndex}: {Tracks}",
                tracks.Count, tracks.SelectedIndex,
                string.Join("; ", tracks.Select(t => $"{t.Language} {t.Label} {t.SupportInfo.DecoderStatus}")));
            if (_playbackParams?.AudioStreamIndex is int serverStreamIndex && serverStreamIndex >= 0)
            {
                SelectPlayerAudioTrack(playbackItem, serverStreamIndex);
            }
        }

        // The player numbers a file's audio tracks in file order; the server's stream index
        // counts every stream, so the match is the track's position among the audio streams.
        private bool SelectPlayerAudioTrack(MediaPlaybackItem playbackItem, int serverStreamIndex)
        {
            var audioStreams = _currentMediaSource?.MediaStreams?
                .Where(s => s.Type == MediaStream_Type.Audio).OrderBy(s => s.Index).ToList();
            var ordinal = audioStreams?.FindIndex(s => s.Index == serverStreamIndex) ?? -1;
            var tracks = playbackItem?.AudioTracks;
            if (ordinal < 0 || tracks == null || ordinal >= tracks.Count)
            {
                Logger.LogInformation(
                    "Server audio stream {ServerStreamIndex} is audio track {Ordinal}; the player lists {TrackCount}, so the server applies it",
                    serverStreamIndex, ordinal, tracks?.Count ?? 0);
                return false;
            }

            if (tracks.SelectedIndex != ordinal)
            {
                tracks.SelectedIndex = ordinal;
            }

            Logger.LogInformation(
                "Player audio track {Ordinal} of {TrackCount} selected for server stream {ServerStreamIndex} ({Language} {Label})",
                ordinal, tracks.Count, serverStreamIndex, tracks[ordinal].Language, tracks[ordinal].Label);
            return true;
        }

        public Task ChangeAudioTrackAsync(AudioTrack audioTrack)
        {
            return RestartPlaybackAsync(
                $"audio track change to {audioTrack.DisplayName}",
                audioStreamIndex: audioTrack.ServerStreamIndex);
        }

        public Task ChangeSubtitleTrackAsync(SubtitleTrack subtitle)
        {
            // Subtitles are burned in by the server, so enabling, changing and disabling all
            // restart the stream; -1 asks for none.
            return RestartPlaybackAsync(
                $"subtitle change to {subtitle.DisplayTitle}",
                subtitleStreamIndex: subtitle.IsNoneOption ? -1 : subtitle.ServerStreamIndex);
        }

        public PlaybackReport CreateReport(long positionTicks)
        {
            var itemId = _playbackParams?.Item?.Id;
            if (itemId == null || string.IsNullOrEmpty(_playSessionId))
            {
                return null;
            }

            return new PlaybackReport
            {
                ItemId = itemId.Value,
                MediaSourceId = _currentMediaSource?.Id,
                PlaySessionId = _playSessionId,
                PositionTicks = positionTicks,
                PlayMethod = _playMethod,
                AudioStreamIndex = _playbackParams.AudioStreamIndex,
                SubtitleStreamIndex = _playbackParams.SubtitleStreamIndex
            };
        }

        public void StopStream()
        {
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
            }

            _openStream?.Dispose();
            _openStream = null;
        }

        public void ReleasePlayer(MediaPlayer player)
        {
            // The next episode may already have initialized this service with its own player
            if (_mediaPlayer == player)
            {
                _mediaPlayer = null;
            }
        }

        public MediaSourceInfo GetCurrentMediaSource()
        {
            return _currentMediaSource;
        }

        public async Task RetryAsServerStreamAsync()
        {
            _directPlayDisabledForSession = true;

            // A file that failed to open reports position 0; restarting from that would lose
            // (and report away) the resume point.
            var playerPosition = _mediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
            var resumePosition = TimeSpan.FromTicks(_playbackParams?.StartPositionTicks ?? 0);
            var position = playerPosition > TimeSpan.FromSeconds(1) ? playerPosition : resumePosition;

            await RestartPlaybackAsync("direct play failed", positionOverride: position);
        }

        public Task RestartStreamAsync(string restartReason, TimeSpan? position)
        {
            return RestartPlaybackAsync(restartReason, positionOverride: position);
        }

        /// <summary>
        ///     Reopens the current item's stream at the current position (or
        ///     <paramref name="positionOverride" />) with the given track
        /// </summary>
        private async Task RestartPlaybackAsync(string restartReason,
            int? audioStreamIndex = null, int? subtitleStreamIndex = null, TimeSpan? positionOverride = null)
        {
            if (_mediaPlayer?.PlaybackSession == null)
            {
                Logger.LogWarning(
                    "Cannot restart playback for {RestartReason} - media player not initialized", restartReason);
                return;
            }

            if (_playbackParams?.Item == null)
            {
                throw new InvalidOperationException($"Cannot restart playback for {restartReason}: no current item");
            }

            // The player counts from the start of its manifest; the offset makes that a position
            // in the item. The new stream has no offset of its own until one is found.
            var currentPosition = positionOverride ?? _mediaPlayer.PlaybackSession.Position + HlsManifestOffset;
            HlsManifestOffset = TimeSpan.Zero;

            Logger.LogInformation(
                "Restarting playback for {RestartReason}. Current position: {CurrentPosition:hh\\:mm\\:ss\\.fff}, " +
                "AudioStreamIndex={AudioStreamIndex}, " +
                "SubtitleStreamIndex={SubtitleStreamIndex}", restartReason, currentPosition,
                audioStreamIndex ?? _playbackParams.AudioStreamIndex, subtitleStreamIndex ?? _playbackParams.SubtitleStreamIndex);

            // Stream first: the stop report ends the server's transcode, and a player still
            // running goes on asking for segments that no longer exist
            StopStream();

            await _mediaSessionService.ReportPlaybackStoppedAsync(CreateReport(currentPosition.Ticks));

            _playbackParams.StartPositionTicks = currentPosition.Ticks;

            if (audioStreamIndex.HasValue)
            {
                _playbackParams.AudioStreamIndex = audioStreamIndex.Value;
            }

            if (subtitleStreamIndex.HasValue)
            {
                _playbackParams.SubtitleStreamIndex = subtitleStreamIndex.Value;
            }

            // Replaces _playSessionId: the reports below name the new stream. StartPlayback sets
            // AutoPlay, so the new stream plays once it opens; a Play() before that would start
            // audio and video out of step, as StartPlayback notes.
            var playbackInfo = await GetPlaybackInfoAsync();
            var mediaSource = await CreateMediaSourceAsync(playbackInfo);
            StartPlayback(mediaSource, currentPosition.Ticks);

            await _mediaSessionService.ReportPlaybackStartAsync(CreateReport(currentPosition.Ticks));

            Logger.LogDebug("Successfully restarted playback for {RestartReason}", restartReason);
        }
    }
}
