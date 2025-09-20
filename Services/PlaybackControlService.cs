using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        private readonly IUnifiedDeviceService _deviceService;
        private BaseItemDto _currentItem;
        private MediaSourceInfo _currentMediaSource;

        private MediaPlayer _mediaPlayer;
        private string _playSessionId;
        private MediaPlaybackParams _playbackParams;
        private TimeSpan? _pendingResumePosition;

        // Resume state management
        // State machine flow: NotStarted -> InProgress -> Verifying -> (RecoveryNeeded)* -> Succeeded/Failed
        // RecoveryNeeded can loop back to Verifying multiple times with different recovery attempts
        private enum ResumeState
        {
            NotStarted,      // Resume not yet attempted
            InProgress,      // Initial resume seek applied
            Verifying,       // Checking if position is advancing at target
            RecoveryNeeded,  // Stuck, applying recovery techniques
            Succeeded,       // Resume completed successfully
            Failed           // Resume failed after all attempts
        }

        // Retry configuration for different stream types
        private struct RetryConfig
        {
            public int MaxAttempts;
            public int DelayMs;
            public double ToleranceSeconds;

            public static RetryConfig ForHls => new RetryConfig
            {
                MaxAttempts = 8,  // HLS needs more time for server to transcode
                DelayMs = 5000,   // Increased to 5s to give server time to restart transcode and generate segments
                ToleranceSeconds = 3.0
            };

            public static RetryConfig ForDirectPlay => new RetryConfig
            {
                MaxAttempts = 5,
                DelayMs = 1000,
                ToleranceSeconds = 2.0
            };
        }

        // Enhanced HLS resume tracking
        private bool _isHlsStream = false;
        private ResumeState _resumeState = ResumeState.NotStarted;
        private volatile int _hlsResumeAttempts = 0;
        private DateTime _hlsResumeStartTime = DateTime.MinValue;
        private TimeSpan _lastVerifiedPosition = TimeSpan.Zero;
        private DateTime _lastPositionCheckTime = DateTime.MinValue;
        private volatile int _stuckPositionCount = 0;
        private volatile int _recoveryAttemptLevel = 0;
        private const int MAX_STUCK_CHECKS = 5; // If position doesn't change for 5 checks, it's stuck
        private const double STUCK_POSITION_TOLERANCE = 0.5; // Position must advance by at least 0.5 seconds
        private DateTime _lastPositionLogTime = DateTime.MinValue;

        // Stuck detection status
        private enum StuckStatus
        {
            NotStuck,
            PossiblyStuck,
            DefinitelyStuck
        }

        public PlaybackControlService(
            ILogger<PlaybackControlService> logger,
            JellyfinApiClient apiClient,
            IAuthenticationService authService,
            IDeviceProfileService deviceProfileService,
            IPreferencesService preferencesService,
            IMediaControlService mediaControlService,
            IMediaPlaybackService mediaPlaybackService,
            IUnifiedDeviceService deviceService) : base(logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _authService = authService ?? throw new ArgumentNullException(nameof(authService));
            _deviceProfileService =
                deviceProfileService ?? throw new ArgumentNullException(nameof(deviceProfileService));
            _preferencesService = preferencesService ?? throw new ArgumentNullException(nameof(preferencesService));
            _mediaControlService = mediaControlService ?? throw new ArgumentNullException(nameof(mediaControlService));
            _mediaPlaybackService = mediaPlaybackService ?? throw new ArgumentNullException(nameof(mediaPlaybackService));
            _deviceService = deviceService ?? throw new ArgumentNullException(nameof(deviceService));
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

            // Reset resume tracking for new playback
            _isHlsStream = false;
            _resumeState = ResumeState.NotStarted;
            _hlsResumeAttempts = 0;
            _hlsResumeStartTime = DateTime.MinValue;
            HlsManifestOffset = TimeSpan.Zero; // Reset HLS manifest offset

            // Reset stuck detection tracking
            _lastVerifiedPosition = TimeSpan.Zero;
            _lastPositionCheckTime = DateTime.MinValue;
            Interlocked.Exchange(ref _stuckPositionCount, 0);
            _recoveryAttemptLevel = 0;

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

                if (!Guid.TryParse(_authService.UserId, out var userGuid))
                {
                    Logger.LogError($"Invalid user ID format: {_authService.UserId}");
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
                
                bool needsClientSideResume = hasSubtitlesWithResume || hasAudioWithResume;
                
                var playbackInfoRequest = new PlaybackInfoDto
                {
                    UserId = userGuid,
                    MediaSourceId = _playbackParams?.MediaSourceId,
                    AudioStreamIndex = _playbackParams?.AudioStreamIndex,
                    SubtitleStreamIndex = _playbackParams?.SubtitleStreamIndex,
                    // Don't send StartTimeTicks if we have subtitle/audio selection - we'll handle resume client-side
                    StartTimeTicks = needsClientSideResume ? 0 : _playbackParams?.StartPositionTicks,
                    MaxStreamingBitrate = maxStreamingBitrate,
                    AutoOpenLiveStream = true,
                    DeviceProfile = deviceProfile,
                    EnableDirectPlay = preferences.EnableDirectPlay,
                    EnableDirectStream = true, // Always allow direct stream
                    EnableTranscoding = true, // Always allow transcoding as fallback
                    AllowAudioStreamCopy = preferences.AllowAudioStreamCopy,
                    AllowVideoStreamCopy = true // Generally want to allow video stream copy when possible
                };
                
                if (needsClientSideResume)
                {
                    var reason = hasSubtitlesWithResume ? 
                        (hasAudioWithResume ? "Subtitle + Audio" : "Subtitle") : 
                        "Audio";
                    Logger.LogInformation($"{reason} + Resume detected: Will apply resume position {TimeSpan.FromTicks(_playbackParams.StartPositionTicks.Value):mm\\:ss} client-side");
                }

                Logger.LogInformation($"Requesting playback info with MediaSourceId: {_playbackParams?.MediaSourceId}");
                Logger.LogInformation($"AudioStreamIndex: {_playbackParams?.AudioStreamIndex}");
                Logger.LogInformation($"SubtitleStreamIndex: {_playbackParams?.SubtitleStreamIndex}");

                // Log StartTimeTicks being sent to server (even though server may ignore it for HLS)
                if (_playbackParams?.StartPositionTicks > 0)
                {
                    var resumeTime = TimeSpan.FromTicks(_playbackParams.StartPositionTicks.Value);
                    Logger.LogInformation($"StartTimeTicks: {_playbackParams.StartPositionTicks} ({resumeTime:hh\\:mm\\:ss})");
                    Logger.LogInformation($"Note: Server may ignore StartTimeTicks for HLS streams - client-side seek will be used");
                }
                else
                {
                    Logger.LogInformation("StartTimeTicks: 0 (starting from beginning)");
                }

                Logger.LogInformation("MaxStreamingBitrate: unlimited (adaptive)");
                Logger.LogInformation($"EnableDirectPlay: {preferences.EnableDirectPlay}");
                Logger.LogInformation($"EnableDirectStream: true");
                Logger.LogInformation($"EnableTranscoding: true");
                Logger.LogInformation($"AllowAudioStreamCopy: {preferences.AllowAudioStreamCopy}");
                Logger.LogInformation($"AllowVideoStreamCopy: true");

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
                _currentMediaSource = SelectBestMediaSource(playbackInfo.MediaSources);

                if (_currentMediaSource == null)
                {
                    throw new InvalidOperationException("No valid media source found");
                }

                // Log the MediaSource details
                Logger.LogInformation($"MediaSource TranscodingUrl: {_currentMediaSource.TranscodingUrl}");
                Logger.LogInformation($"MediaSource DirectStreamUrl: {GetDirectStreamUrl(_currentMediaSource)}");
                Logger.LogInformation($"MediaSource SupportsTranscoding: {_currentMediaSource.SupportsTranscoding}");

                var streamUrl = GetStreamUrl(_currentMediaSource, playbackInfo.PlaySessionId);
                Logger.LogInformation("Stream URL: {StreamUrl}", streamUrl);

                // Detect and track HLS streams
                _isHlsStream = _currentMediaSource.TranscodingUrl?.Contains(".m3u8") == true;

                // Create adaptive media source for HLS/DASH
                if (_currentMediaSource.TranscodingUrl?.Contains(".m3u8") == true ||
                    _currentMediaSource.TranscodingUrl?.Contains(".mpd") == true)
                {
                    Logger.LogInformation($"Creating AdaptiveMediaSource for HLS/DASH stream");
                    Logger.LogInformation($"URL being passed to AdaptiveMediaSource: {streamUrl}");

                    // Server controls adaptive streaming configuration
                    Logger.LogInformation("Using server-configured streaming settings");

                    // Log detailed URL components for debugging
                    var uri = new Uri(streamUrl);

                    // Check if API key is present in URL
                    var hasApiKey = streamUrl.Contains("api_key=");

                    // Log the PlaySessionId to correlate with server logs
                    if (streamUrl.Contains("PlaySessionId="))
                    {
                        var playSessionStart = streamUrl.IndexOf("PlaySessionId=") + 14;
                        var playSessionEnd = streamUrl.IndexOf("&", playSessionStart);
                        if (playSessionEnd == -1) playSessionEnd = streamUrl.Length;
                        var playSessionId = streamUrl.Substring(playSessionStart, playSessionEnd - playSessionStart);
                    }

                    // Log transcoding reasons if present in URL
                    if (streamUrl.Contains("TranscodeReasons="))
                    {
                        var reasonsStart = streamUrl.IndexOf("TranscodeReasons=") + 17;
                        var reasonsEnd = streamUrl.IndexOf("&", reasonsStart);
                        if (reasonsEnd == -1) reasonsEnd = streamUrl.Length;
                        var reasons = streamUrl.Substring(reasonsStart, reasonsEnd - reasonsStart);
                    }

                    // Skip HEAD request - it's causing 400 errors with the server
                    // Go directly to creating AdaptiveMediaSource

                    // Try without custom HttpClient - URL already has api_key for auth
                    var result = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(streamUrl));

                    // Log detailed result information
                    Logger.LogInformation($"[HLS-DEBUG] AdaptiveMediaSource.CreateFromUriAsync returned status: {result.Status}");
                    if (result.HttpResponseMessage != null)
                    {
                        Logger.LogInformation($"[HLS-DEBUG] HTTP Status Code: {result.HttpResponseMessage.StatusCode}");
                        Logger.LogInformation($"[HLS-DEBUG] HTTP Reason: {result.HttpResponseMessage.ReasonPhrase}");

                        // Response headers available in result.HttpResponseMessage.Headers if needed
                    }
                    else
                    {
                        Logger.LogWarning("[HLS-DEBUG] No HttpResponseMessage available in result");
                    }

                    if (result.Status == AdaptiveMediaSourceCreationStatus.Success)
                    {
                        var adaptiveSource = result.MediaSource;

                        // Log manifest info
                        if (adaptiveSource.AvailableBitrates != null && adaptiveSource.AvailableBitrates.Count > 0)
                        {
                            Logger.LogInformation($"Initial bitrate: {adaptiveSource.InitialBitrate / 1000}kbps");
                            Logger.LogInformation($"Available bitrates: {adaptiveSource.AvailableBitrates.Count}");
                        }

                        // Subscribe to bitrate change events for monitoring
                        adaptiveSource.DownloadBitrateChanged += OnDownloadBitrateChanged;
                        adaptiveSource.PlaybackBitrateChanged += OnPlaybackBitrateChanged;

                        return MediaSource.CreateFromAdaptiveMediaSource(adaptiveSource);
                    }
                    else
                    {
                        Logger.LogError($"Failed to create AdaptiveMediaSource: {result.Status}");

                        // Log additional failure details
                        Logger.LogError($"[HLS-DEBUG] Failure Details:");
                        Logger.LogError($"[HLS-DEBUG]   Status: {result.Status}");
                        Logger.LogError($"[HLS-DEBUG]   ExtendedError: {result.ExtendedError?.Message ?? "None"}");
                        Logger.LogError($"[HLS-DEBUG]   ExtendedError HResult: {result.ExtendedError?.HResult ?? 0}");

                        // Log possible causes based on status
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

                        throw new InvalidOperationException($"AdaptiveMediaSource creation failed: {result.Status}");
                    }
                }

                // Create regular media source
                return MediaSource.CreateFromUri(new Uri(streamUrl));
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

                if (shouldResume)
                {
                    // Always store pending resume position for client-side fallback
                    _pendingResumePosition = resumePosition;

                    // Log the tiered approach we'll use
                    Logger.LogInformation($"[RESUME-TIERED] Resume requested to {resumePosition:hh\\:mm\\:ss}");
                    Logger.LogInformation($"[RESUME-TIERED] Tier 1: StartTimeTicks already sent to server in PlaybackInfoDto");
                    Logger.LogInformation($"[RESUME-TIERED] Tier 2: Will apply client-side seek if position is not at resume point");

                    if (_isHlsStream)
                    {
                        Logger.LogInformation($"[RESUME-TIERED] Tier 3: HLS detected - will track manifest offset if server creates new manifest");
                    }
                }

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

        public bool ApplyPendingResumePosition()
        {
            if (!_pendingResumePosition.HasValue || _mediaPlayer?.PlaybackSession == null)
            {
                return false;
            }

            // HLS WORKAROUND: Use enhanced resume logic for HLS streams
            // Can be simplified when Jellyfin server properly handles HLS resume
            if (_isHlsStream)
            {
                return ApplyHlsResumePosition();
            }

            // For non-HLS streams, use simple resume
            return ApplySimpleResumePosition();
        }

        /// <summary>
        /// Enhanced resume for non-HLS streams with validation and stuck detection
        /// </summary>
        private bool ApplySimpleResumePosition()
        {
            if (!_pendingResumePosition.HasValue || _mediaPlayer?.PlaybackSession == null)
            {
                return false;
            }

            var resumePosition = _pendingResumePosition.Value;

            // Initialize tracking on first attempt (reuse same variables as HLS)
            if (_lastPositionCheckTime == DateTime.MinValue && _hlsResumeStartTime == DateTime.MinValue)
            {
                _hlsResumeStartTime = DateTime.UtcNow; // Reuse for timeout tracking
            }

            // Check for overall timeout - don't try forever
            if (_hlsResumeStartTime != DateTime.MinValue)
            {
                var totalElapsed = DateTime.UtcNow - _hlsResumeStartTime;
                if (totalElapsed.TotalSeconds > 20) // 20 second timeout for DirectPlay (shorter than HLS)
                {
                    Logger.LogError($"[DirectPlay-TIMEOUT] Resume operation timed out after {totalElapsed.TotalSeconds:F1}s");
                    _pendingResumePosition = null;
                    return false;
                }
            }

            try
            {
                var playbackSession = _mediaPlayer.PlaybackSession;
                var currentState = playbackSession.PlaybackState;
                var currentPosition = playbackSession.Position;
                var naturalDuration = playbackSession.NaturalDuration;

                // Check if media is ready for seeking
                if (currentState == MediaPlaybackState.Opening)
                {
                    Logger.LogInformation($"[DirectPlay] Media still opening, deferring resume to {resumePosition:hh\\:mm\\:ss}");
                    return false; // Will be retried
                }

                // For DirectPlay, wait for initial buffering to complete if at position 0
                if (currentState == MediaPlaybackState.Buffering && currentPosition == TimeSpan.Zero)
                {
                    Logger.LogInformation("[DirectPlay] Waiting for initial buffering to complete before resume");
                    return false; // Will be retried
                }

                // Check if we're already at the target position (within 2 seconds)
                var positionDiff = Math.Abs((currentPosition - resumePosition).TotalSeconds);
                if (positionDiff <= 2.0)
                {
                    // For DirectPlay, also verify playback is actually advancing before marking complete
                    // This prevents marking as complete when stuck buffering at the resume position

                    if (_lastPositionCheckTime == DateTime.MinValue)
                    {
                        // First time reaching target position - start monitoring
                        Logger.LogInformation($"[DirectPlay] Reached target position {currentPosition:hh\\:mm\\:ss}, verifying playback is advancing...");
                        _lastVerifiedPosition = currentPosition;
                        _lastPositionCheckTime = DateTime.UtcNow;
                        Interlocked.Exchange(ref _stuckPositionCount, 0);
                        return false; // Need to verify position advances
                    }

                    var timeSinceLastCheck = DateTime.UtcNow - _lastPositionCheckTime;
                    var positionChange = Math.Abs((currentPosition - _lastVerifiedPosition).TotalSeconds);

                    // Need at least 1 second between checks for meaningful comparison
                    if (timeSinceLastCheck.TotalSeconds < 1)
                    {
                        return false; // Too soon to check
                    }

                    if (positionChange < STUCK_POSITION_TOLERANCE)
                    {
                        Interlocked.Increment(ref _stuckPositionCount);
                        Logger.LogWarning($"[DirectPlay-STUCK] Position not advancing at resume point {currentPosition:hh\\:mm\\:ss} (count: {_stuckPositionCount}/{MAX_STUCK_CHECKS})");

                        if (_stuckPositionCount >= MAX_STUCK_CHECKS)
                        {
                            Logger.LogError($"[DirectPlay-STUCK] Playback is stuck at {currentPosition:hh\\:mm\\:ss} after resume. Giving up.");
                            _pendingResumePosition = null;
                            return false; // Report failure
                        }

                        // Try recovery techniques for DirectPlay
                        if (_stuckPositionCount == 2 && currentState == MediaPlaybackState.Playing)
                        {
                            Logger.LogInformation("[DirectPlay-STUCK] Attempting to unstick with pause/play");
                            _mediaPlayer.Pause();
                            // Use async delay without blocking
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false);
                                _mediaPlayer.Play();
                            });
                        }
                        else if (_stuckPositionCount == 3)
                        {
                            Logger.LogInformation("[DirectPlay-STUCK] Attempting to unstick with small seek forward");
                            playbackSession.Position = currentPosition + TimeSpan.FromSeconds(1);
                            _recoveryAttemptLevel = 2; // Mark that we did a forward seek
                        }

                        _lastPositionCheckTime = DateTime.UtcNow;
                        _lastVerifiedPosition = currentPosition;
                        return false; // Continue checking
                    }
                    else
                    {
                        // Position changed, but need to verify it's not just our recovery seek
                        if (_recoveryAttemptLevel == 2 && positionChange <= 1.5)
                        {
                            // Position only advanced due to our recovery seek, not actual playback
                            Logger.LogWarning($"[DirectPlay-STUCK] Position change ({positionChange:F1}s) appears to be from recovery seek, not actual playback");
                            _recoveryAttemptLevel = 0;
                            Interlocked.Increment(ref _stuckPositionCount);

                            if (_stuckPositionCount >= MAX_STUCK_CHECKS)
                            {
                                Logger.LogError($"[DirectPlay-STUCK] Playback is stuck at {currentPosition:hh\\:mm\\:ss} after resume. Giving up.");
                                _pendingResumePosition = null;
                                return false;
                            }

                            _lastPositionCheckTime = DateTime.UtcNow;
                            _lastVerifiedPosition = currentPosition;
                            return false;
                        }

                        // Position is truly advancing! Resume successful
                        Logger.LogInformation($"[DirectPlay] Resume successful! Position advancing from {_lastVerifiedPosition:hh\\:mm\\:ss} to {currentPosition:hh\\:mm\\:ss}");
                        _pendingResumePosition = null;
                        Interlocked.Exchange(ref _stuckPositionCount, 0);
                        _recoveryAttemptLevel = 0;
                        return true;
                    }
                }

                // Validate resume position against duration if available
                if (naturalDuration > TimeSpan.Zero && resumePosition >= naturalDuration)
                {
                    // Adjust resume position to be 10 seconds before the end
                    resumePosition = naturalDuration - TimeSpan.FromSeconds(10);
                    Logger.LogWarning($"[DirectPlay] Resume position adjusted from {_pendingResumePosition.Value:hh\\:mm\\:ss} to {resumePosition:hh\\:mm\\:ss}");
                }

                // Apply the seek
                Logger.LogInformation($"[DirectPlay] Seeking from {currentPosition:hh\\:mm\\:ss} to {resumePosition:hh\\:mm\\:ss}");
                playbackSession.Position = resumePosition;

                // Don't clear pending position yet - wait for verification that playback advances
                Logger.LogInformation($"[DirectPlay] Seek initiated to {resumePosition:hh\\:mm\\:ss}, will verify on next check");
                return false; // Will verify on next attempt
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"[DirectPlay] Failed to set resume position to {resumePosition:hh\\:mm\\:ss}");
                // Keep _pendingResumePosition so it can be retried
                return false;
            }
        }

        /// <summary>
        /// Check if playback is stuck at a given position
        /// </summary>
        private StuckStatus CheckIfStuck(TimeSpan currentPos, TimeSpan lastPos, TimeSpan elapsed)
        {
            // Need at least 1 second between checks for meaningful comparison
            if (elapsed.TotalSeconds < 1)
            {
                return StuckStatus.NotStuck; // Too soon to check
            }

            var positionChange = Math.Abs((currentPos - lastPos).TotalSeconds);

            if (positionChange < STUCK_POSITION_TOLERANCE)
            {
                // Position hasn't moved enough
                if (_stuckPositionCount >= MAX_STUCK_CHECKS)
                {
                    return StuckStatus.DefinitelyStuck;
                }
                return StuckStatus.PossiblyStuck;
            }

            // Position is advancing normally
            return StuckStatus.NotStuck;
        }

        /// <summary>
        /// HLS WORKAROUND: Enhanced resume logic for HLS streams with retry and validation
        /// Can be simplified when Jellyfin server properly handles HLS resume
        /// 
        /// Known HLS Seeking Issues (as of Jellyfin 10.10.x):
        /// - Large seeks (>5 minutes) often fail because server restarts ffmpeg transcode
        /// - Server uses -noaccurate_seek which seeks to nearest keyframe before target
        /// - After seek, server restarts ffmpeg with new -start_number that may not align with manifest
        /// - Client's AdaptiveMediaSource may get stuck waiting for segments after transcode restart
        /// - Server needs 3-5 seconds to restart transcode and generate new segments after seek
        /// 
        /// Workaround Components:
        /// 1. State machine tracks resume progress (NotStarted -> InProgress -> Verifying -> Succeeded/Failed)
        /// 2. Position verification ensures playback actually advances (not stuck buffering)
        /// 3. Recovery attempts when stuck: pause/play toggle, small forward seek, backward seek
        /// 4. Configurable retry parameters for HLS vs DirectPlay streams
        /// 5. HlsManifestOffset tracking when server creates new manifest at different position
        /// </summary>
        private bool ApplyHlsResumePosition()
        {
            if (_resumeState == ResumeState.Failed)
            {
                return false;
            }

            if (_resumeState == ResumeState.Succeeded)
            {
                return true;
            }

            var resumePosition = _pendingResumePosition.Value;

            // Get appropriate retry config
            var config = _isHlsStream ? RetryConfig.ForHls : RetryConfig.ForDirectPlay;

            // Initialize resume tracking on first attempt
            if (_resumeState == ResumeState.NotStarted)
            {
                _resumeState = ResumeState.InProgress;
                _hlsResumeStartTime = DateTime.UtcNow;
                Logger.LogInformation($"[HLS-RESUME] Starting resume to {resumePosition:hh\\:mm\\:ss}");
            }

            Interlocked.Increment(ref _hlsResumeAttempts);

            // Check if we've exceeded max attempts
            if (_hlsResumeAttempts > config.MaxAttempts)
            {
                Logger.LogError($"[HLS-RESUME] Exceeded max attempts ({config.MaxAttempts}), giving up");
                _pendingResumePosition = null;
                return false;
            }

            try
            {
                var playbackSession = _mediaPlayer.PlaybackSession;
                var currentState = playbackSession.PlaybackState;
                var currentPosition = playbackSession.Position;
                var naturalDuration = playbackSession.NaturalDuration;

                Logger.LogInformation($"[HLS-RESUME] Attempt {_hlsResumeAttempts}: State={currentState}, Pos={currentPosition:mm\\:ss}, Duration={naturalDuration:mm\\:ss}");

                // For HLS, be extra careful about when to seek
                // Wait if media is still opening or initial buffering
                if (currentState == MediaPlaybackState.Opening ||
                    (currentState == MediaPlaybackState.Buffering && currentPosition == TimeSpan.Zero))
                {
                    Logger.LogInformation("[HLS-RESUME] Media still loading, will retry");
                    return false; // Will be retried by MediaPlayerViewModel
                }

                // For first attempt on HLS, wait for Playing state to ensure manifest is stable
                if (_hlsResumeAttempts == 1 && currentState != MediaPlaybackState.Playing)
                {
                    Logger.LogInformation($"[HLS-RESUME] Waiting for Playing state (current: {currentState})");
                    return false;
                }

                // Ensure we have a valid duration for HLS
                if (naturalDuration == TimeSpan.Zero)
                {
                    Logger.LogWarning("[HLS-RESUME] Duration not available yet, will retry");
                    return false;
                }

                // Check if we're already at the target position (within tolerance)
                var positionDiff = Math.Abs((currentPosition - resumePosition).TotalSeconds);

                // HLS WORKAROUND: Handle server creating new manifest at different position
                // Can be removed when Jellyfin server properly handles HLS resume
                if (_hlsResumeAttempts >= 2 && currentState == MediaPlaybackState.Buffering &&
                    positionDiff <= 15.0) // Within 15 seconds of target (typical keyframe distance)
                {
                    Logger.LogWarning($"[HLS-RESUME] Detected stuck buffering at {currentPosition:mm\\:ss} (target was {resumePosition:mm\\:ss})");
                    Logger.LogInformation($"[HLS-RESUME] Server created new manifest with -noaccurate_seek. Applying HLS offset workaround.");

                    // The server has created a new manifest starting at roughly the resume position
                    // Store the offset so MediaPlayerViewModel can display correct position
                    HlsManifestOffset = currentPosition;

                    // Seek to position 0 to play from the start of this new manifest
                    playbackSession.Position = TimeSpan.Zero;

                    Logger.LogInformation($"[HLS-OFFSET] Set manifest offset to {HlsManifestOffset:mm\\:ss}. Position 0 = {HlsManifestOffset:mm\\:ss} in actual media time.");

                    // Mark as succeeded since we've applied the workaround
                    _resumeState = ResumeState.Succeeded;
                    _pendingResumePosition = null;

                    return true; // Report success to stop retrying
                }

                if (positionDiff <= config.ToleranceSeconds)
                {
                    // For HLS, we must verify the position is actually advancing before marking complete
                    // This prevents marking as complete when stuck buffering at the resume position

                    // Check for overall timeout - don't try forever
                    if (_hlsResumeStartTime != DateTime.MinValue)
                    {
                        var totalElapsed = DateTime.UtcNow - _hlsResumeStartTime;
                        if (totalElapsed.TotalSeconds > 30) // 30 second overall timeout
                        {
                            Logger.LogError($"[HLS-RESUME-TIMEOUT] Resume operation timed out after {totalElapsed.TotalSeconds:F1}s at position {currentPosition:mm\\:ss}");
                            _pendingResumePosition = null;
                            _resumeState = ResumeState.Failed;
                            return false;
                        }
                    }

                    if (_resumeState != ResumeState.Verifying && _resumeState != ResumeState.RecoveryNeeded)
                    {
                        // First time reaching target position - start monitoring
                        _resumeState = ResumeState.Verifying;
                        Logger.LogInformation($"[HLS-RESUME] Reached target position {currentPosition:mm\\:ss}, verifying playback is advancing...");
                        _lastVerifiedPosition = currentPosition;
                        _lastPositionCheckTime = DateTime.UtcNow;
                        Interlocked.Exchange(ref _stuckPositionCount, 0);
                        return false; // Need to verify position advances
                    }

                    // Add debug logging to track state transitions
                    var timeSinceLastCheck = DateTime.UtcNow - _lastPositionCheckTime;
                    var positionChange = Math.Abs((currentPosition - _lastVerifiedPosition).TotalSeconds);

                    // Need at least 1 second between checks for meaningful comparison
                    if (timeSinceLastCheck.TotalSeconds < 1)
                    {
                        return false; // Too soon to check
                    }

                    if (positionChange < STUCK_POSITION_TOLERANCE)
                    {
                        Interlocked.Increment(ref _stuckPositionCount);
                        Logger.LogWarning($"[HLS-STUCK] Position not advancing at resume point {currentPosition:mm\\:ss} (count: {_stuckPositionCount}/{MAX_STUCK_CHECKS})");

                        // Update last check time and position for next comparison
                        _lastPositionCheckTime = DateTime.UtcNow;
                        _lastVerifiedPosition = currentPosition;

                        if (_stuckPositionCount >= MAX_STUCK_CHECKS)
                        {
                            Logger.LogError($"[HLS-STUCK] Playback is stuck at {currentPosition:mm\\:ss} after resume. Giving up.");
                            _pendingResumePosition = null;
                            _resumeState = ResumeState.Failed;
                            return false;
                        }

                        // Try recovery techniques based on attempt level
                        if (_resumeState != ResumeState.RecoveryNeeded)
                        {
                            _resumeState = ResumeState.RecoveryNeeded;
                            _recoveryAttemptLevel = 0;
                        }

                        _recoveryAttemptLevel++;
                        if (_recoveryAttemptLevel == 1 && currentState == MediaPlaybackState.Playing)
                        {
                            Logger.LogInformation("[HLS-STUCK] Recovery level 1: Attempting pause/play toggle");
                            _mediaPlayer.Pause();
                            // Use async delay without blocking
                            _ = System.Threading.Tasks.Task.Run(async () =>
                            {
                                await System.Threading.Tasks.Task.Delay(100).ConfigureAwait(false);
                                _mediaPlayer.Play();
                            });
                        }
                        else if (_recoveryAttemptLevel == 2)
                        {
                            Logger.LogInformation("[HLS-STUCK] Recovery level 2: Small seek forward (1s)");
                            playbackSession.Position = currentPosition + TimeSpan.FromSeconds(1);
                        }
                        else if (_recoveryAttemptLevel == 3 && currentState == MediaPlaybackState.Buffering)
                        {
                            Logger.LogInformation("[HLS-STUCK] Recovery level 3: Seek back 5 seconds");
                            var restartPosition = currentPosition - TimeSpan.FromSeconds(5);
                            if (restartPosition < TimeSpan.Zero) restartPosition = TimeSpan.Zero;
                            playbackSession.Position = restartPosition;
                        }

                        _lastPositionCheckTime = DateTime.UtcNow;
                        _lastVerifiedPosition = currentPosition;
                        return false; // Continue checking
                    }
                    else
                    {
                        // Position changed, but need to verify it's not just our recovery seek
                        if (_recoveryAttemptLevel == 2 && positionChange <= 1.5)
                        {
                            // Position only advanced due to our recovery seek, not actual playback
                            Logger.LogWarning($"[HLS-STUCK] Position change ({positionChange:F1}s) appears to be from recovery seek, not actual playback");
                            _recoveryAttemptLevel++; // Move to next recovery level
                            Interlocked.Increment(ref _stuckPositionCount);

                            if (_stuckPositionCount >= MAX_STUCK_CHECKS)
                            {
                                Logger.LogError($"[HLS-STUCK] Playback is stuck at {currentPosition:mm\\:ss} after resume. Giving up.");
                                _pendingResumePosition = null;
                                _resumeState = ResumeState.Failed;
                                return false;
                            }

                            _lastPositionCheckTime = DateTime.UtcNow;
                            _lastVerifiedPosition = currentPosition;
                            return false;
                        }

                        // Position is truly advancing! Resume successful
                        Logger.LogInformation($"[HLS-RESUME] Resume successful! Position advancing from {_lastVerifiedPosition:mm\\:ss} to {currentPosition:mm\\:ss}");

                        // Log if we accepted an inaccurate seek
                        var originalTarget = TimeSpan.FromTicks(_playbackParams?.StartPositionTicks ?? 0);
                        var acceptedDiff = Math.Abs((currentPosition - originalTarget).TotalSeconds);
                        if (acceptedDiff > 3.0)
                        {
                            Logger.LogInformation($"[HLS-RESUME] Playback resumed at {currentPosition:mm\\:ss} (originally requested {originalTarget:mm\\:ss}, diff: {acceptedDiff:F1}s)");
                        }

                        _resumeState = ResumeState.Succeeded;
                        _pendingResumePosition = null;
                        Interlocked.Exchange(ref _stuckPositionCount, 0);
                        _recoveryAttemptLevel = 0;
                        return true;
                    }
                }

                // Validate and adjust resume position
                var adjustedPosition = resumePosition;
                if (adjustedPosition >= naturalDuration)
                {
                    adjustedPosition = naturalDuration - TimeSpan.FromSeconds(10);
                    Logger.LogWarning($"[HLS-RESUME] Adjusted position from {resumePosition:mm\\:ss} to {adjustedPosition:mm\\:ss}");
                }

                // For HLS, ensure we're not seeking during active buffering
                if (currentState == MediaPlaybackState.Buffering && _hlsResumeAttempts > 1)
                {
                    Logger.LogInformation("[HLS-RESUME] Waiting for buffering to complete before seek");
                    return false;
                }

                // Apply the seek
                Logger.LogInformation($"[HLS-RESUME] Seeking from {currentPosition:mm\\:ss} to {adjustedPosition:mm\\:ss}");
                playbackSession.Position = adjustedPosition;

                // Note: Server may perform inaccurate seek due to keyframe alignment
                // For HLS, we'll verify on next attempt
                if (_hlsResumeAttempts < config.MaxAttempts)
                {
                    Logger.LogInformation("[HLS-RESUME] Seek initiated, will verify on next check");
                    return false; // Will be verified on next call
                }

                _resumeState = ResumeState.Succeeded;
                _pendingResumePosition = null;
                Logger.LogInformation($"[HLS-RESUME] Resume completed after {_hlsResumeAttempts} attempts");
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"[HLS-RESUME] Error during attempt {_hlsResumeAttempts}");

                // Continue retrying unless we've hit the limit
                if (_hlsResumeAttempts >= config.MaxAttempts)
                {
                    _pendingResumePosition = null;
                    _resumeState = ResumeState.Failed;
                    return false;
                }

                return false; // Will retry
            }
        }

        /// <summary>
        /// Check if HLS resume is still in progress
        /// </summary>
        public bool IsHlsResumeInProgress()
        {
            return _isHlsStream && _pendingResumePosition.HasValue &&
                   _resumeState != ResumeState.Succeeded && _resumeState != ResumeState.Failed;
        }

        /// <summary>
        /// Get HLS resume status for diagnostics
        /// </summary>
        public (bool InProgress, int Attempts, TimeSpan? Target) GetHlsResumeStatus()
        {
            return (IsHlsResumeInProgress(), _hlsResumeAttempts, _pendingResumePosition);
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

        public async Task<List<SkipSegment>> GetSkipSegmentsAsync(string itemId)
        {
            try
            {
                // Skip segments API not available in current SDK
                var segments = new List<SkipSegment>();
                return await Task.FromResult(segments);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to get skip segments for item {ItemId}", itemId);
                return new List<SkipSegment>();
            }
        }

        protected override void UnsubscribeEvents()
        {
            UnsubscribeFromPlaybackEvents(_mediaPlayer);
            base.UnsubscribeEvents();
        }

        private MediaSourceInfo SelectBestMediaSource(List<MediaSourceInfo> sources)
        {
            if (sources == null || sources.Count == 0)
            {
                Logger.LogError($"SelectBestMediaSource: No media sources available (count: {sources?.Count ?? 0})");
                return null;
            }

            Logger.LogInformation($"SelectBestMediaSource: Evaluating {sources.Count} media sources");

            // Log details of each source
            foreach (var source in sources)
            {
                Logger.LogInformation($"  Source: Id={source.Id}, DirectPlay={source.SupportsDirectPlay}, DirectStream={source.SupportsDirectStream}, Transcoding={source.SupportsTranscoding}");
                Logger.LogInformation($"    TranscodingUrl: {source.TranscodingUrl}");
                Logger.LogInformation($"    Path: {source.Path}");
                Logger.LogInformation($"    Container: {source.Container}");
                Logger.LogInformation($"    Bitrate: {source.Bitrate}");
            }

            // Prefer direct play sources
            var directPlaySource = sources.FirstOrDefault(s => s.SupportsDirectPlay == true);
            if (directPlaySource != null)
            {
                Logger.LogInformation($"Selected DirectPlay source: {directPlaySource.Id}");
                return directPlaySource;
            }

            // Then prefer direct stream
            var directStreamSource = sources.FirstOrDefault(s => s.SupportsDirectStream == true);
            if (directStreamSource != null)
            {
                Logger.LogInformation($"Selected DirectStream source: {directStreamSource.Id}");
                return directStreamSource;
            }

            // Finally, use transcoding
            var transcodingSource = sources.FirstOrDefault();
            if (transcodingSource != null)
            {
                Logger.LogInformation($"Selected Transcoding source: {transcodingSource.Id}");
            }
            return transcodingSource;
        }

        private string GetStreamUrl(MediaSourceInfo mediaSource, string playSessionId)
        {
            string url;

            // If we have a transcoding URL from the server, use it
            if (!string.IsNullOrEmpty(mediaSource.TranscodingUrl))
            {
                // Transcoding URL is relative, need to prepend server URL
                var baseUrl = _authService.ServerUrl?.TrimEnd('/') ?? "";
                url = $"{baseUrl}{mediaSource.TranscodingUrl}";

                // Server-provided transcoding URLs are complete - do not modify them
                // The server already includes all necessary parameters
                // IMPORTANT: We don't modify URLs here to maintain SDK compatibility
            }
            else if (!string.IsNullOrEmpty(GetDirectStreamUrl(mediaSource)))
            {
                // Direct stream URL from server
                var baseUrl = _authService.ServerUrl?.TrimEnd('/') ?? "";
                url = $"{baseUrl}{GetDirectStreamUrl(mediaSource)}";
            }
            else
            {
                // Use SDK to build direct play URL
                if (!_currentItem?.Id.HasValue ?? true)
                {
                    Logger.LogError("Cannot build direct play URL - current item has no ID");
                    throw new InvalidOperationException("Current item has no ID");
                }

                // Build URL using SDK for direct streaming
                var requestInfo = _apiClient.Videos[_currentItem.Id.Value].Stream.ToGetRequestInformation(config =>
                {
                    config.QueryParameters.Static = true;
                    config.QueryParameters.MediaSourceId = mediaSource.Id;
                    config.QueryParameters.PlaySessionId = playSessionId;
                    config.QueryParameters.DeviceId = _deviceService.GetDeviceId();

                    // Add stream indices if present
                    if (_playbackParams?.AudioStreamIndex >= 0)
                    {
                        config.QueryParameters.AudioStreamIndex = _playbackParams.AudioStreamIndex.Value;
                    }

                    if (_playbackParams?.SubtitleStreamIndex >= 0)
                    {
                        config.QueryParameters.SubtitleStreamIndex = _playbackParams.SubtitleStreamIndex.Value;
                    }
                });

                var builtUri = _apiClient.BuildUri(requestInfo);
                url = builtUri.ToString();
            }

            // For transcoding URLs, we may need to add stream indices if not already present
            if (!string.IsNullOrEmpty(mediaSource.TranscodingUrl))
            {
                // Add AudioStreamIndex if present and not already in URL
                if (_playbackParams?.AudioStreamIndex.HasValue == true &&
                    _playbackParams.AudioStreamIndex.Value >= 0 &&
                    !url.Contains("AudioStreamIndex="))
                {
                    var separator = url.Contains('?') ? "&" : "?";
                    url += $"{separator}AudioStreamIndex={_playbackParams.AudioStreamIndex.Value}";
                    Logger.LogInformation($"Added AudioStreamIndex={_playbackParams.AudioStreamIndex.Value} to transcoding URL");
                }

                // Add SubtitleStreamIndex if present and not already in URL
                if (_playbackParams?.SubtitleStreamIndex.HasValue == true &&
                    _playbackParams.SubtitleStreamIndex.Value >= 0 &&
                    !url.Contains("SubtitleStreamIndex="))
                {
                    var separator = url.Contains('?') ? "&" : "?";
                    url += $"{separator}SubtitleStreamIndex={_playbackParams.SubtitleStreamIndex.Value}&SubtitleMethod=Encode";
                    Logger.LogInformation($"Added SubtitleStreamIndex={_playbackParams.SubtitleStreamIndex.Value} to transcoding URL");
                }
            }

            // Server-provided URLs already contain authentication (api_key parameter)
            // SDK-generated URLs use HTTP headers for authentication
            // Do not add duplicate API keys

            // Log resume handling approach
            if (_playbackParams?.StartPositionTicks > 0)
            {
                var seconds = _playbackParams.StartPositionTicks.Value / 10000000.0;
                var resumeTime = TimeSpan.FromTicks(_playbackParams.StartPositionTicks.Value);

                // Tiered approach for resume:
                // 1. StartTimeTicks was already sent in PlaybackInfoDto (always sent first)
                // 2. Client-side seek will be applied after playback starts
                // 3. For HLS, we'll track manifest offset if server creates new manifest

                Logger.LogInformation($"[RESUME-STRATEGY] StartTimeTicks sent to server: {_playbackParams.StartPositionTicks.Value} ({resumeTime:hh\\:mm\\:ss})");

                if (mediaSource.TranscodingUrl?.Contains(".m3u8") == true)
                {
                    Logger.LogInformation($"[RESUME-STRATEGY] HLS stream detected - will use client-side seek with offset tracking if server doesn't honor StartTimeTicks");
                }
                else
                {
                    Logger.LogInformation($"[RESUME-STRATEGY] Non-HLS stream - will use client-side seek if server doesn't honor StartTimeTicks");
                }
            }

            // Validate the final URL
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                Logger.LogError($"Invalid URL generated: {url}");
                throw new InvalidOperationException($"Generated URL is invalid: {url}");
            }

            Logger.LogInformation($"Stream URL generated: {url}");
            return url;
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

                // BufferingProgress often throws InvalidCastException for HLS streams
                try
                {
                    bufferProgress = sender.BufferingProgress;
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

        #region MediaSourceInfo Helper Methods

        private static MediaStream GetVideoStream(MediaSourceInfo source)
        {
            return source?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);
        }

        private static MediaStream GetDefaultAudioStream(MediaSourceInfo source)
        {
            return source?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Audio && s.IsDefault == true)
                   ?? source?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Audio);
        }

        private static string GetDirectStreamUrl(MediaSourceInfo source)
        {
            // DirectStreamUrl is not a direct property on MediaSourceInfo in the SDK
            // It needs to be constructed or accessed differently
            return source?.Path;
        }

        #endregion

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
                if (_mediaPlayer?.PlaybackSession == null)
                {
                    Logger.LogWarning($"Cannot restart playback for {restartReason} - media player not initialized");
                    return;
                }

                // Save the current item before any state changes
                var itemToReload = _currentItem ?? _playbackParams?.Item;
                if (itemToReload == null)
                {
                    Logger.LogError($"Cannot restart playback for {restartReason} - no current item available");
                    throw new InvalidOperationException("No current item available for playback restart");
                }

                // Save current position and state
                var currentPosition = _mediaPlayer.PlaybackSession.Position;
                var wasPlaying = _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

                Logger.LogInformation(
                    $"Restarting playback for {restartReason}. Current position: {currentPosition:hh\\:mm\\:ss\\.fff}");

                // Report playback stop to server before switching
                // This properly closes the old transcode session
                if (!string.IsNullOrEmpty(_playSessionId) && itemToReload?.Id != null)
                {
                    try
                    {
                        if (_mediaPlaybackService is IMediaSessionService sessionService)
                        {
                            await sessionService.ReportPlaybackStoppedAsync(
                                _playSessionId,
                                currentPosition.Ticks);
                            Logger.LogInformation($"Reported playback stop for {restartReason}");
                        }
                        else
                        {
                            Logger.LogWarning("MediaPlaybackService does not implement IMediaSessionService - cannot report playback stop");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "Failed to report playback stop before restart");
                    }
                }

                // Stop current playback
                _mediaControlService.Stop();

                // Update playback params with current position for proper resume
                _playbackParams.StartPositionTicks = currentPosition.Ticks;

                // Update stream indices if provided
                if (audioStreamIndex.HasValue)
                {
                    _playbackParams.AudioStreamIndex = audioStreamIndex.Value;
                }
                if (subtitleStreamIndex.HasValue)
                {
                    _playbackParams.SubtitleStreamIndex = subtitleStreamIndex.Value;
                }

                // Get new playback info with updated parameters
                var newPlaybackInfo = await GetPlaybackInfoAsync(itemToReload, maxBitrate);

                // Create new media source
                var mediaSource = await CreateMediaSourceAsync(newPlaybackInfo);

                // Start playback with saved position
                await StartPlaybackAsync(mediaSource, currentPosition.Ticks);

                // Report playback start to server with new session ID
                // This is critical for the server to begin transcoding
                if (!string.IsNullOrEmpty(_playSessionId) && itemToReload?.Id != null)
                {
                    if (_mediaPlaybackService is IMediaSessionService sessionService)
                    {
                        await sessionService.ReportPlaybackStartAsync(
                            _playSessionId,
                            currentPosition.Ticks);
                        Logger.LogInformation($"Reported playback start for {restartReason} with session {_playSessionId}");
                    }
                    else
                    {
                        Logger.LogWarning("MediaPlaybackService does not implement IMediaSessionService - cannot report playback start");
                    }
                }

                // Resume playback if it was playing
                if (wasPlaying)
                {
                    _mediaControlService.Play();
                }

                Logger.LogInformation($"Successfully restarted playback for {restartReason}");
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
