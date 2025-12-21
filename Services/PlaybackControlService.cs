using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using AudioTrack = Gelatinarm.Models.AudioTrack;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Service for controlling media playback operations
    /// </summary>
    public class PlaybackControlService : BaseService, IPlaybackControlService
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly IAuthenticationService _authService;
        private readonly IDeviceProfileService _deviceProfileService;
        private readonly IMediaControlService _mediaControlService;
        private readonly IPreferencesService _preferencesService;
        private readonly IMediaPlaybackService _mediaPlaybackService;
        private readonly IPlaybackRestartService _playbackRestartService;
        private readonly IUnifiedDeviceService _deviceService;
        private readonly PlaybackResumeCoordinator _resumeCoordinator;
        private readonly PlaybackSourceResolver _sourceResolver;
        private BaseItemDto _currentItem;
        private MediaSourceInfo _currentMediaSource;

        private MediaPlayer _mediaPlayer;
        private string _playSessionId;
        private MediaPlaybackParams _playbackParams;
        private TimeSpan? _pendingResumePosition;
        private IStreamResumePolicy _resumePolicy = new DirectPlayResumePolicy();

        // Resume state management
        // Enhanced HLS resume tracking
        private DateTime _lastPositionLogTime = DateTime.MinValue;

        public PlaybackControlService(
            ILogger<PlaybackControlService> logger,
            JellyfinApiClient apiClient,
            IAuthenticationService authService,
            IDeviceProfileService deviceProfileService,
            IPreferencesService preferencesService,
            IMediaControlService mediaControlService,
            IMediaPlaybackService mediaPlaybackService,
            IPlaybackRestartService playbackRestartService,
            IUnifiedDeviceService deviceService) : base(logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _deviceProfileService =
                deviceProfileService ?? throw new ArgumentNullException(nameof(deviceProfileService));
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
            _mediaControlService = mediaControlService ?? throw new ArgumentNullException(nameof(mediaControlService));
            _mediaPlaybackService = mediaPlaybackService ?? throw new ArgumentNullException(nameof(mediaPlaybackService));
            _playbackRestartService = playbackRestartService ?? throw new ArgumentNullException(nameof(playbackRestartService));
            _deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));
            _resumeCoordinator = new PlaybackResumeCoordinator(logger);
            _sourceResolver = new PlaybackSourceResolver(logger, apiClient, authService, deviceService);
        }

        public event EventHandler<MediaPlaybackState> PlaybackStateChanged;
        public event EventHandler<TimeSpan> PositionChanged;

        /// <summary>
        /// Gets the HLS manifest offset when server creates a new manifest at a different position
        /// This should be added to the playback position to get the actual media position
        /// </summary>
        public TimeSpan HlsManifestOffset { get; internal set; } = TimeSpan.Zero;

        public async Task InitializeAsync(MediaPlayer mediaPlayer, MediaPlaybackParams playbackParams)
        {
            _mediaPlayer = InitializeParameter(mediaPlayer, nameof(mediaPlayer));
            _playbackParams = InitializeParameter(playbackParams, nameof(playbackParams));
            _currentItem = playbackParams.Item;

            ResetResumeTracking();

            // Subscribe to media player events using helper
            SubscribeToPlaybackEvents(_mediaPlayer);

            await Task.CompletedTask;
        }

        public async Task<PlaybackInfoResponse> GetPlaybackInfoAsync(BaseItemDto item, int? maxBitrate = null)
        {
            try
            {
                var deviceProfile = _deviceProfileService.GetDeviceProfile();
                var preferences = await _preferencesService.GetAppPreferencesAsync();

                // Always use adaptive streaming with no bitrate limit
                // Server will automatically adjust quality based on network conditions
                int? maxStreamingBitrate = null;
                Logger.LogInformation("Using adaptive bitrate streaming (no bitrate limit)");

                if (!TryGetAuthUserGuid(_authService, out var userGuid))
                {
                    throw new InvalidOperationException("Invalid user ID format");
                }

                // When subtitle/audio tracks are specified with resume position, some servers ignore StartTimeTicks.
                // Apply resume client-side in these cases.
                bool hasSubtitlesWithResume = _playbackParams?.SubtitleStreamIndex.HasValue == true &&
                                              _playbackParams.SubtitleStreamIndex.Value >= 0 &&
                                              _playbackParams?.StartPositionTicks > 0;

                bool hasAudioWithResume = _playbackParams?.AudioStreamIndex.HasValue == true &&
                                          _playbackParams.AudioStreamIndex.Value >= 0 &&
                                          _playbackParams?.StartPositionTicks > 0;

                // Check if the item has 2-channel or less audio
                // If so, always allow audio stream copy to prevent unnecessary transcoding
                bool shouldAllowAudioStreamCopy = preferences.AllowAudioStreamCopy;

                if (item?.MediaStreams != null)
                {
                    var audioStream = item.MediaStreams.FirstOrDefault(s => s.Type == MediaStream_Type.Audio);
                    if (audioStream != null && audioStream.Channels.HasValue && audioStream.Channels.Value <= 2)
                    {
                        // Always allow stream copy for stereo or mono audio
                        shouldAllowAudioStreamCopy = true;
                        Logger.LogInformation($"Detected {audioStream.Channels.Value} channel audio - forcing AllowAudioStreamCopy=true");
                    }
                }

                var playbackInfoRequest = new PlaybackInfoDto
                {
                    UserId = userGuid,
                    MediaSourceId = _playbackParams?.MediaSourceId,
                    AudioStreamIndex = _playbackParams?.AudioStreamIndex,
                    SubtitleStreamIndex = _playbackParams?.SubtitleStreamIndex,
                    // Always send StartTimeTicks - server may use it even for HLS
                    StartTimeTicks = _playbackParams?.StartPositionTicks,
                    MaxStreamingBitrate = maxStreamingBitrate,
                    AutoOpenLiveStream = true,
                    DeviceProfile = deviceProfile,
                    EnableDirectPlay = preferences.EnableDirectPlay,
                    EnableDirectStream = true, // Always allow direct stream
                    EnableTranscoding = true, // Always allow transcoding as fallback
                    AllowAudioStreamCopy = shouldAllowAudioStreamCopy,
                    AllowVideoStreamCopy = true // Generally want to allow video stream copy when possible
                };

                LogClientSideResumeRequirement(hasSubtitlesWithResume, hasAudioWithResume, _playbackParams?.StartPositionTicks);
                LogPlaybackInfoRequest(preferences, shouldAllowAudioStreamCopy);

                if (!item.Id.HasValue)
                {
                    Logger.LogError("Cannot get playback info - item has no ID");
                    throw new ArgumentException("Item must have an ID", nameof(item));
                }

                var response = await _apiClient.Items[item.Id.Value]
                    .PlaybackInfo.PostAsync(playbackInfoRequest)
                    .ConfigureAwait(false);

                _playSessionId = response.PlaySessionId;

                // Log response details
                Logger.LogInformation($"Playback info response - MediaSources count: {response?.MediaSources?.Count ?? 0}");
                if (response?.MediaSources != null)
                {
                    foreach (var source in response.MediaSources)
                    {
                        Logger.LogInformation($"MediaSource Id: {source.Id}, Name: {source.Name}, SupportsDirectPlay: {source.SupportsDirectPlay}, SupportsDirectStream: {source.SupportsDirectStream}, SupportsTranscoding: {source.SupportsTranscoding}");
                    }
                }

                return response;
            }
            catch (Exception ex)
            {
                await HandleErrorAsync(ex, $"Get playback info for {item.Name}", ErrorCategory.Media);
                throw;
            }
        }

        public async Task<MediaSource> CreateMediaSourceAsync(PlaybackInfoResponse playbackInfo)
        {
            try
            {
                // Select the best media source
                _currentMediaSource = _sourceResolver.SelectBestMediaSource(playbackInfo.MediaSources);

                if (_currentMediaSource == null)
                {
                    throw new InvalidOperationException("No valid media source found");
                }

                LogMediaSourceDetails(_currentMediaSource);

                if (_currentMediaSource.SupportsDirectPlay == true)
                {
                    return CreateDirectPlayMediaSource(playbackInfo);
                }

                return await CreateStreamingMediaSourceAsync(playbackInfo);
            }
            catch (Exception ex)
            {
                await HandleErrorAsync(ex, "Create media source", ErrorCategory.Media);
                throw;
            }
        }

        public async Task StartPlaybackAsync(MediaSource mediaSource, long? startPositionTicks = null)
        {
            try
            {
                Logger.LogInformation("[PLAYBACK-START] Beginning StartPlaybackAsync");

                // Log MediaSource state before creating playback item
                if (mediaSource != null)
                {
                    Logger.LogInformation($"[PLAYBACK-START] MediaSource State: {mediaSource.State}, " +
                        $"IsOpen: {mediaSource.IsOpen}, " +
                        $"Duration: {mediaSource.Duration?.TotalSeconds}s");
                }

                // Create MediaPlaybackItem from MediaSource to enable subtitle/audio track access
                Logger.LogInformation("[PLAYBACK-START] Creating MediaPlaybackItem");
                var playbackItem = new MediaPlaybackItem(mediaSource);

                // TIERED RESUME APPROACH:
                // Tier 1: StartTimeTicks was already sent to server in GetPlaybackInfoAsync
                // Tier 2: If server doesn't honor it, apply client-side seek
                // Tier 3: For HLS, track manifest offset if server creates new manifest at seek position

                var shouldResume = startPositionTicks.HasValue && startPositionTicks.Value > 0;
                var resumePosition = shouldResume ? TimeSpan.FromTicks(startPositionTicks.Value) : TimeSpan.Zero;

                PrepareResumeTracking(shouldResume, resumePosition);

                Logger.LogInformation("[PLAYBACK-START] Setting MediaPlayer.Source");
                _mediaPlayer.Source = playbackItem;

                // FIX for audio/video sync: Set AutoPlay to true instead of calling Play() immediately
                // This allows the MediaPlayer to fully initialize streams before starting playback
                Logger.LogInformation("[PLAYBACK-START] Setting AutoPlay = true (allows proper stream initialization)");
                _mediaPlayer.AutoPlay = true;

                // Note: The MediaPlayer will automatically start playing when ready
                // This prevents audio/video sync issues that occur when Play() is called too early
                Logger.LogInformation("[PLAYBACK-START] MediaPlayer will auto-start when ready");

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[PLAYBACK-START] Failed to start playback");

                // Log COM-specific error details
                if (ex.HResult != 0)
                {
                    Logger.LogError($"[PLAYBACK-START] HResult: 0x{ex.HResult:X8}");
                }

                throw;
            }
        }

        // Note: Basic playback control methods (Play/Pause/Stop/Seek/Skip) have been removed
        // from this service. Use MediaControlService for direct MediaPlayer control.
        // This service focuses on adaptive streaming, format-specific operations,
        // and media source creation.

        public bool ApplyResumeIfNeeded(ref long pendingSeekPositionTicks)
        {
            if (pendingSeekPositionTicks > 0 && _mediaPlayer?.PlaybackSession != null)
            {
                var targetPosition = TimeSpan.FromTicks(pendingSeekPositionTicks);
                Logger.LogInformation($"Applying pending seek after quality/track switch to {targetPosition:hh\\:mm\\:ss}");

                try
                {
                    _mediaPlayer.PlaybackSession.Position = targetPosition;
                    pendingSeekPositionTicks = 0;
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to apply quality/track switch resume position");
                }
            }

            return ApplyPendingResumePosition();
        }

        public bool ApplyPendingResumePosition()
        {
            var originalTarget = _playbackParams?.StartPositionTicks.HasValue == true
                ? TimeSpan.FromTicks(_playbackParams.StartPositionTicks.Value)
                : (TimeSpan?)null;

            return _resumeCoordinator.ApplyPendingResumePosition(
                _mediaPlayer,
                ref _pendingResumePosition,
                _resumePolicy,
                originalTarget,
                offset => HlsManifestOffset = offset);
        }

        /// <summary>
        /// Check if HLS resume is still in progress
        /// </summary>
        public bool IsHlsResumeInProgress()
        {
            return _resumeCoordinator.IsInProgress(_resumePolicy, _pendingResumePosition.HasValue);
        }

        public void CancelPendingResume(string reason)
        {
            _resumeCoordinator.CancelPendingResume(ref _pendingResumePosition, reason);
        }

        /// <summary>
        /// Get HLS resume status for diagnostics
        /// </summary>
        public (bool InProgress, int Attempts, TimeSpan? Target) GetHlsResumeStatus()
        {
            return _resumeCoordinator.GetStatus(_resumePolicy, _pendingResumePosition);
        }

        public async Task<List<AudioTrack>> GetAudioTracksAsync(PlaybackInfoResponse playbackInfo)
        {
            try
            {
                var audioTracks = new List<AudioTrack>();

                if (_currentMediaSource?.MediaStreams != null)
                {
                    var audioStreams = _currentMediaSource.MediaStreams
                        .Where(s => s.Type == MediaStream_Type.Audio)
                        .ToList();

                    foreach (var stream in audioStreams)
                    {
                        audioTracks.Add(new AudioTrack
                        {
                            ServerStreamIndex = stream.Index ?? 0,
                            Language = stream.Language ?? "Unknown",
                            DisplayName = GetAudioTrackDisplayName(stream),
                            IsDefault = stream.IsDefault ?? false
                        });
                    }
                }

                return await Task.FromResult(audioTracks);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to get audio tracks");
                return new List<AudioTrack>();
            }
        }

        private void ResetResumeTracking()
        {
            _resumePolicy = new DirectPlayResumePolicy();
            HlsManifestOffset = TimeSpan.Zero;
            _resumeCoordinator.Reset();
        }

        private void LogMediaSourceDetails(MediaSourceInfo mediaSource)
        {
            Logger.LogInformation($"MediaSource TranscodingUrl: {mediaSource.TranscodingUrl}");
            Logger.LogInformation($"MediaSource DirectStreamUrl: {PlaybackSourceResolver.GetDirectStreamUrl(mediaSource)}");
            Logger.LogInformation($"MediaSource SupportsDirectPlay: {mediaSource.SupportsDirectPlay}");
            Logger.LogInformation($"MediaSource SupportsDirectStream: {mediaSource.SupportsDirectStream}");
            Logger.LogInformation($"MediaSource SupportsTranscoding: {mediaSource.SupportsTranscoding}");
        }

        private MediaSource CreateDirectPlayMediaSource(PlaybackInfoResponse playbackInfo)
        {
            Logger.LogInformation("Direct Play is available - using direct HTTP streaming");

            // Clear the TranscodingUrl since we're using Direct Play
            // This ensures PlaybackStatisticsService correctly reports "Direct playing"
            _currentMediaSource.TranscodingUrl = null;

            var directPlayUrl = _sourceResolver.BuildStreamUrl(_currentMediaSource, _currentItem, _playbackParams, playbackInfo.PlaySessionId);
            Logger.LogInformation($"Direct Play URL: {directPlayUrl}");

            _resumePolicy = new DirectPlayResumePolicy();

            return MediaSource.CreateFromUri(new Uri(directPlayUrl));
        }

        private async Task<MediaSource> CreateStreamingMediaSourceAsync(PlaybackInfoResponse playbackInfo)
        {
            var streamUrl = _sourceResolver.BuildStreamUrl(_currentMediaSource, _currentItem, _playbackParams, playbackInfo.PlaySessionId);
            Logger.LogInformation("Stream URL: {StreamUrl}", streamUrl);

            UpdateResumePolicyFromMediaSource();

            if (IsAdaptiveStreaming(_currentMediaSource))
            {
                return await CreateAdaptiveStreamingSourceAsync(streamUrl);
            }

            return MediaSource.CreateFromUri(new Uri(streamUrl));
        }

        private async Task<MediaSource> CreateAdaptiveStreamingSourceAsync(string streamUrl)
        {
            LogAdaptiveMediaSourceRequest(streamUrl);

            // Skip HEAD request - it's causing 400 errors with the server
            // Go directly to creating AdaptiveMediaSource
            var result = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(streamUrl));

            LogAdaptiveMediaSourceResult(result);

            if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
            {
                LogAdaptiveMediaSourceFailure(result);
                throw new InvalidOperationException($"AdaptiveMediaSource creation failed: {result.Status}");
            }

            ConfigureAdaptiveMediaSource(result.MediaSource);
            return MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
        }

        private void ConfigureAdaptiveMediaSource(AdaptiveMediaSource adaptiveSource)
        {
            if (adaptiveSource?.AvailableBitrates != null && adaptiveSource.AvailableBitrates.Count > 0)
            {
                Logger.LogInformation($"Initial bitrate: {adaptiveSource.InitialBitrate / 1000}kbps");
                Logger.LogInformation($"Available bitrates: {adaptiveSource.AvailableBitrates.Count}");
            }

            adaptiveSource.DownloadBitrateChanged += OnDownloadBitrateChanged;
            adaptiveSource.PlaybackBitrateChanged += OnPlaybackBitrateChanged;
        }

        private void UpdateResumePolicyFromMediaSource()
        {
            var isHlsStream = _currentMediaSource.TranscodingUrl?.Contains(".m3u8") == true;
            _resumePolicy = isHlsStream ? new HlsResumePolicy() : new DirectPlayResumePolicy();
        }

        private static bool IsAdaptiveStreaming(MediaSourceInfo mediaSource)
        {
            return mediaSource.TranscodingUrl?.Contains(".m3u8") == true ||
                mediaSource.TranscodingUrl?.Contains(".mpd") == true;
        }

        private void PrepareResumeTracking(bool shouldResume, TimeSpan resumePosition)
        {
            if (!shouldResume)
            {
                return;
            }

            if (_resumePolicy is HlsResumePolicy)
            {
                _resumePolicy = new HlsInitialResumePolicy();
                Logger.LogInformation("[RESUME-TIERED] Initial HLS resume - disabling manifest offset workaround");
            }

            _pendingResumePosition = resumePosition;

            Logger.LogInformation($"[RESUME-TIERED] Resume requested to {resumePosition:hh\\:mm\\:ss}");
            Logger.LogInformation("[RESUME-TIERED] Tier 1: StartTimeTicks already sent to server in PlaybackInfoDto");
            Logger.LogInformation("[RESUME-TIERED] Tier 2: Will apply client-side seek if position is not at resume point");

            if (_resumePolicy.ShouldUseManifestOffset)
            {
                Logger.LogInformation("[RESUME-TIERED] Tier 3: HLS detected - will track manifest offset if server creates new manifest");
            }
        }

        private void LogAdaptiveMediaSourceRequest(string streamUrl)
        {
            Logger.LogInformation("Creating AdaptiveMediaSource for HLS/DASH stream");

            if (!UrlHelper.HasApiKey(streamUrl))
            {
                Logger.LogWarning("[HLS-DEBUG] Stream URL does not include ApiKey parameter; auth headers are not used for AdaptiveMediaSource");
            }

            var playSessionId = GetQueryParameter(streamUrl, "PlaySessionId");
            LogAdaptiveMediaSourceParam("PlaySessionId", playSessionId);

            var startTicks = GetQueryParameter(streamUrl, "StartTimeTicks");
            LogAdaptiveMediaSourceStartTicks(startTicks);

            var transcodeReasons = GetQueryParameter(streamUrl, "TranscodeReasons");
            LogAdaptiveMediaSourceParam("TranscodeReasons", transcodeReasons);
        }

        private void LogAdaptiveMediaSourceParam(string label, string value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                Logger.LogInformation("[HLS-DEBUG] {Label}: {Value}", label, value);
            }
        }

        private void LogAdaptiveMediaSourceStartTicks(string startTicks)
        {
            if (!string.IsNullOrEmpty(startTicks))
            {
                Logger.LogInformation("[HLS-MANIFEST] StartTimeTicks in URL: {StartTimeTicks}", startTicks);
                return;
            }

            Logger.LogInformation("[HLS-MANIFEST] No StartTimeTicks in URL - server may provide manifest starting at 0");
        }

        private static string GetQueryParameter(string url, string key)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(key))
            {
                return null;
            }

            var marker = $"{key}=";
            var start = url.IndexOf(marker, StringComparison.Ordinal);
            if (start == -1)
            {
                return null;
            }

            start += marker.Length;
            var end = url.IndexOf("&", start, StringComparison.Ordinal);
            if (end == -1)
            {
                end = url.Length;
            }

            return url.Substring(start, end - start);
        }

        private void LogClientSideResumeRequirement(
            bool hasSubtitlesWithResume,
            bool hasAudioWithResume,
            long? startPositionTicks)
        {
            if (!startPositionTicks.HasValue || startPositionTicks.Value <= 0)
            {
                return;
            }

            if (!hasSubtitlesWithResume && !hasAudioWithResume)
            {
                return;
            }

            var reason = hasSubtitlesWithResume
                ? (hasAudioWithResume ? "Subtitle + Audio" : "Subtitle")
                : "Audio";
            Logger.LogInformation($"{reason} + Resume detected: Will apply resume position {TimeSpan.FromTicks(startPositionTicks.Value):hh\\:mm\\:ss} client-side");
        }

        private void LogPlaybackInfoRequest(AppPreferences preferences, bool shouldAllowAudioStreamCopy)
        {
            Logger.LogInformation($"Requesting playback info with MediaSourceId: {_playbackParams?.MediaSourceId}");
            Logger.LogInformation($"AudioStreamIndex: {_playbackParams?.AudioStreamIndex}");
            Logger.LogInformation($"SubtitleStreamIndex: {_playbackParams?.SubtitleStreamIndex}");

            if (_playbackParams?.StartPositionTicks > 0)
            {
                var resumeTime = TimeSpan.FromTicks(_playbackParams.StartPositionTicks.Value);
                Logger.LogInformation($"StartTimeTicks: {_playbackParams.StartPositionTicks} ({resumeTime:hh\\:mm\\:ss})");
                Logger.LogInformation("Note: Server may ignore StartTimeTicks for HLS streams - client-side seek will be used");
            }
            else
            {
                Logger.LogInformation("StartTimeTicks: 0 (starting from beginning)");
            }

            Logger.LogInformation("MaxStreamingBitrate: unlimited (adaptive)");
            Logger.LogInformation($"EnableDirectPlay: {preferences.EnableDirectPlay}");
            Logger.LogInformation("EnableDirectStream: true");
            Logger.LogInformation("EnableTranscoding: true");
            Logger.LogInformation($"AllowAudioStreamCopy: {shouldAllowAudioStreamCopy}");
            Logger.LogInformation("AllowVideoStreamCopy: true");
        }

        private void LogAdaptiveMediaSourceResult(AdaptiveMediaSourceCreationResult result)
        {
            Logger.LogInformation($"[HLS-DEBUG] AdaptiveMediaSource.CreateFromUriAsync returned status: {result.Status}");
            if (result.HttpResponseMessage != null)
            {
                Logger.LogInformation($"[HLS-DEBUG] HTTP Status Code: {result.HttpResponseMessage.StatusCode} {result.HttpResponseMessage.ReasonPhrase}");
                return;
            }

            Logger.LogWarning("[HLS-DEBUG] No HttpResponseMessage available in result");
        }

        private void LogAdaptiveMediaSourceFailure(AdaptiveMediaSourceCreationResult result)
        {
            Logger.LogError($"Failed to create AdaptiveMediaSource: {result.Status}");
            Logger.LogError("[HLS-DEBUG] Failure Details:");
            Logger.LogError($"[HLS-DEBUG]   Status: {result.Status}");
            Logger.LogError($"[HLS-DEBUG]   ExtendedError: {result.ExtendedError?.Message ?? "None"}");
            Logger.LogError($"[HLS-DEBUG]   ExtendedError HResult: {result.ExtendedError?.HResult ?? 0}");

            switch (result.Status)
            {
                case AdaptiveMediaSourceCreationStatus.ManifestDownloadFailure:
                    Logger.LogError("[HLS-DEBUG] Manifest download failed - possible causes:");
                    Logger.LogError("[HLS-DEBUG]   - Server not reachable or URL incorrect");
                    Logger.LogError("[HLS-DEBUG]   - Authentication/authorization issue");
                    Logger.LogError("[HLS-DEBUG]   - Server hasn't started transcoding yet");
                    Logger.LogError("[HLS-DEBUG]   - Network connectivity issue");
                    Logger.LogError("[HLS-DEBUG]   - SSL/TLS certificate issue");
                    break;
                case AdaptiveMediaSourceCreationStatus.ManifestParseFailure:
                    Logger.LogError("[HLS-DEBUG] Manifest parse failed - invalid HLS/DASH manifest format");
                    break;
                case AdaptiveMediaSourceCreationStatus.UnsupportedManifestContentType:
                    Logger.LogError("[HLS-DEBUG] Unsupported manifest content type");
                    break;
                case AdaptiveMediaSourceCreationStatus.UnsupportedManifestVersion:
                    Logger.LogError("[HLS-DEBUG] Unsupported manifest version");
                    break;
                case AdaptiveMediaSourceCreationStatus.UnsupportedManifestProfile:
                    Logger.LogError("[HLS-DEBUG] Unsupported manifest profile");
                    break;
                case AdaptiveMediaSourceCreationStatus.UnknownFailure:
                    Logger.LogError("[HLS-DEBUG] Unknown failure occurred");
                    break;
            }
        }

        public async Task ChangeAudioTrackAsync(AudioTrack audioTrack)
        {
            try
            {
                Logger.LogInformation($"Changing audio track to: {audioTrack.DisplayName}");

                // Use shared restart method, passing the audio index
                await RestartPlaybackWithCurrentPositionAsync(
                    restartReason: $"audio track change to {audioTrack.DisplayName}",
                    audioStreamIndex: audioTrack.ServerStreamIndex);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to change audio track");
                throw;
            }
        }

        public MediaSourceInfo GetCurrentMediaSource()
        {
            return _currentMediaSource;
        }

        protected override void UnsubscribeEvents()
        {
            UnsubscribeFromPlaybackEvents(_mediaPlayer);
            base.UnsubscribeEvents();
        }


        private string GetAudioTrackDisplayName(MediaStream stream)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(stream.Language))
            {
                parts.Add(stream.Language);
            }

            if (!string.IsNullOrEmpty(stream.Codec))
            {
                parts.Add(stream.Codec.ToUpperInvariant());
            }

            if (stream.Channels.HasValue)
            {
                parts.Add($"{stream.Channels}ch");
            }

            if (stream.BitRate.HasValue)
            {
                parts.Add($"{stream.BitRate / 1000}kbps");
            }

            return string.Join(" - ", parts);
        }

        private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            try
            {
                // Early exit if sender is null or disposed
                if (sender == null)
                {
                    Logger.LogDebug("[PLAYBACK-STATE] Sender is null, skipping handler");
                    return;
                }

                MediaPlaybackState state = MediaPlaybackState.None;
                TimeSpan position = TimeSpan.Zero;
                double bufferProgress = 1.0;
                bool? canPause = null;
                bool? canSeek = null;

                // Wrap all COM object property access in try-catch to handle disposed objects
                try
                {
                    state = sender.PlaybackState;
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80004005) || ex.HResult == unchecked((int)0x800706BA))
                {
                    // COM object disposed or RPC server unavailable
                    Logger.LogDebug("[PLAYBACK-STATE] MediaPlaybackSession disposed, skipping handler");
                    return;
                }

                try
                {
                    position = sender.Position;
                }
                catch (Exception)
                {
                    // Position unavailable
                }

                var isHlsStream = _currentMediaSource?.TranscodingUrl?.Contains(".m3u8") == true;

                // BufferingProgress often throws InvalidCastException for HLS streams
                try
                {
                    if (!isHlsStream)
                    {
                        bufferProgress = sender.BufferingProgress;
                    }
                }
                catch (Exception)
                {
                    // Expected for HLS streams or disposed objects
                }

                // These properties may throw when the session is disposed
                try
                {
                    canPause = sender.CanPause;
                    canSeek = sender.CanSeek;
                }
                catch (Exception)
                {
                    // Properties unavailable - session may be disposed
                }

                Logger.LogInformation($"[PLAYBACK-STATE] State changed to: {state}, " +
                    $"Position: {position.TotalSeconds:F2}s, " +
                    $"BufferingProgress: {bufferProgress:P}, " +
                    $"CanPause: {canPause?.ToString() ?? "N/A"}, " +
                    $"CanSeek: {canSeek?.ToString() ?? "N/A"}");

                // Log memory usage periodically
                if (state == MediaPlaybackState.Playing || state == MediaPlaybackState.Buffering)
                {
                    var memoryUsage = Windows.System.MemoryManager.AppMemoryUsage / (1024.0 * 1024.0);
                    var memoryLimit = Windows.System.MemoryManager.AppMemoryUsageLimit / (1024.0 * 1024.0);
                    Logger.LogDebug($"[MEMORY] Current usage: {memoryUsage:F2} MB / {memoryLimit:F2} MB ({memoryUsage / memoryLimit:P})");
                }

                PlaybackStateChanged?.Invoke(this, state);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[ERROR] Exception in OnPlaybackStateChanged handler");
                // Don't rethrow - we don't want to crash the app from an event handler
            }
        }

        private void OnPositionChanged(MediaPlaybackSession sender, object args)
        {
            try
            {
                // Early exit if sender is null or disposed
                if (sender == null)
                {
                    return;
                }

                TimeSpan position = TimeSpan.Zero;
                TimeSpan naturalDuration = TimeSpan.Zero;
                double? playbackRate = null;
                bool? isProtected = null;

                // Wrap all COM object property access in try-catch to handle disposed objects
                try
                {
                    position = sender.Position;
                }
                catch (Exception ex) when (ex.HResult == unchecked((int)0x80004005) || ex.HResult == unchecked((int)0x800706BA))
                {
                    // COM object disposed or RPC server unavailable
                    return;
                }

                // Only log position changes every 5 seconds to reduce log spam
                var now = DateTime.UtcNow;
                if ((now - _lastPositionLogTime).TotalSeconds >= 5.0)
                {
                    // Try to get additional properties for logging, but don't fail if unavailable
                    try
                    {
                        naturalDuration = sender.NaturalDuration;
                        playbackRate = sender.PlaybackRate;
                        isProtected = sender.IsProtected;
                    }
                    catch (Exception)
                    {
                        // Properties unavailable - session may be disposed
                    }

                    Logger.LogDebug($"[POSITION] Current: {position.TotalSeconds:F2}s / {naturalDuration.TotalSeconds:F2}s, " +
                        $"PlaybackRate: {playbackRate?.ToString() ?? "N/A"}, " +
                        $"IsProtected: {isProtected?.ToString() ?? "N/A"}");
                    _lastPositionLogTime = now;
                }

                PositionChanged?.Invoke(this, position);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[ERROR] Exception in OnPositionChanged handler");
                // Don't rethrow - we don't want to crash the app from an event handler
            }
        }

        #region MediaPlayer Event Helpers

        /// <summary>
        ///     Subscribe to common MediaPlayer playback events
        /// </summary>
        protected void SubscribeToPlaybackEvents(MediaPlayer mediaPlayer)
        {
            if (mediaPlayer?.PlaybackSession == null)
                return;

            mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
            mediaPlayer.PlaybackSession.PositionChanged += OnPositionChanged;
        }

        /// <summary>
        ///     Unsubscribe from MediaPlayer playback events
        /// </summary>
        protected void UnsubscribeFromPlaybackEvents(MediaPlayer mediaPlayer)
        {
            if (mediaPlayer?.PlaybackSession == null)
                return;

            mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            mediaPlayer.PlaybackSession.PositionChanged -= OnPositionChanged;
        }

        #endregion

        #region Stream Switching Helper

        /// <summary>
        ///     Common method for switching streams (audio, subtitle, quality) with proper resume
        /// </summary>
        /// <param name="maxBitrate">Optional max bitrate for quality changes</param>
        /// <param name="restartReason">Reason for restart (for logging)</param>
        /// <returns>Task</returns>
        public async Task RestartPlaybackWithCurrentPositionAsync(int? maxBitrate = null, string restartReason = "stream change", int? audioStreamIndex = null, int? subtitleStreamIndex = null)
        {
            try
            {
                var itemToReload = _currentItem ?? _playbackParams?.Item;
                await _playbackRestartService.RestartPlaybackAsync(new PlaybackRestartRequest
                {
                    RestartReason = restartReason,
                    MediaPlayer = _mediaPlayer,
                    PlaybackParams = _playbackParams,
                    ItemToReload = itemToReload,
                    PlaySessionId = _playSessionId,
                    AudioStreamIndex = audioStreamIndex,
                    SubtitleStreamIndex = subtitleStreamIndex,
                    MaxBitrate = maxBitrate,
                    GetPlaybackInfoAsync = GetPlaybackInfoAsync,
                    CreateMediaSourceAsync = CreateMediaSourceAsync,
                    StartPlaybackAsync = StartPlaybackAsync
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Failed to restart playback for {restartReason}");
                throw;
            }
        }

        #endregion

        #region Adaptive Streaming Event Handlers

        private void OnDownloadBitrateChanged(AdaptiveMediaSource sender, AdaptiveMediaSourceDownloadBitrateChangedEventArgs args)
        {
            try
            {
                Logger.LogInformation($"[BITRATE] Download bitrate changed from {args.OldValue / 1000}kbps to {args.NewValue / 1000}kbps");

                // Log memory usage when bitrate changes
                var memoryUsage = Windows.System.MemoryManager.AppMemoryUsage / (1024.0 * 1024.0);
                Logger.LogDebug($"[MEMORY] After bitrate change: {memoryUsage:F2} MB");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[ERROR] Exception in OnDownloadBitrateChanged handler");
            }
        }

        private void OnPlaybackBitrateChanged(AdaptiveMediaSource sender, AdaptiveMediaSourcePlaybackBitrateChangedEventArgs args)
        {
            try
            {
                Logger.LogInformation($"[BITRATE] Playback bitrate changed from {args.OldValue / 1000}kbps to {args.NewValue / 1000}kbps");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[ERROR] Exception in OnPlaybackBitrateChanged handler");
            }
        }

        #endregion
    }
}
