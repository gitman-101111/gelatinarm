using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Web.Http.Headers;

namespace Gelatinarm.Services
{
    public class MusicPlayerService : BaseService, IMusicPlayerService
    {
        private readonly JellyfinApiClient _apiClient;
        private readonly IAuthenticationService _authService;
        private readonly IUnifiedDeviceService _deviceService;
        private readonly IMediaControlService _mediaControlService;
        private readonly IMediaOptimizationService _mediaOptimizationService;
        private readonly IMediaPlaybackService _mediaPlaybackService;
        private readonly IPreferencesService _preferencesService;

        private readonly IPlaybackQueueService _queueService;
        private readonly IServiceProvider _serviceProvider;
        private readonly IUserProfileService _userProfileService;
        private MediaSourceInfo _currentMediaSource;
        private string _currentPlaySessionId;
        private bool _isInFallbackMode = false;
        private SystemMediaTransportControls _systemMediaTransportControls;
        private DateTime _smtcSuppressStoppedUntilUtc = DateTime.MinValue;

        private DateTime _lastPlaybackStartTime = DateTime.MinValue;
        private CancellationTokenSource _playbackCancellationTokenSource;
        private CancellationTokenSource _progressReportCancellationTokenSource;
        private CancellationTokenSource _transportControlsUpdateCts;
        private CancellationTokenSource _playbackStatusUpdateCts;
        private Timer _progressReportTimer;
        private bool _isSmtcInitialized = false;
        private bool _isSubscribedToEvents = false;

        public MusicPlayerService(
            ILogger<MusicPlayerService> logger,
            IServiceProvider serviceProvider,
            JellyfinApiClient apiClient,
            IAuthenticationService authService,
            IUserProfileService userProfileService,
            IMediaPlaybackService mediaPlaybackService,
            IUnifiedDeviceService deviceService,
            IPreferencesService preferencesService,
            IMediaOptimizationService mediaOptimizationService,
            IPlaybackQueueService queueService,
            IMediaControlService mediaControlService) : base(logger)
        {
            _serviceProvider = serviceProvider;
            _apiClient = apiClient;
            _authService = authService;
            _userProfileService = userProfileService;
            _mediaPlaybackService = mediaPlaybackService;
            _deviceService = deviceService;
            _preferencesService = preferencesService;
            _mediaOptimizationService = mediaOptimizationService;
            _queueService = queueService;
            _mediaControlService = mediaControlService;

            // Don't subscribe to events in constructor - wait until audio playback starts
            // Initialize();
        }

        public MediaPlayer MediaPlayer => _mediaControlService.MediaPlayer;
        public bool IsPlaying => _mediaControlService.IsPlaying;
        public BaseItemDto CurrentItem => _mediaControlService.CurrentItem;
        public List<BaseItemDto> Queue => _queueService.Queue;
        public int CurrentQueueIndex => _queueService.CurrentQueueIndex;
        public bool IsRepeatOne => _mediaControlService.RepeatMode == RepeatMode.One;
        public bool IsRepeatAll => _mediaControlService.RepeatMode == RepeatMode.All;
        public bool IsShuffleMode => _queueService.IsShuffleMode;
        public bool IsShuffleEnabled => _queueService.IsShuffleMode;
        public RepeatMode RepeatMode => _mediaControlService.RepeatMode;

        public event EventHandler<BaseItemDto> NowPlayingChanged;
        public event EventHandler<MediaPlaybackState> PlaybackStateChanged;
        public event EventHandler<List<BaseItemDto>> QueueChanged;
        public event EventHandler<bool> ShuffleStateChanged;
        public event EventHandler<RepeatMode> RepeatModeChanged;

        public async Task PlayItem(BaseItemDto item, MediaSourceInfo mediaSource = null)
        {
            var context = CreateErrorContext("PlayItem", ErrorCategory.Media);
            try
            {
                await PlayCurrentQueueItem(item, mediaSource).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        public async Task PlayItems(List<BaseItemDto> items, int startIndex = 0)
        {
            var context = CreateErrorContext("PlayItems", ErrorCategory.Media);
            try
            {
                if (items == null || !items.Any())
                {
                    return;
                }

                Logger.LogInformation($"=== PlayItems called with {items.Count} items, startIndex={startIndex} ===");
                for (var i = 0; i < items.Count; i++)
                {
                    Logger.LogInformation($"  Queue[{i}]: {items[i].Name} (ID: {items[i].Id})");
                }

                _queueService.SetQueue(items, startIndex);

                // Reset repeat-one when a new queue is built
                if (_mediaControlService.RepeatMode == RepeatMode.One)
                {
                    Logger.LogInformation("Resetting repeat-one mode for new queue");
                    _mediaControlService.SetRepeatMode(RepeatMode.None);
                    SetRepeatMode(RepeatMode.None);
                    UpdateTransportControlsState();
                    RepeatModeChanged?.Invoke(this, RepeatMode.None);
                }

                if (_queueService.CurrentQueueIndex >= 0 && _queueService.CurrentQueueIndex < _queueService.Queue.Count)
                {
                    // Play the item at the current queue index
                    await PlayItem(_queueService.Queue[_queueService.CurrentQueueIndex]).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        public void AddToQueue(BaseItemDto item)
        {
            _queueService.AddToQueue(item);
        }

        public void AddToQueueNext(BaseItemDto item)
        {
            _queueService.AddToQueueNext(item);
        }

        public void ClearQueue()
        {
            _queueService.ClearQueue();
        }

        public void Stop()
        {
            var context = CreateErrorContext("Stop", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    // Cancel any ongoing playback operations
                    _playbackCancellationTokenSource?.Cancel();
                    _playbackCancellationTokenSource?.Dispose();
                    _playbackCancellationTokenSource = null;

                    // Stop playback through media control service
                    var currentItem = _mediaControlService.CurrentItem;
                    var positionTicks = _mediaControlService.Position.Ticks;

                    _mediaControlService.Stop();

                    // Stop progress reporting synchronously
                    StopProgressReporting();

                    // Clear System Media Transport Controls display
                    ClearDisplay();

                    // Dispose SMTC when stopping playback
                    if (_isSmtcInitialized)
                    {
                        Logger.LogInformation("Disposing System Media Transport Controls");
                        DisposeSystemMediaTransportControls();
                        _isSmtcInitialized = false;
                    }

                    // Store values for async reporting
                    var itemId = currentItem?.Id;
                    var mediaSourceId = _currentMediaSource?.Id;
                    var playSessionId = _currentPlaySessionId;

                    // Clear current state
                    _currentMediaSource = null;
                    _currentPlaySessionId = null;

                    // Unsubscribe from events when stopping audio playback
                    UnsubscribeFromEvents();

                    // Notify UI that nothing is playing — OnNowPlayingChanged can't propagate this
                    // because _playbackCancellationTokenSource is already cancelled above
                    NowPlayingChanged?.Invoke(this, null);

                    // Report playback stopped asynchronously (fire and forget)
                    if (itemId.HasValue && mediaSourceId != null && playSessionId != null)
                    {
                        ReportPlaybackStoppedFireAndForget(
                            itemId.Value.ToString(),
                            mediaSourceId,
                            positionTicks,
                            playSessionId);
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void Play()
        {
            var context = CreateErrorContext("Play", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    _mediaControlService.Play();
                    UpdateTransportControlsState();
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void Pause()
        {
            var context = CreateErrorContext("Pause", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    Logger.LogInformation(
                        $"Pause called - Current queue size: {_queueService.Queue.Count}, Current index: {_queueService.CurrentQueueIndex}");
                    _mediaControlService.Pause();
                    Logger.LogInformation($"After pause - Queue size: {_queueService.Queue.Count}");

                    // Ensure transport controls state is updated after pause
                    UpdateTransportControlsState();
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SkipNext()
        {
            var context = CreateErrorContext("SkipNext", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    if (!_queueService.Queue.Any())
                    {
                        return;
                    }

                    var nextIndex = _queueService.GetNextIndex(_mediaControlService.RepeatMode == RepeatMode.All);
                    if (nextIndex >= 0)
                    {
                        _queueService.SetCurrentIndex(nextIndex);
                        FireAndForget(() => PlayItem(_queueService.Queue[nextIndex]), "PlayItem");
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SkipPrevious()
        {
            var context = CreateErrorContext("SkipPrevious", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    if (!_queueService.Queue.Any())
                    {
                        return;
                    }

                    var prevIndex = _queueService.GetPreviousIndex(_mediaControlService.RepeatMode == RepeatMode.All);
                    if (prevIndex >= 0)
                    {
                        _queueService.SetCurrentIndex(prevIndex);
                        FireAndForget(() => PlayItem(_queueService.Queue[prevIndex]), "PlayItem");
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SeekForward(int seconds)
        {
            _mediaControlService.SeekForward(seconds);
        }

        public void SeekBackward(int seconds)
        {
            _mediaControlService.SeekBackward(seconds);
        }

        public void CycleRepeatMode()
        {
            var context = CreateErrorContext("CycleRepeatMode", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    var newMode = _mediaControlService.CycleRepeatMode();
                    SetRepeatMode(newMode);
                    UpdateTransportControlsState();

                    // Notify UI to update
                    RepeatModeChanged?.Invoke(this, newMode);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void ToggleShuffleMode()
        {
            var context = CreateErrorContext("ToggleShuffleMode", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    _queueService.SetShuffle(!_queueService.IsShuffleMode);
                    SetShuffleEnabled(_queueService.IsShuffleMode);
                    UpdateTransportControlsState();

                    // Notify UI to update
                    ShuffleStateChanged?.Invoke(this, _queueService.IsShuffleMode);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SetShuffle(bool enabled)
        {
            var context = CreateErrorContext("SetShuffle", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    _queueService.SetShuffle(enabled);
                    SetShuffleEnabled(enabled);
                    UpdateTransportControlsState();

                    // Notify UI to update
                    ShuffleStateChanged?.Invoke(this, enabled);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }


        // Add methods from BackgroundPlaybackService that are missing
        public async Task<bool> EnableBackgroundPlayback()
        {
            var context = CreateErrorContext("EnableBackgroundPlayback", ErrorCategory.Media);
            try
            {
                // Ensure MediaPlayer is initialized
                await EnsureMediaPlayerInitializedAsync().ConfigureAwait(false);

                // Background playback enabled
                if (_mediaControlService.MediaPlayer != null)
                {
                    _mediaControlService.MediaPlayer.AudioCategory = MediaPlayerAudioCategory.Media;
                    Logger.LogInformation("Background playback enabled");
                }
                return true;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, false, false).ConfigureAwait(false);
            }
        }

        public async Task<bool> DisableBackgroundPlayback()
        {
            var context = CreateErrorContext("DisableBackgroundPlayback", ErrorCategory.Media);
            try
            {
                // Background playback disabled
                if (_mediaControlService.MediaPlayer != null)
                {
                    _mediaControlService.MediaPlayer.AudioCategory = MediaPlayerAudioCategory.Media;
                    Logger.LogInformation("Background playback disabled");
                }
                return true;
            }
            catch (Exception ex)
            {
                return await ErrorHandler.HandleErrorAsync(ex, context, false, false).ConfigureAwait(false);
            }
        }


        public void SetQueue(List<BaseItemDto> items, int startIndex = 0)
        {
            _queueService.SetQueue(items, startIndex);

            // Reset repeat-one when a new queue is built
            if (_mediaControlService.RepeatMode == RepeatMode.One)
            {
                Logger.LogInformation("Resetting repeat-one mode for new queue");
                _mediaControlService.SetRepeatMode(RepeatMode.None);
                SetRepeatMode(RepeatMode.None);
                UpdateTransportControlsState();
                RepeatModeChanged?.Invoke(this, RepeatMode.None);
            }
        }

        public void ToggleRepeatMode()
        {
            var context = CreateErrorContext("ToggleRepeatMode", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    var currentMode = _mediaControlService.RepeatMode;
                    var newMode = currentMode switch
                    {
                        RepeatMode.None => RepeatMode.One,
                        RepeatMode.One => RepeatMode.All,
                        RepeatMode.All => RepeatMode.None,
                        _ => RepeatMode.None
                    };

                    _mediaControlService.SetRepeatMode(newMode);
                    SetRepeatMode(newMode);
                    UpdateTransportControlsState();
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void Dispose()
        {
            // Stop progress reporting timer
            StopProgressReporting();

            // Ensure cancellation token is disposed
            _progressReportCancellationTokenSource?.Dispose();
            _playbackCancellationTokenSource?.Dispose();
            _transportControlsUpdateCts?.Cancel();
            _transportControlsUpdateCts?.Dispose();
            _playbackStatusUpdateCts?.Cancel();
            _playbackStatusUpdateCts?.Dispose();

            // Unwire event handlers
            UnsubscribeFromEvents();

            // Dispose sub-services
            DisposeSystemMediaTransportControls();
            (_mediaControlService as IDisposable)?.Dispose();
        }

        private void SubscribeToEvents()
        {
            if (_isSubscribedToEvents)
            {
                Logger.LogDebug("Already subscribed to events, skipping");
                return;
            }

            Logger.LogInformation("MusicPlayerService: Subscribing to events for audio playback");

            // Wire up event handlers (but don't initialize SMTC yet)
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

            Logger.LogInformation("MusicPlayerService: Unsubscribing from events");

            // Unwire event handlers
            if (_mediaControlService != null)
            {
                _mediaControlService.NowPlayingChanged -= OnNowPlayingChanged;
                _mediaControlService.PlaybackStateChanged -= OnPlaybackStateChanged;
                _mediaControlService.MediaFailed -= OnMediaFailed;
                _mediaControlService.MediaEnded -= OnMediaEnded;
                _mediaControlService.MediaOpened -= OnMediaOpened;
            }

            if (_queueService != null)
            {
                _queueService.QueueChanged -= OnQueueChanged;
                _queueService.QueueIndexChanged -= OnQueueIndexChanged;
            }

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
                    Logger.LogInformation("Creating MediaPlayer for audio playback");
                    var audioMediaPlayer = new MediaPlayer
                    {
                        AudioCategory = MediaPlayerAudioCategory.Media,
                        Volume = 1.0
                    };

                    // Initialize MediaControlService with the audio MediaPlayer
                    await _mediaControlService.InitializeAsync(audioMediaPlayer).ConfigureAwait(false);

                    Logger.LogInformation("MediaPlayer created and MediaControlService initialized for audio playback");
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
            if (session == null || session.IsCancellationRequested) return;

            NowPlayingChanged?.Invoke(this, item);

            if (item != null && IsAudioItem(item))
            {
                if (!_isSmtcInitialized && _mediaControlService.MediaPlayer != null)
                {
                    Logger.LogInformation($"Initializing System Media Transport Controls for music playback: {item.Name}");
                    InitializeSystemMediaTransportControls(_mediaControlService.MediaPlayer);
                    _isSmtcInitialized = true;
                }

                FireAndForget(() => UpdateDisplay(item), "UpdateSystemMediaDisplay");
                UpdateTransportControlsState();
            }
        }

        private void OnPlaybackStateChanged(object sender, MediaPlaybackState state)
        {
            PlaybackStateChanged?.Invoke(this, state);
            UpdatePlaybackStatus(state);
        }

        private void OnQueueChanged(object sender, List<BaseItemDto> queue)
        {
            UpdateTransportControlsState();
            QueueChanged?.Invoke(this, queue);
        }

        private void OnQueueIndexChanged(object sender, int index)
        {
            UpdateTransportControlsState();
        }

        private bool IsAudioItem(BaseItemDto item)
        {
            if (item == null) return false;

            // Check if it's an audio item type
            return item.Type == BaseItemDto_Type.Audio ||
                   item.Type == BaseItemDto_Type.MusicAlbum ||
                   item.Type == BaseItemDto_Type.MusicArtist ||
                   item.Type == BaseItemDto_Type.Playlist ||
                   item.MediaType == BaseItemDto_MediaType.Audio;
        }

        private void OnMediaOpened(object sender, object args)
        {
            var session = _playbackCancellationTokenSource;
            if (session == null || session.IsCancellationRequested) return;

            Logger.LogInformation("Media opened successfully");

            // Initialize SMTC only when MUSIC starts playing (not for video)
            var currentItem = _mediaControlService.CurrentItem;
            if (!_isSmtcInitialized && currentItem != null && IsAudioItem(currentItem))
            {
                Logger.LogInformation($"Initializing System Media Transport Controls for music playback: {currentItem.Name}");
                InitializeSystemMediaTransportControls(_mediaControlService.MediaPlayer);
                _isSmtcInitialized = true;
            }

            // Re-apply display on every MediaOpened: the MediaPlayer resets SMTC display when
            // a new source finishes opening, wiping the info set in OnNowPlayingChanged.
            if (currentItem != null && IsAudioItem(currentItem))
            {
                FireAndForget(() => UpdateDisplay(currentItem), "UpdateSystemMediaDisplay");
                UpdateTransportControlsState();

                // Start playback reporting
                FireAndForgetSafe(() => StartPlaybackReporting(), "StartPlaybackReporting");
            }
        }

        private void OnSystemMediaButtonPressed(object sender, SystemMediaTransportControlsButton button)
        {
            Logger.LogInformation($"System media button pressed: {button}");
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    Play();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    Pause();
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    Stop();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    SkipNext();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    SkipPrevious();
                    break;
            }
        }

        private void OnShuffleChangeRequested(object sender, bool enabled)
        {
            // SMTC requested a shuffle change - update internal state without calling back to SMTC
            _queueService.SetShuffle(enabled);
            UpdateTransportControlsState();
            Logger.LogInformation($"Shuffle updated from SMTC to: {enabled}");

            // Notify UI to update
            ShuffleStateChanged?.Invoke(this, enabled);
        }

        private void OnRepeatModeChangeRequested(object sender, MediaPlaybackAutoRepeatMode mode)
        {
            var newMode = mode switch
            {
                MediaPlaybackAutoRepeatMode.None => RepeatMode.None,
                MediaPlaybackAutoRepeatMode.Track => RepeatMode.One,
                MediaPlaybackAutoRepeatMode.List => RepeatMode.All,
                _ => RepeatMode.None
            };

            _mediaControlService.SetRepeatMode(newMode);
            Logger.LogInformation($"Repeat mode updated from SMTC to: {newMode}");

            // Notify UI to update
            RepeatModeChanged?.Invoke(this, newMode);
        }

        private void InitializeSystemMediaTransportControls(MediaPlayer mediaPlayer)
        {
            if (mediaPlayer == null)
            {
                Logger.LogWarning("Cannot initialize SMTC - MediaPlayer is null");
                return;
            }

            _systemMediaTransportControls = mediaPlayer.SystemMediaTransportControls;
            if (_systemMediaTransportControls == null)
            {
                Logger.LogWarning("MediaPlayer.SystemMediaTransportControls returned null");
                return;
            }

            _systemMediaTransportControls.IsEnabled = true;
            _systemMediaTransportControls.IsPauseEnabled = true;
            _systemMediaTransportControls.IsPlayEnabled = true;
            _systemMediaTransportControls.IsNextEnabled = true;
            _systemMediaTransportControls.IsPreviousEnabled = true;
            _systemMediaTransportControls.IsStopEnabled = true;

            _systemMediaTransportControls.ButtonPressed += OnSystemMediaTransportControlsButtonPressed;
            _systemMediaTransportControls.ShuffleEnabledChangeRequested += OnShuffleEnabledChangeRequested;
            _systemMediaTransportControls.AutoRepeatModeChangeRequested += OnAutoRepeatModeChangeRequested;

            Logger.LogInformation("System Media Transport Controls initialized");
            UpdateTransportControlsState();
        }

        private void UnwireSystemMediaTransportControls()
        {
            if (_systemMediaTransportControls == null)
            {
                return;
            }

            _systemMediaTransportControls.ButtonPressed -= OnSystemMediaTransportControlsButtonPressed;
            _systemMediaTransportControls.ShuffleEnabledChangeRequested -= OnShuffleEnabledChangeRequested;
            _systemMediaTransportControls.AutoRepeatModeChangeRequested -= OnAutoRepeatModeChangeRequested;
        }

        private void UpdatePlaybackStatus(MediaPlaybackState state)
        {
            var smtc = _systemMediaTransportControls;
            if (smtc == null)
                return;

            if ((state == MediaPlaybackState.None || state == MediaPlaybackState.Paused) &&
                DateTime.UtcNow < _smtcSuppressStoppedUntilUtc)
            {
                Logger.LogDebug("Suppressing SMTC stopped status during transition");
                return;
            }

            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _playbackStatusUpdateCts, newCts);
            oldCts?.Cancel();
            oldCts?.Dispose();
            var token = newCts.Token;

            var context = CreateErrorContext("UpdatePlaybackStatus", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);

                    smtc.PlaybackStatus = state switch
                    {
                        MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
                        MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
                        MediaPlaybackState.None => MediaPlaybackStatus.Stopped,
                        MediaPlaybackState.Opening => MediaPlaybackStatus.Changing,
                        MediaPlaybackState.Buffering => MediaPlaybackStatus.Changing,
                        _ => MediaPlaybackStatus.Closed
                    };

                    Logger.LogDebug($"Updated SMTC playback status to: {smtc.PlaybackStatus}");
                }
                catch (OperationCanceledException)
                {
                    // Debounced — a newer state superseded this one
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private async Task UpdateDisplay(BaseItemDto item)
        {
            var smtc = _systemMediaTransportControls;
            if (smtc == null || item == null)
                return;

            var context = CreateErrorContext("UpdateDisplay", ErrorCategory.Media);
            try
            {
                await UIHelper.RunOnUIThreadAsync(async () =>
                {
                    var updater = smtc.DisplayUpdater;
                    if (updater == null)
                    {
                        Logger.LogWarning("SystemMediaTransportControls.DisplayUpdater returned null");
                        return;
                    }

                    updater.Type = MediaPlaybackType.Music;
                    updater.MusicProperties.Title = item.Name ?? "Unknown Title";
                    updater.MusicProperties.Artist = item.AlbumArtist ?? item.Artists?.FirstOrDefault() ?? "Unknown Artist";
                    updater.MusicProperties.AlbumTitle = item.Album ?? string.Empty;

                    await SetAlbumArtwork(updater, item);
                    updater.Update();

                    Logger.LogInformation($"Updated System Media Transport Controls: {item.Name}");
                }, logger: Logger).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private void UpdateButtonStates(bool canGoNext, bool canGoPrevious)
        {
            var smtc = _systemMediaTransportControls;
            if (smtc == null)
                return;

            var context = CreateErrorContext("UpdateButtonStates", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    await UIHelper.RunOnUIThreadAsync(() =>
                    {
                        if (!smtc.IsEnabled)
                            smtc.IsEnabled = true;

                        smtc.IsNextEnabled = canGoNext;
                        smtc.IsPreviousEnabled = canGoPrevious;
                    }, logger: Logger).ConfigureAwait(false);

                    Logger.LogInformation($"Updated transport controls buttons: Next={canGoNext}, Previous={canGoPrevious}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void SetShuffleEnabled(bool enabled)
        {
            var smtc = _systemMediaTransportControls;
            if (smtc == null)
                return;

            var context = CreateErrorContext("SetShuffleEnabled", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    await UIHelper.RunOnUIThreadAsync(() =>
                    {
                        smtc.ShuffleEnabled = enabled;
                    }, logger: Logger).ConfigureAwait(false);

                    Logger.LogInformation($"SMTC shuffle set to: {enabled}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void SetRepeatMode(RepeatMode mode)
        {
            var smtc = _systemMediaTransportControls;
            if (smtc == null)
                return;

            var context = CreateErrorContext("SetRepeatMode", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    await UIHelper.RunOnUIThreadAsync(() =>
                    {
                        smtc.AutoRepeatMode = mode switch
                        {
                            RepeatMode.None => MediaPlaybackAutoRepeatMode.None,
                            RepeatMode.One => MediaPlaybackAutoRepeatMode.Track,
                            RepeatMode.All => MediaPlaybackAutoRepeatMode.List,
                            _ => MediaPlaybackAutoRepeatMode.None
                        };
                    }, logger: Logger).ConfigureAwait(false);

                    Logger.LogInformation($"SMTC repeat mode set to: {smtc.AutoRepeatMode}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void ClearDisplay()
        {
            var smtc = _systemMediaTransportControls;
            if (smtc == null)
                return;

            var context = CreateErrorContext("ClearDisplay", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    smtc.DisplayUpdater.ClearAll();
                    smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                    smtc.DisplayUpdater.Update();
                    Logger.LogInformation("Cleared System Media Transport Controls display");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void DisposeSystemMediaTransportControls()
        {
            if (_systemMediaTransportControls == null)
            {
                return;
            }

            UnwireSystemMediaTransportControls();
            _systemMediaTransportControls.DisplayUpdater.ClearAll();
            _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _systemMediaTransportControls.DisplayUpdater.Update();
            _systemMediaTransportControls.IsEnabled = false;
            _systemMediaTransportControls = null;
        }

        private void OnSystemMediaTransportControlsButtonPressed(SystemMediaTransportControls sender,
            SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            Logger.LogInformation($"SMTC Button pressed: {args.Button}");
            OnSystemMediaButtonPressed(this, args.Button);
        }

        private void OnShuffleEnabledChangeRequested(SystemMediaTransportControls sender,
            ShuffleEnabledChangeRequestedEventArgs args)
        {
            Logger.LogInformation("SMTC Shuffle change requested");
            OnShuffleChangeRequested(this, !sender.ShuffleEnabled);
        }

        private void OnAutoRepeatModeChangeRequested(SystemMediaTransportControls sender,
            AutoRepeatModeChangeRequestedEventArgs args)
        {
            Logger.LogInformation($"SMTC Repeat mode change requested: {args.RequestedAutoRepeatMode}");
            sender.AutoRepeatMode = args.RequestedAutoRepeatMode;
            OnRepeatModeChangeRequested(this, args.RequestedAutoRepeatMode);
        }

        private async Task SetAlbumArtwork(SystemMediaTransportControlsDisplayUpdater updater, BaseItemDto item)
        {
            try
            {
                var hasPrimaryImage = item.ImageTags?.AdditionalData != null &&
                                      item.ImageTags.AdditionalData.ContainsKey("Primary");

                string imageUrl = null;
                if (item.AlbumId.HasValue && item.AlbumPrimaryImageTag != null)
                {
                    imageUrl = ImageHelper.GetImageUrl(item.AlbumId.Value.ToString(), "Primary");
                }
                else if (item.Id.HasValue && hasPrimaryImage)
                {
                    imageUrl = ImageHelper.GetImageUrl(item.Id.Value.ToString(), "Primary");
                }
                else if (item.AlbumId.HasValue)
                {
                    imageUrl = ImageHelper.GetImageUrl(item.AlbumId.Value.ToString(), "Primary");
                }

                if (!string.IsNullOrEmpty(imageUrl))
                {
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(imageUrl));
                    Logger.LogInformation("Set SMTC thumbnail successfully");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to set album artwork for SMTC");
            }
        }


        private void OnMediaFailed(object sender, MediaPlayerFailedEventArgs args)
        {
            // Handle the event synchronously and delegate async work
            FireAndForget(() => HandleMediaFailedAsync(args), "HandleMediaFailed");
        }

        private async Task HandleMediaFailedAsync(MediaPlayerFailedEventArgs args)
        {
            try
            {
                Logger.LogError("=== Media Playback Failed ===");
                Logger.LogError($"  Error: {args.Error}");
                Logger.LogError($"  ErrorMessage: {args.ErrorMessage}");
                Logger.LogError(
                    $"  PlaybackSession State: {_mediaControlService.MediaPlayer.PlaybackSession?.PlaybackState}");
                Logger.LogError(
                    $"  Position when failed: {_mediaControlService.MediaPlayer.PlaybackSession?.Position}");

                // Log additional details about the failure
                var currentItem = _mediaControlService.CurrentItem;
                if (currentItem != null)
                {
                    Logger.LogError("Failed item details:");
                    Logger.LogError($"  Name: {currentItem.Name}");
                    Logger.LogError($"  ID: {currentItem.Id}");
                    Logger.LogError($"  Type: {currentItem.Type}");
                    Logger.LogError($"  Container: {currentItem.Container}");
                    Logger.LogError($"  Path: {currentItem.Path}");

                    // Log media source details if available
                    if (_mediaControlService.MediaPlayer?.Source is MediaPlaybackItem playbackItem)
                    {
                        var source = playbackItem.Source;
                        Logger.LogError($"  Media source URI: {source?.Uri}");
                        Logger.LogError($"  Audio tracks in item: {playbackItem.AudioTracks?.Count ?? 0}");
                    }

                    // Log timing of failure
                    var timeSinceStart = DateTime.UtcNow - _lastPlaybackStartTime;
                    Logger.LogError($"  Time since playback start: {timeSinceStart.TotalMilliseconds}ms");

                    // For any media type, if source not supported, provide detailed guidance
                    if (args.Error == MediaPlayerError.SourceNotSupported)
                    {
                        Logger.LogError("Media source not supported by Xbox decoder");
                        Logger.LogError("Common causes:");
                        Logger.LogError("  - FLAC/MP3/M4A files with embedded artwork >1500x1500 pixels");
                        Logger.LogError("  - Video files with incompatible codecs or high-res poster frames");
                        Logger.LogError("Solutions:");
                        Logger.LogError("  - Re-encode media with smaller/no embedded artwork");
                        Logger.LogError("  - Strip metadata with tools like FFmpeg or Mp3tag");

                        // Automatically attempt fallback for audio files
                        if (currentItem?.Type == BaseItemDto_Type.Audio && !_isInFallbackMode)
                        {
                            Logger.LogInformation("Attempting automatic transcoding fallback for audio playback");
                            _smtcSuppressStoppedUntilUtc = DateTime.UtcNow.AddSeconds(3);
                            await PlayItemWithTranscodingFallback(currentItem).ConfigureAwait(false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in HandleMediaFailedAsync");
            }
        }

        private void OnMediaEnded(object sender, object args)
        {
            var currentItem = _mediaControlService.CurrentItem;
            var repeatMode = _mediaControlService.RepeatMode;

            Logger.LogInformation($"Audio playback ended - RepeatMode: {repeatMode}, CurrentItem: {currentItem?.Name}");

            // Stop playback reporting for the ended track
            FireAndForgetSafe(() => StopPlaybackReporting(), "StopPlaybackReporting");

            var context = CreateErrorContext("OnMediaEnded", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    if (repeatMode == RepeatMode.One)
                    {
                        // Repeat the current track
                        Logger.LogInformation($"Repeating current track: {currentItem?.Name}");
                        FireAndForgetSafe(() => PlayItem(currentItem), "PlayItem-RepeatOne");
                    }
                    else
                    {
                        Logger.LogInformation("Not in repeat one mode, calling SkipNext");
                        // Use SkipNext which handles shuffle and repeat all
                        SkipNext();
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }


        private async Task PlayCurrentQueueItem(BaseItemDto item, MediaSourceInfo mediaSource = null)
        {
            if (item?.Id == null)
            {
                Logger.LogError("Cannot play item - null or missing ID");
                return;
            }

            // Only handle audio items
            if (!IsAudioItem(item))
            {
                Logger.LogWarning($"MusicPlayerService: Ignoring non-audio item: {item.Name} (Type: {item.Type})");
                return;
            }

            // Cancel any in-flight callbacks from the previous session before subscribing
            _playbackCancellationTokenSource?.Cancel();
            _playbackCancellationTokenSource?.Dispose();
            _playbackCancellationTokenSource = new CancellationTokenSource();

            // Subscribe to events when starting audio playback
            SubscribeToEvents();

            // Ensure MediaControlService has a MediaPlayer for audio playback
            await EnsureMediaPlayerInitializedAsync().ConfigureAwait(false);

            // Apply LUFS-based volume normalization if enabled in settings
            await _mediaOptimizationService.ApplyNormalizationAsync(
                _mediaControlService.MediaPlayer, item.NormalizationGain).ConfigureAwait(false);


            // Stop any existing playback reporting
            await StopPlaybackReporting().ConfigureAwait(false);

            // Reset fallback mode flag
            _isInFallbackMode = false;

            _currentMediaSource = mediaSource;
            _currentPlaySessionId = Guid.NewGuid().ToString();

            Logger.LogInformation($"=== Playing item: {item.Name} ===");
            Logger.LogInformation($"  Type: {item.Type}");
            Logger.LogInformation($"  ID: {item.Id}");
            Logger.LogInformation($"  Container: {item.Container}");
            Logger.LogInformation($"  PlaySessionId: {_currentPlaySessionId}");
            Logger.LogInformation(
                $"  Queue position: {_queueService.CurrentQueueIndex + 1}/{_queueService.Queue.Count}");

            // Update the MediaPlaybackService with the current item
            if (_mediaPlaybackService is IMediaSessionService sessionService)
            {
                sessionService.UpdateCurrentItem(item);
            }

            // Update transport controls state
            UpdateTransportControlsState();

            if (mediaSource != null)
            {
                await LoadMedia(item, mediaSource).ConfigureAwait(false);
            }
            else
            {
                if (!item.Id.HasValue)
                {
                    Logger.LogError("Cannot get playback info - item has no ID");
                    return;
                }

                var playbackInfo = await _mediaPlaybackService.GetPlaybackInfoAsync(item.Id.Value.ToString())
                    .ConfigureAwait(false);
                if (playbackInfo?.MediaSources?.Any() == true)
                {
                    MediaSourceInfo selectedSource = null;

                    var firstSource = playbackInfo.MediaSources.FirstOrDefault();
                    if (firstSource == null)
                    {
                        Logger.LogWarning($"No media sources available for item {item.Name}");
                        return;
                    }

                    // Log codec details if available
                    if (firstSource.Container?.ToLower() == "flac")
                    {
                        var audioStream =
                            firstSource.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Audio);
                        if (audioStream != null)
                        {
                            Logger.LogInformation(
                                $"FLAC properties: SampleRate={audioStream.SampleRate}Hz, BitDepth={audioStream.BitDepth}bit, BitRate={audioStream.BitRate}, Channels={audioStream.Channels}");
                        }
                    }

                    // Select the best media source
                    selectedSource = playbackInfo.MediaSources?.FirstOrDefault(ms =>
                        ms.SupportsDirectStream == true || ms.SupportsTranscoding == true);

                    Logger.LogInformation(
                        $"Selected media source: DirectStream={selectedSource?.SupportsDirectStream}, Transcoding={selectedSource?.SupportsTranscoding}");

                    if (selectedSource != null)
                    {
                        Logger.LogInformation(
                            $"MediaSource details - Path: {selectedSource.Path}, TranscodingUrl: {selectedSource.TranscodingUrl}");
                        _currentMediaSource = selectedSource;
                        await LoadMedia(item, selectedSource).ConfigureAwait(false);
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
        }

        public async Task PlaySingleItem(BaseItemDto item, MediaSourceInfo mediaSource = null)
        {
            var context = CreateErrorContext("PlaySingleItem", ErrorCategory.Media);
            try
            {
                if (item?.Id == null)
                {
                    Logger.LogError("Cannot play item - null or missing ID");
                    return;
                }

                // Replace queue with only this item
                _queueService.ClearQueue();
                _queueService.AddToQueue(item);
                QueueChanged?.Invoke(this, _queueService.Queue);

                await PlayCurrentQueueItem(item, mediaSource).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        private async Task LoadMedia(BaseItemDto item, MediaSourceInfo mediaSource)
        {
            var context = CreateErrorContext("LoadMedia", ErrorCategory.Media);
            try
            {
                Logger.LogInformation($"=== LoadMedia Started for: {item.Name} ===");

                string mediaUrl = null;
                var serverUrl = _authService.ServerUrl;
                var accessToken = _authService.AccessToken;

                if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken))
                {
                    Logger.LogError("Server URL or access token is not available");
                    return;
                }

                // Log complete MediaSourceInfo
                Logger.LogInformation("MediaSource Properties:");
                Logger.LogInformation($"  Id: {mediaSource.Id}");
                Logger.LogInformation($"  Path: {mediaSource.Path}");
                Logger.LogInformation($"  Container: {mediaSource.Container}");
                Logger.LogInformation($"  Size: {mediaSource.Size}");
                Logger.LogInformation($"  Bitrate: {mediaSource.Bitrate}");
                Logger.LogInformation($"  SupportsDirectPlay: {mediaSource.SupportsDirectPlay}");
                Logger.LogInformation($"  SupportsDirectStream: {mediaSource.SupportsDirectStream}");
                Logger.LogInformation($"  SupportsTranscoding: {mediaSource.SupportsTranscoding}");
                Logger.LogInformation($"  IsRemote: {mediaSource.IsRemote}");
                Logger.LogInformation($"  Protocol: {mediaSource.Protocol}");

                // Determine the best URL to use based on media source capabilities
                if (mediaSource.SupportsDirectStream == true && !string.IsNullOrEmpty(mediaSource.Path))
                {
                    // Prefer direct streaming for audio files when supported
                    if (item.Type == BaseItemDto_Type.Audio && mediaSource.Protocol == MediaSourceInfo_Protocol.File)
                    {
                        mediaUrl = BuildDirectAudioStreamUrl(item, mediaSource, serverUrl, accessToken);
                        if (!string.IsNullOrEmpty(mediaUrl))
                        {
                            Logger.LogInformation("Using audio stream endpoint for direct playback");
                        }
                    }
                    else if (mediaSource.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        mediaUrl = mediaSource.Path;
                    }
                    else
                    {
                        mediaUrl = $"{serverUrl.TrimEnd('/')}{mediaSource.Path}";
                    }

                    Logger.LogInformation($"Using direct stream path for {mediaSource.Container} file");
                }
                else if (!string.IsNullOrEmpty(mediaSource.TranscodingUrl))
                {
                    // Use the server-provided transcoding URL
                    if (mediaSource.TranscodingUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        mediaUrl = mediaSource.TranscodingUrl;
                    }
                    else
                    {
                        mediaUrl = $"{serverUrl.TrimEnd('/')}{mediaSource.TranscodingUrl}";
                    }

                    Logger.LogInformation($"Using server-provided transcoding URL: {mediaUrl}");
                }
                else if (item.Type == BaseItemDto_Type.Audio && item.Id.HasValue &&
                         mediaSource.SupportsTranscoding == true)
                {
                    // Fall back to universal endpoint for audio that needs transcoding using SDK
                    var requestInfo = _apiClient.Audio[item.Id.Value].Universal.ToGetRequestInformation(config =>
                    {
                        // Add media source ID if available
                        if (!string.IsNullOrEmpty(mediaSource.Id))
                        {
                            config.QueryParameters.MediaSourceId = mediaSource.Id;
                        }

                        // Let the server decide the best format - support direct play when possible
                        // Xbox supports: MP3, AAC, FLAC, WMA, AC3
                        // Don't force transcoding - let Jellyfin decide based on client capabilities
                        config.QueryParameters.DeviceId = _deviceService.GetDeviceId();
                        config.QueryParameters.UserId = _userProfileService.GetCurrentUserGuid();
                    });

                    // SDK handles authentication via headers
                    mediaUrl = _apiClient.BuildUri(requestInfo).ToString();

                    // Add API key for authentication since MediaSource.CreateFromUri doesn't use SDK headers
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        mediaUrl = UrlHelper.AppendApiKey(mediaUrl, accessToken);
                    }

                    Logger.LogInformation("Using universal HLS endpoint for audio transcoding");
                    Logger.LogInformation($"HLS URL constructed: {mediaUrl}");
                }
                else if (!string.IsNullOrEmpty(mediaSource.Path))
                {
                    // Last resort - use the path directly
                    if (mediaSource.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    {
                        mediaUrl = mediaSource.Path;
                    }
                    else
                    {
                        mediaUrl = $"{serverUrl.TrimEnd('/')}{mediaSource.Path}";
                    }

                    Logger.LogInformation("Using direct path from media source as fallback");
                }
                // SDK handles authentication - do not manually add API keys

                if (!string.IsNullOrEmpty(mediaUrl))
                {
                    var originalMediaUrl = mediaUrl;
                    mediaUrl = UrlHelper.AppendApiKey(mediaUrl, accessToken);
                    if (!string.Equals(originalMediaUrl, mediaUrl, StringComparison.Ordinal))
                    {
                        Logger.LogInformation("Appended ApiKey to media URL for authentication");
                    }

                    Logger.LogInformation($"Playing media from URL: {mediaUrl}");
                    Logger.LogInformation($"Item type: {item.Type}, MediaType: {item.MediaType}");
                    Logger.LogInformation($"Container: {mediaSource.Container}");

                    MediaSource source = null;
                    var isAudio = item.Type == BaseItemDto_Type.Audio;

                    if (isAudio)
                    {
                        var audioStream =
                            mediaSource.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Audio);
                        if (audioStream != null)
                        {
                            Logger.LogInformation(
                                $"Audio properties for {mediaSource.Container}: {audioStream.SampleRate}Hz/{audioStream.BitDepth}bit, BitRate: {audioStream.BitRate}");
                        }
                    }

                    // Determine whether to use adaptive or simple media source
                    var useSimpleSource = false;

                    // For audio files, check if we're using direct streaming
                    if (isAudio)
                    {
                        // If we're using a direct path (not universal endpoint), use simple source
                        if (!mediaUrl.Contains("/universal") && mediaSource.SupportsDirectStream == true)
                        {
                            useSimpleSource = true;
                            Logger.LogInformation("Using simple source for direct audio streaming");
                        }
                        // If container indicates a direct file format and server might return direct file
                        else if (!string.IsNullOrEmpty(mediaSource.Container))
                        {
                            var directStreamContainers = new[] { "mp3", "m4a", "aac", "flac", "alac", "wav", "wma", "amr" };
                            if (directStreamContainers.Contains(mediaSource.Container.ToLower()) &&
                                mediaSource.SupportsDirectStream == true &&
                                !mediaUrl.Contains("transcodingProtocol=hls"))
                            {
                                // Even with universal endpoint, server might return direct file for these formats
                                useSimpleSource = true;
                                Logger.LogInformation(
                                    $"Using simple source for {mediaSource.Container} audio (server may return direct file)");
                            }
                        }
                    }

                    if (isAudio && useSimpleSource && !mediaUrl.Contains("/universal"))
                    {
                        await LogMediaUrlHeadersAsync(mediaUrl, mediaSource.Container).ConfigureAwait(false);
                    }

                    if (useSimpleSource)
                    {
                        Logger.LogInformation($"Using simple media source for direct {mediaSource.Container} playback");
                        source = await _mediaOptimizationService.CreateSimpleMediaSourceAsync(
                            mediaUrl,
                            accessToken,
                            isAudio,
                            _preferencesService).ConfigureAwait(false);
                    }
                    else
                    {
                        try
                        {
                            Logger.LogInformation(
                                $"Attempting adaptive media source for {(isAudio ? "audio" : "video")} streaming");
                            source = await _mediaOptimizationService.CreateAdaptiveMediaSourceAsync(
                                mediaUrl,
                                accessToken,
                                isAudio,
                                _preferencesService).ConfigureAwait(false);
                        }
                        catch (Exception adaptiveEx) when
                            (adaptiveEx.Message.Contains("UnsupportedManifestContentType"))
                        {
                            // Fallback to simple source if adaptive fails with manifest error
                            Logger.LogWarning(
                                $"Adaptive source failed with manifest error, falling back to simple source: {adaptiveEx.Message}");
                            source = await _mediaOptimizationService.CreateSimpleMediaSourceAsync(
                                mediaUrl,
                                accessToken,
                                isAudio,
                                _preferencesService).ConfigureAwait(false);
                        }
                    }

                    Logger.LogInformation("Creating MediaPlaybackItem from source");
                    var playbackItem = new MediaPlaybackItem(source);

                    if (isAudio && playbackItem.AudioTracks?.Any() == true)
                    {
                        Logger.LogInformation(
                            $"Audio playback item created with {playbackItem.AudioTracks.Count} track(s)");
                        for (var i = 0; i < playbackItem.AudioTracks.Count; i++)
                        {
                            var track = playbackItem.AudioTracks[i];
                            Logger.LogInformation($"  Audio Track {i}: Language={track.Language}, Label={track.Label}");
                        }
                    }

                    Logger.LogInformation("Setting new MediaPlaybackItem as source");
                    _lastPlaybackStartTime = DateTime.UtcNow;
                    await _mediaControlService.SetMediaSource(playbackItem, item).ConfigureAwait(false);
                    _mediaControlService.Play();

                    Logger.LogInformation("Successfully set media source and started playback");
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

        private async Task LogMediaUrlHeadersAsync(string mediaUrl, string container)
        {
            if (string.IsNullOrEmpty(mediaUrl))
            {
                return;
            }

            try
            {
                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.Accept.Clear();
                httpClient.DefaultRequestHeaders.Accept.Add(new HttpMediaTypeWithQualityHeaderValue("*/*"));

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var response = await httpClient
                    .GetAsync(new Uri(mediaUrl), HttpCompletionOption.ResponseHeadersRead)
                    .AsTask(cts.Token)
                    .ConfigureAwait(false);

                var contentType = response.Content?.Headers?.ContentType?.MediaType;
                var contentLength = response.Content?.Headers?.ContentLength;

                Logger.LogInformation(
                    $"[MEDIA-URL-HEADERS] Status={(int)response.StatusCode} {response.StatusCode}, " +
                    $"ContentType={contentType ?? "unknown"}, ContentLength={contentLength?.ToString() ?? "unknown"}");

                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    Logger.LogWarning("[MEDIA-URL-HEADERS] Authentication failed for direct media URL");
                }

                if (!string.IsNullOrEmpty(container) &&
                    !string.IsNullOrEmpty(contentType) &&
                    !contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.LogWarning(
                        $"[MEDIA-URL-HEADERS] Unexpected content type for {container} media: {contentType}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[MEDIA-URL-HEADERS] Failed to probe media URL headers");
            }
        }

        private string BuildDirectAudioStreamUrl(
            BaseItemDto item,
            MediaSourceInfo mediaSource,
            string serverUrl,
            string accessToken)
        {
            if (!item?.Id.HasValue ?? true)
            {
                return null;
            }

            if (string.IsNullOrEmpty(serverUrl))
            {
                return null;
            }

            var baseUrl = serverUrl.TrimEnd('/');
            var queryParts = new List<string> { "static=true" };

            if (!string.IsNullOrEmpty(mediaSource?.Id))
            {
                queryParts.Add($"mediaSourceId={Uri.EscapeDataString(mediaSource.Id)}");
            }

            var deviceId = _deviceService.GetDeviceId();
            if (!string.IsNullOrEmpty(deviceId))
            {
                queryParts.Add($"deviceId={Uri.EscapeDataString(deviceId)}");
            }

            var url = $"{baseUrl}/Audio/{item.Id.Value}/stream";
            if (queryParts.Count > 0)
            {
                url += "?" + string.Join("&", queryParts);
            }

            return UrlHelper.AppendApiKey(url, accessToken);
        }

        private void UpdateTransportControlsState()
        {
            var newCts = new CancellationTokenSource();
            var oldCts = Interlocked.Exchange(ref _transportControlsUpdateCts, newCts);
            oldCts?.Cancel();
            oldCts?.Dispose();
            var token = newCts.Token;

            FireAndForget(async () =>
            {
                try
                {
                    await Task.Delay(75, token).ConfigureAwait(false);

                    var queue = _queueService.Queue;
                    var currentIndex = _queueService.CurrentQueueIndex;
                    var isRepeatAll = _mediaControlService.RepeatMode == RepeatMode.All;
                    var isShuffleMode = _queueService.IsShuffleMode;

                    var shouldEnableNext = (currentIndex >= 0 && currentIndex < queue.Count - 1) || isRepeatAll ||
                                           (isShuffleMode && queue.Count > 1);
                    var shouldEnablePrevious = currentIndex > 0 || isRepeatAll || (isShuffleMode && queue.Count > 1);

                    UpdateButtonStates(shouldEnableNext, shouldEnablePrevious);
                    SetShuffleEnabled(isShuffleMode);
                    SetRepeatMode(_mediaControlService.RepeatMode);

                    Logger.LogInformation(
                        $"Updated transport controls: Next={shouldEnableNext}, Previous={shouldEnablePrevious}, Shuffle={isShuffleMode}, Repeat={_mediaControlService.RepeatMode}, QueueIndex={currentIndex}/{queue.Count}");
                }
                catch (OperationCanceledException)
                {
                    // Debounced — a newer update superseded this one
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to update transport controls state");
                }
            });
        }

        private async Task StartPlaybackReporting()
        {
            var context = CreateErrorContext("StartPlaybackReporting", ErrorCategory.Media);
            try
            {
                var currentItem = _mediaControlService.CurrentItem;
                if (currentItem?.Id == null || _currentMediaSource == null)
                {
                    return;
                }

                // Report playback start
                var positionTicks = _mediaControlService.Position.Ticks;
                if (!currentItem.Id.HasValue)
                {
                    Logger.LogWarning("Cannot report playback start - item has no ID");
                    return;
                }

                await _mediaPlaybackService.ReportPlaybackStartAsync(
                    currentItem.Id.Value.ToString(),
                    _currentMediaSource.Id,
                    positionTicks,
                    _currentPlaySessionId).ConfigureAwait(false);

                Logger.LogInformation($"Reported playback start for {currentItem.Name}");

                // Start progress reporting timer (every 10 seconds)
                StopProgressReporting();
                _progressReportCancellationTokenSource = new CancellationTokenSource();
                var cancellationToken = _progressReportCancellationTokenSource.Token;

                _progressReportTimer = new Timer(async _ =>
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            await ReportProgress(cancellationToken).ConfigureAwait(false);
                        }
                    }, null, TimeSpan.FromSeconds(RetryConstants.PLAYBACK_PROGRESS_INTERVAL_SECONDS),
                    TimeSpan.FromSeconds(RetryConstants.PLAYBACK_PROGRESS_INTERVAL_SECONDS));
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        private async Task ReportProgress(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var context = CreateErrorContext("ReportProgress", ErrorCategory.Media);
            try
            {
                var currentItem = _mediaControlService.CurrentItem;
                if (currentItem?.Id == null || _currentMediaSource == null)
                {
                    return;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var positionTicks = _mediaControlService.Position.Ticks;
                var isPaused = !_mediaControlService.IsPlaying;

                try
                {
                    if (!currentItem.Id.HasValue)
                    {
                        Logger.LogWarning("Cannot report playback progress - item has no ID");
                        return;
                    }

                    await _mediaPlaybackService.ReportPlaybackProgressAsync(
                        currentItem.Id.Value.ToString(),
                        _currentMediaSource.Id,
                        positionTicks,
                        _currentPlaySessionId,
                        isPaused).ConfigureAwait(false);

                    Logger.LogDebug(
                        $"Reported playback progress: {_mediaControlService.Position:mm\\:ss} / {_mediaControlService.Duration:mm\\:ss}");
                }
                catch (TaskCanceledException)
                {
                    Logger.LogDebug("Progress reporting cancelled");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Failed to report playback progress");
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        private void StopProgressReporting()
        {
            // Cancel any ongoing progress reports
            _progressReportCancellationTokenSource?.Cancel();
            _progressReportCancellationTokenSource?.Dispose();
            _progressReportCancellationTokenSource = null;

            // Dispose the timer
            _progressReportTimer?.Dispose();
            _progressReportTimer = null;
        }

        private async Task StopPlaybackReporting()
        {
            var context = CreateErrorContext("StopPlaybackReporting", ErrorCategory.Media);
            try
            {
                StopProgressReporting();

                var currentItem = _mediaControlService.CurrentItem;
                if (currentItem?.Id == null || _currentMediaSource == null)
                {
                    return;
                }

                var positionTicks = _mediaControlService.Position.Ticks;
                if (!currentItem.Id.HasValue)
                {
                    Logger.LogWarning("Cannot report playback stopped - item has no ID");
                    return;
                }

                await _mediaPlaybackService.ReportPlaybackStoppedAsync(
                    currentItem.Id.Value.ToString(),
                    _currentMediaSource.Id,
                    positionTicks,
                    _currentPlaySessionId).ConfigureAwait(false);

                Logger.LogInformation($"Reported playback stopped for {currentItem.Name}");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }


        private async Task PlayItemWithTranscodingFallback(BaseItemDto item)
        {
            var context = CreateErrorContext("PlayItemWithTranscodingFallback", ErrorCategory.Media);
            try
            {
                Logger.LogInformation($"=== Attempting transcoding fallback for: {item.Name} ===");

                if (item?.Id == null)
                {
                    Logger.LogError("Cannot play item - null or missing ID");
                    return;
                }

                // Prevent infinite loop - check if we've already tried fallback
                if (_isInFallbackMode)
                {
                    Logger.LogError("Already in fallback mode - stopping to prevent loop");
                    return;
                }

                _isInFallbackMode = true;

                var serverUrl = _authService.ServerUrl;
                var accessToken = _authService.AccessToken;

                if (string.IsNullOrEmpty(serverUrl) || string.IsNullOrEmpty(accessToken))
                {
                    Logger.LogError("Server URL or access token is not available");
                    _isInFallbackMode = false;
                    return;
                }

                try
                {
                    // Get playback info to ensure we have the latest media sources
                    if (!item.Id.HasValue)
                    {
                        Logger.LogError("Cannot get playback info - item has no ID");
                        return;
                    }

                    var playbackInfo = await _mediaPlaybackService.GetPlaybackInfoAsync(item.Id.Value.ToString())
                        .ConfigureAwait(false);
                    if (playbackInfo?.MediaSources?.Any() == true)
                    {
                        var mediaSource = playbackInfo.MediaSources.FirstOrDefault();
                        if (mediaSource == null)
                        {
                            Logger.LogError($"No media sources available for item {item.Name} when expected");
                            return;
                        }

                        _currentMediaSource = mediaSource;

                        // Use the SDK's universal endpoint for transcoding
                        // This lets Jellyfin decide the best format and strips metadata
                        // Item ID was already checked above, safe to use
                        var requestInfo = _apiClient.Audio[item.Id.Value].Universal.ToGetRequestInformation(config =>
                        {
                            config.QueryParameters.UserId = _userProfileService.GetCurrentUserGuid();
                            config.QueryParameters.DeviceId = _deviceService.GetDeviceId();

                            // Add optional parameters
                            if (!string.IsNullOrEmpty(mediaSource.Id))
                            {
                                config.QueryParameters.MediaSourceId = mediaSource.Id;
                            }

                            // Force transcoding to MP3 to strip embedded artwork
                            // This ensures the server transcodes even if the source format is supported
                            config.QueryParameters.Container = new[] { "mp3" };
                            config.QueryParameters.AudioCodec = "mp3";
                            config.QueryParameters.MaxStreamingBitrate = 320000; // 320 kbps for high quality MP3
                        });

                        // SDK handles authentication via headers
                        var mediaUrl = _apiClient.BuildUri(requestInfo).ToString();

                        // Add API key for authentication since MediaSource.CreateFromUri doesn't use SDK headers
                        if (!string.IsNullOrEmpty(accessToken))
                        {
                            mediaUrl = UrlHelper.AppendApiKey(mediaUrl, accessToken);
                        }

                        Logger.LogInformation($"Universal endpoint URL: {mediaUrl}");
                        Logger.LogInformation($"Using /Audio/{item.Id}/universal endpoint for server-side transcoding to MP3");
                        Logger.LogInformation("Server will handle format conversion and metadata stripping");

                        // Create media source from the universal URL
                        var uri = new Uri(mediaUrl);
                        var source = MediaSource.CreateFromUri(uri);
                        var playbackItem = new MediaPlaybackItem(source);

                        Logger.LogInformation("Clearing current MediaPlayer source");
                        _smtcSuppressStoppedUntilUtc = DateTime.UtcNow.AddSeconds(3);
                        _mediaControlService.ClearMediaSource();
                        await Task.Delay(MediaConstants.MEDIA_SOURCE_CLEAR_DELAY_MS).ConfigureAwait(false);

                        Logger.LogInformation("Setting transcoded MediaPlaybackItem as source");
                        await _mediaControlService.SetMediaSource(playbackItem, item).ConfigureAwait(false);

                        Logger.LogInformation("Starting transcoded playback");
                        _lastPlaybackStartTime = DateTime.UtcNow;
                        _mediaControlService.Play();

                        Logger.LogInformation("Successfully initiated transcoding fallback");

                        // Update the MediaPlaybackService with the current item again after transcoding
                        if (_mediaPlaybackService is IMediaSessionService sessionService)
                        {
                            sessionService.UpdateCurrentItem(item);
                        }

                        // Start playback reporting for the transcoded stream
                        await StartPlaybackReporting().ConfigureAwait(false);
                    }
                    else
                    {
                        Logger.LogError("No media sources available");
                        _isInFallbackMode = false;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to play with transcoding fallback");
                    _isInFallbackMode = false;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false).ConfigureAwait(false);
            }
        }

        /// <summary>
        ///     Safely executes an async operation with fire-and-forget pattern and proper error logging
        /// </summary>
        private async void FireAndForgetSafe(Func<Task> asyncOperation, string operationName = null)
        {
            try
            {
                await asyncOperation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    $"Error in fire-and-forget operation{(operationName != null ? $" '{operationName}'" : "")}");
            }
        }

        private async void ReportPlaybackStoppedFireAndForget(string itemId, string mediaSourceId, long positionTicks,
            string playSessionId)
        {
            try
            {
                await _mediaPlaybackService.ReportPlaybackStoppedAsync(
                    itemId,
                    mediaSourceId,
                    positionTicks,
                    playSessionId).ConfigureAwait(false);
                Logger.LogInformation("Reported playback stopped");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to report playback stopped");
            }
        }
    }
}
