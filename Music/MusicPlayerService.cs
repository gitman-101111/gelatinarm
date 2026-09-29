using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Playback;
using Gelatinarm.Playback;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Device;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Server;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Music
{
    public interface IMusicPlayerService : IDisposable
    {
        MediaPlayer MediaPlayer { get; }

        bool IsPlaying { get; }

        BaseItemDto CurrentItem { get; }

        RepeatMode RepeatMode { get; }

        Task PlayItemAsync(BaseItemDto item);

        Task PlayItemsAsync(List<BaseItemDto> items, int startIndex = 0);

        /// <summary>
        ///     Plays <paramref name="items" /> in the queue's shuffle mode from a random track, so the
        ///     shuffle button shows on and turning it off returns to their order
        /// </summary>
        Task ShufflePlayAsync(List<BaseItemDto> items);

        /// <summary>
        ///     Fetches the server's instant mix seeded from <paramref name="seed" /> and plays it
        /// </summary>
        Task PlayInstantMixAsync(BaseItemDto seed);

        void Stop();

        /// <summary>
        ///     Stops playback and waits for the stopped report to reach the server. Use before
        ///     the signed-in user changes, so the report is sent under the current user's token.
        /// </summary>
        Task StopAsync();

        void Play();

        void Pause();

        void SkipNext();

        void SkipPrevious();

        void CycleRepeatMode();

        void SetShuffle(bool enabled);

        event EventHandler<BaseItemDto> NowPlayingChanged;

        event EventHandler<MediaPlaybackState> PlaybackStateChanged;

        event EventHandler<bool> ShuffleStateChanged;

        event EventHandler<RepeatMode> RepeatModeChanged;
    }

    public partial class MusicPlayerService : BaseService, IMusicPlayerService
    {
        private static readonly string[] Mp3Container = { "mp3" };

        private readonly JellyfinApiClient _apiClient;
        private readonly IAuthenticationService _authService;
        private readonly IUnifiedDeviceService _deviceService;
        private readonly IMediaControlService _mediaControlService;
        private readonly IMediaOptimizationService _mediaOptimizationService;
        private readonly IMediaPlaybackService _mediaPlaybackService;
        private readonly IMediaSessionService _mediaSessionService;
        private readonly IPlaybackQueueService _queueService;
        private readonly IUserProfileService _userProfileService;
        private MediaSourceInfo _currentMediaSource;
        private string _currentPlaySessionId;
        private bool _isInFallbackMode;
        private SystemMediaTransportControls _systemMediaTransportControls;
        private DateTime _smtcSuppressStoppedUntilUtc = DateTime.MinValue;

        private DateTime _lastPlaybackStartTime = DateTime.MinValue;
        private CancellationTokenSource _playbackCancellationTokenSource;
        private CancellationTokenSource _progressReportCancellationTokenSource;
        private CancellationTokenSource _transportControlsUpdateCts;
        private CancellationTokenSource _playbackStatusUpdateCts;
        private Timer _progressReportTimer;
        private bool _isSmtcInitialized;
        private bool _isSubscribedToEvents;

        public MusicPlayerService(
            ILogger<MusicPlayerService> logger,
            JellyfinApiClient apiClient,
            IAuthenticationService authService,
            IUserProfileService userProfileService,
            IMediaPlaybackService mediaPlaybackService,
            IMediaSessionService mediaSessionService,
            IUnifiedDeviceService deviceService,
            IMediaOptimizationService mediaOptimizationService,
            IPlaybackQueueService queueService,
            IMediaControlService mediaControlService) : base(logger)
        {
            _apiClient = apiClient;
            _authService = authService;
            _userProfileService = userProfileService;
            _mediaPlaybackService = mediaPlaybackService;
            _mediaSessionService = mediaSessionService;
            _deviceService = deviceService;
            _mediaOptimizationService = mediaOptimizationService;
            _queueService = queueService;
            _mediaControlService = mediaControlService;
        }

        public MediaPlayer MediaPlayer => _mediaControlService.MediaPlayer;
        public bool IsPlaying => _mediaControlService.IsPlaying;
        public BaseItemDto CurrentItem => _mediaControlService.CurrentItem;
        public RepeatMode RepeatMode => _mediaControlService.RepeatMode;

        public event EventHandler<BaseItemDto> NowPlayingChanged;
        public event EventHandler<MediaPlaybackState> PlaybackStateChanged;
        public event EventHandler<bool> ShuffleStateChanged;
        public event EventHandler<RepeatMode> RepeatModeChanged;

        public async Task PlayItemAsync(BaseItemDto item)
        {
            var context = CreateErrorContext("PlayItemAsync", ErrorCategory.Media);
            try
            {
                await PlayCurrentQueueItemAsync(item).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        public Task ShufflePlayAsync(List<BaseItemDto> items)
        {
            if (items == null || items.Count == 0)
            {
                return Task.CompletedTask;
            }

            SetShuffle(true);
            return PlayItemsAsync(items, ShuffleHelper.RandomIndex(items.Count));
        }

        public async Task PlayItemsAsync(List<BaseItemDto> items, int startIndex = 0)
        {
            var context = CreateErrorContext("PlayItemsAsync", ErrorCategory.Media);
            try
            {
                if (items is not { Count: > 0 })
                {
                    return;
                }

                for (var i = 0; i < items.Count; i++)
                {
                    Logger.LogDebug("  Queue[{I}]: {ItemsName} (ID: {ItemsId})", i, items[i].Name, items[i].Id);
                }

                SetQueue(items, startIndex);

                if (_queueService.CurrentQueueIndex >= 0 && _queueService.CurrentQueueIndex < _queueService.Queue.Count)
                {
                    await PlayItemAsync(_queueService.Queue[_queueService.CurrentQueueIndex]).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        public async Task PlayInstantMixAsync(BaseItemDto seed)
        {
            var userId = _userProfileService.GetCurrentUserGuid();
            if (seed?.Id == null || !userId.HasValue)
            {
                return;
            }

            var context = CreateErrorContext("PlayInstantMix", ErrorCategory.Media);
            try
            {
                var instantMix = await _apiClient.Items[seed.Id.Value].InstantMix.GetAsync(config =>
                {
                    config.QueryParameters.UserId = userId.Value;
                    config.QueryParameters.Limit = MusicConstants.MaxDiscoveryQueryLimit;
                    config.QueryParameters.Fields = new[] { ItemFields.MediaSources };
                }).ConfigureAwait(false);

                if (instantMix?.Items is { Count: > 0 })
                {
                    Logger.LogInformation(
                        "Starting instant mix for '{SeedName}' with {ItemsCount} tracks", seed.Name, instantMix.Items.Count);
                    await PlayItemsAsync(instantMix.Items.ToList()).ConfigureAwait(false);
                }
                else
                {
                    Logger.LogWarning("No instant mix items returned for '{SeedName}'", seed.Name);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context).ConfigureAwait(false);
            }
        }

        public void Stop()
        {
            FireAndForget(() => StopCoreAsync(false));
        }

        public Task StopAsync()
        {
            return StopCoreAsync(true);
        }

        private async Task StopCoreAsync(bool awaitStopReport)
        {
            var context = CreateErrorContext("Stop", ErrorCategory.Media);
            try
            {
                AsyncHelper.Cancel(ref _playbackCancellationTokenSource);

                // Before Stop, which clears the current item
                var stopReport = CreateReport(_mediaControlService.Position.Ticks);

                _mediaControlService.Stop();

                StopProgressReporting();

                if (_isSmtcInitialized)
                {
                    Logger.LogDebug("Disposing System Media Transport Controls");
                    DisposeSystemMediaTransportControls();
                    _isSmtcInitialized = false;
                }

                _currentMediaSource = null;
                _currentPlaySessionId = null;

                UnsubscribeFromEvents();

                // Notify UI that nothing is playing — OnNowPlayingChanged can't propagate this
                // because _playbackCancellationTokenSource is already cancelled above
                NowPlayingChanged?.Invoke(this, null);

                // Report playback stopped: awaited when the caller is about to change the
                // signed-in user (the report must go out under the current user's token),
                // otherwise fire and forget so backing out stays fast
                if (stopReport != null)
                {
                    if (awaitStopReport)
                    {
                        var report = _mediaSessionService.ReportPlaybackStoppedAsync(stopReport);
                        var finished = await Task.WhenAny(report,
                            Task.Delay(MusicConstants.StopReportBeforeUserChangeTimeoutMs)).ConfigureAwait(false);
                        if (finished == report)
                        {
                            await report.ConfigureAwait(false); // surface a failure to the catch below
                            Logger.LogDebug("Reported playback stopped");
                        }
                        else
                        {
                            Logger.LogWarning("Playback stopped report did not complete in time; continuing");
                        }
                    }
                    else
                    {
                        FireAndForget(() => _mediaSessionService.ReportPlaybackStoppedAsync(stopReport),
                            "ReportPlaybackStopped");
                    }
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public void Play()
        {
            var context = CreateErrorContext("Play", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                _mediaControlService.Play();
                UpdateTransportControlsState();
            });
        }

        public void Pause()
        {
            var context = CreateErrorContext("Pause", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                _mediaControlService.Pause();

                UpdateTransportControlsState();
            });
        }

        public void SkipNext()
        {
            SkipTo("SkipNext", repeatAll => _queueService.GetNextIndex(repeatAll));
        }

        public void SkipPrevious()
        {
            SkipTo("SkipPrevious", repeatAll => _queueService.GetPreviousIndex(repeatAll));
        }

        private void SkipTo(string operation, Func<bool, int> getIndex)
        {
            var context = CreateErrorContext(operation, ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                if (_queueService.Queue.Count == 0)
                {
                    return;
                }

                var index = getIndex(_mediaControlService.RepeatMode == RepeatMode.All);
                if (index >= 0)
                {
                    _queueService.SetCurrentIndex(index);
                    FireAndForget(() => PlayItemAsync(_queueService.Queue[index]), "PlayItemAsync");
                }
            });
        }

        public void CycleRepeatMode()
        {
            var context = CreateErrorContext("CycleRepeatMode", ErrorCategory.Media);
            ErrorHandler.Run(context, () => ApplyRepeatMode(RepeatMode switch
            {
                RepeatMode.None => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                _ => RepeatMode.None
            }));
        }

        public void SetShuffle(bool enabled)
        {
            var context = CreateErrorContext("SetShuffle", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                _queueService.SetShuffle(enabled);
                UpdateTransportControlsState();
                ShuffleStateChanged?.Invoke(this, enabled);
            });
        }

        // Every repeat change, from the mini player, the system controls or a new queue. The
        // transport controls' update also sets their repeat and shuffle state.
        private void ApplyRepeatMode(RepeatMode mode)
        {
            _mediaControlService.SetRepeatMode(mode);
            UpdateTransportControlsState();
            RepeatModeChanged?.Invoke(this, mode);
        }

        private void SetQueue(List<BaseItemDto> items, int startIndex)
        {
            _queueService.SetQueue(items, startIndex);

            if (_mediaControlService.RepeatMode == RepeatMode.One)
            {
                Logger.LogDebug("Resetting repeat-one mode for new queue");
                ApplyRepeatMode(RepeatMode.None);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopProgressReporting();

                AsyncHelper.Cancel(ref _playbackCancellationTokenSource);
                AsyncHelper.Cancel(ref _transportControlsUpdateCts);
                AsyncHelper.Cancel(ref _playbackStatusUpdateCts);

                UnsubscribeFromEvents();

                DisposeSystemMediaTransportControls();
                (_mediaControlService as IDisposable)?.Dispose();
            }

            base.Dispose(disposing);
        }

        private void SubscribeToEvents()
        {
            if (_isSubscribedToEvents)
            {
                Logger.LogDebug("Already subscribed to events, skipping");
                return;
            }

            _mediaControlService.NowPlayingChanged += OnNowPlayingChanged;
            _mediaControlService.PlaybackStateChanged += OnPlaybackStateChanged;
            _mediaControlService.MediaFailed += OnMediaFailed;
            _mediaControlService.MediaEnded += OnMediaEnded;
            _mediaControlService.MediaOpened += OnMediaOpened;

            _queueService.QueueChanged += OnQueueChanged;
            _queueService.QueueIndexChanged += OnQueueIndexChanged;

            _isSubscribedToEvents = true;
        }

        private void UnsubscribeFromEvents()
        {
            if (!_isSubscribedToEvents)
            {
                Logger.LogDebug("Not subscribed to events, skipping unsubscribe");
                return;
            }

            _mediaControlService.NowPlayingChanged -= OnNowPlayingChanged;
            _mediaControlService.PlaybackStateChanged -= OnPlaybackStateChanged;
            _mediaControlService.MediaFailed -= OnMediaFailed;
            _mediaControlService.MediaEnded -= OnMediaEnded;
            _mediaControlService.MediaOpened -= OnMediaOpened;

            _queueService.QueueChanged -= OnQueueChanged;
            _queueService.QueueIndexChanged -= OnQueueIndexChanged;

            UnwireSystemMediaTransportControls();

            _isSubscribedToEvents = false;
        }

        private async Task EnsureMediaPlayerInitializedAsync()
        {
            if (_mediaControlService.MediaPlayer == null)
            {
                var context = CreateErrorContext("EnsureMediaPlayerInitialized", ErrorCategory.Media);
                try
                {
                    Logger.LogDebug("Creating MediaPlayer for audio playback");
                    var audioMediaPlayer = new MediaPlayer
                    {
                        AudioCategory = MediaPlayerAudioCategory.Media,
                        Volume = 1.0
                    };

                    _mediaControlService.Initialize(audioMediaPlayer);
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            }
        }

        private void OnNowPlayingChanged(object sender, BaseItemDto item)
        {
            var session = _playbackCancellationTokenSource;
            if (session?.IsCancellationRequested != false)
            {
                return;
            }

            NowPlayingChanged?.Invoke(this, item);
            ShowOnTransportControls(item);
        }

        private void OnPlaybackStateChanged(object sender, MediaPlaybackState state)
        {
            PlaybackStateChanged?.Invoke(this, state);
            UpdatePlaybackStatus(state);
        }

        private void OnQueueChanged(object sender, List<BaseItemDto> queue)
        {
            UpdateTransportControlsState();
        }

        private void OnQueueIndexChanged(object sender, int index)
        {
            UpdateTransportControlsState();
        }

        private static bool IsAudioItem(BaseItemDto item)
        {
            if (item == null)
            {
                return false;
            }

            return item.Type == BaseItemDto_Type.Audio ||
                   item.Type == BaseItemDto_Type.MusicAlbum ||
                   item.Type == BaseItemDto_Type.MusicArtist ||
                   item.Type == BaseItemDto_Type.Playlist ||
                   item.MediaType == BaseItemDto_MediaType.Audio;
        }

        private void OnMediaOpened(object sender, object args)
        {
            var session = _playbackCancellationTokenSource;
            if (session?.IsCancellationRequested != false)
            {
                return;
            }

            var currentItem = _mediaControlService.CurrentItem;
            ShowOnTransportControls(currentItem);
            if (IsAudioItem(currentItem))
            {
                FireAndForget(() => StartPlaybackReportingAsync(), "StartPlaybackReportingAsync");
            }
        }

        private void OnMediaEnded(object sender, object args)
        {
            var currentItem = _mediaControlService.CurrentItem;
            var repeatMode = _mediaControlService.RepeatMode;

            Logger.LogInformation("Audio playback ended - RepeatMode: {RepeatMode}, CurrentItem: {CurrentItemName}", repeatMode, currentItem?.Name);

            FireAndForget(() => StopPlaybackReportingAsync(), "StopPlaybackReportingAsync");

            var context = CreateErrorContext("OnMediaEnded", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                if (repeatMode == RepeatMode.One)
                {
                    Logger.LogInformation("Repeating current track: {CurrentItemName}", currentItem?.Name);
                    FireAndForget(() => PlayItemAsync(currentItem), "PlayItemAsync-RepeatOne");
                }
                else
                {
                    SkipNext();
                }
            });
        }

        private async Task PlayCurrentQueueItemAsync(BaseItemDto item)
        {
            if (item?.Id == null)
            {
                Logger.LogError("Cannot play item - null or missing ID");
                return;
            }

            if (!IsAudioItem(item))
            {
                Logger.LogWarning("MusicPlayerService: Ignoring non-audio item: {ItemName} (Type: {ItemType})", item.Name, item.Type);
                return;
            }

            // Cancel any in-flight callbacks from the previous session before subscribing
            AsyncHelper.Supersede(ref _playbackCancellationTokenSource);

            SubscribeToEvents();

            await EnsureMediaPlayerInitializedAsync().ConfigureAwait(false);

            await _mediaOptimizationService.ApplyNormalizationAsync(
                _mediaControlService.MediaPlayer, item.NormalizationGain).ConfigureAwait(false);

            await StopPlaybackReportingAsync().ConfigureAwait(false);

            _isInFallbackMode = false;

            _currentMediaSource = null;
            _currentPlaySessionId = null;

            UpdateTransportControlsState();

            // The server's session id, as for video: the stream URLs built here and every report
            // name it, so the stop report ends the server's transcode
            var playbackInfo = await _mediaPlaybackService.GetPlaybackInfoAsync(item.Id.Value).ConfigureAwait(false);
            _currentPlaySessionId = playbackInfo?.PlaySessionId;

            Logger.LogInformation(
                "=== Playing {ItemName} ({ItemType}, {ItemId}, {ItemContainer}), session {PlaySessionId}, queue {QueuePosition}/{QueueCount} ===",
                item.Name, item.Type, item.Id, item.Container, _currentPlaySessionId, _queueService.CurrentQueueIndex + 1,
                _queueService.Queue.Count);

            if (playbackInfo?.MediaSources is { Count: > 0 })
            {
                var selectedSource = playbackInfo.MediaSources.FirstOrDefault(ms =>
                    ms.SupportsDirectStream == true || ms.SupportsTranscoding == true);

                Logger.LogDebug(
                    "Selected media source: DirectStream={SelectedSourceSupportsDirectStream}, Transcoding={SelectedSourceSupportsTranscoding}", selectedSource?.SupportsDirectStream, selectedSource?.SupportsTranscoding);

                if (selectedSource != null)
                {
                    Logger.LogDebug(
                        "MediaSource details - Path: {SelectedSourcePath}, TranscodingUrl: {SelectedSourceTranscodingUrl}", selectedSource.Path, UrlHelper.RedactApiKey(selectedSource.TranscodingUrl));
                    _currentMediaSource = selectedSource;
                    await LoadMediaAsync(item, selectedSource).ConfigureAwait(false);
                }
                else
                {
                    Logger.LogError("No suitable media source found");
                }
            }
            else
            {
                Logger.LogError("No media sources available for item");
            }
        }

        private async Task LoadMediaAsync(BaseItemDto item, MediaSourceInfo mediaSource)
        {
            var context = CreateErrorContext("LoadMediaAsync", ErrorCategory.Media);
            try
            {
                string mediaUrl = null;
                var serverUrl = _authService.ServerUrl;
                var accessToken = _authService.AccessToken;

                if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken))
                {
                    Logger.LogError("Server URL or access token is not available");
                    return;
                }

                // Known to fail on Xbox: go straight to the server stream instead of waiting
                // for MediaFailed, which costs the user a failed start first.
                if (item.Type == BaseItemDto_Type.Audio && HasOversizedEmbeddedArtwork(mediaSource))
                {
                    Logger.LogInformation(
                        "Embedded artwork over {MaxPixels}px - streaming {ItemName} via the server without it",
                        MusicConstants.MaxDirectPlayEmbeddedArtworkPixels, item.Name);
                    await PlayItemWithTranscodingFallbackAsync(item, mediaSource).ConfigureAwait(false);
                    return;
                }

                Logger.LogDebug(
                    "MediaSource {MediaSourceId}: {Container}, {Size} bytes, {Bitrate} bps, {Protocol}{Remote}; " +
                    "DirectPlay {DirectPlay}, DirectStream {DirectStream}, Transcoding {Transcoding}; {Path}",
                    mediaSource.Id, mediaSource.Container, mediaSource.Size, mediaSource.Bitrate, mediaSource.Protocol,
                    mediaSource.IsRemote == true ? " (remote)" : string.Empty, mediaSource.SupportsDirectPlay,
                    mediaSource.SupportsDirectStream, mediaSource.SupportsTranscoding, mediaSource.Path);

                if (mediaSource.SupportsDirectStream == true && !string.IsNullOrEmpty(mediaSource.Path))
                {
                    if (item.Type == BaseItemDto_Type.Audio && mediaSource.Protocol == MediaSourceInfo_Protocol.File)
                    {
                        mediaUrl = BuildDirectAudioStreamUrl(item, mediaSource);
                    }
                    else
                    {
                        mediaUrl = UrlHelper.ResolveServerUrl(serverUrl, mediaSource.Path);
                    }

                    Logger.LogDebug("Using direct stream path for {MediaSourceContainer} file", mediaSource.Container);
                }
                else if (!string.IsNullOrEmpty(mediaSource.TranscodingUrl))
                {
                    mediaUrl = UrlHelper.ResolveServerUrl(serverUrl, mediaSource.TranscodingUrl);

                    Logger.LogDebug("Using server-provided transcoding URL: {MediaUrl}", UrlHelper.RedactApiKey(mediaUrl));
                }
                else if (item.Type == BaseItemDto_Type.Audio && item.Id.HasValue &&
                         mediaSource.SupportsTranscoding == true)
                {
                    var requestInfo = _apiClient.Audio[item.Id.Value].Universal.ToGetRequestInformation(config =>
                    {
                        if (!string.IsNullOrEmpty(mediaSource.Id))
                        {
                            config.QueryParameters.MediaSourceId = mediaSource.Id;
                        }

                        // No container or codec asked for: the server picks from the device profile
                        config.QueryParameters.DeviceId = _deviceService.GetDeviceId();
                        config.QueryParameters.UserId = _userProfileService.GetCurrentUserGuid();
                    });

                    mediaUrl = _apiClient.BuildUri(requestInfo).ToString();

                    Logger.LogDebug("Using the universal HLS endpoint: {MediaUrl}", UrlHelper.RedactApiKey(mediaUrl));
                }
                else if (!string.IsNullOrEmpty(mediaSource.Path))
                {
                    mediaUrl = UrlHelper.ResolveServerUrl(serverUrl, mediaSource.Path);

                    Logger.LogDebug("Using direct path from media source as fallback");
                }

                if (!string.IsNullOrEmpty(mediaUrl))
                {
                    // The player fetches the URL itself, without the SDK's auth header
                    mediaUrl = UrlHelper.AppendApiKey(mediaUrl, accessToken);

                    Logger.LogInformation("Playing {Container} from {MediaUrl}", mediaSource.Container,
                        UrlHelper.RedactApiKey(mediaUrl));

                    if (item.Type == BaseItemDto_Type.Audio)
                    {
                        var audioStream =
                            mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Audio);
                        if (audioStream != null)
                        {
                            Logger.LogDebug(
                                "Audio properties for {MediaSourceContainer}: {AudioStreamSampleRate}Hz/{AudioStreamBitDepth}bit, BitRate: {AudioStreamBitRate}", mediaSource.Container, audioStream.SampleRate, audioStream.BitDepth, audioStream.BitRate);
                        }
                    }

                    var source = await _mediaOptimizationService.CreateStreamSourceAsync(mediaUrl).ConfigureAwait(false);

                    var playbackItem = new MediaPlaybackItem(source);
                    _lastPlaybackStartTime = DateTime.UtcNow;
                    await _mediaControlService.SetMediaSourceAsync(playbackItem, item).ConfigureAwait(false);
                    _mediaControlService.Play();
                }
                else
                {
                    Logger.LogError("No valid media URL found in media source");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }
    }
}
