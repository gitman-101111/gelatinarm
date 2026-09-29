using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Details;
using Gelatinarm.Playback;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Device;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Preferences;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public partial class MediaPlayerViewModel : BaseViewModel
    {
        private const double SkipCheckThresholdSeconds = 0.5;
        private volatile bool _isDisposed;
        private readonly IControllerInputService _controllerInputService;
        private readonly IMediaNavigationService _mediaNavigationService;
        private readonly IMediaSessionService _mediaSessionService;

        private readonly JellyfinApiClient _apiClient;
        private readonly IUserDataService _userDataService;
        private readonly IPlaybackControlService _playbackControlService;
        private readonly IPreferencesService _preferencesService;

        [ObservableProperty] private bool _areControlsVisible = true;

        [ObservableProperty] private ObservableCollection<AudioTrack> _audioTracks = new();

        [ObservableProperty] private BaseItemDto _currentItem;

        // The heart follows the item playing: a next episode brings its own
        [ObservableProperty] private bool _isFavorite;

        partial void OnCurrentItemChanged(BaseItemDto value)
        {
            IsFavorite = value?.UserData?.IsFavorite == true;
        }

        [ObservableProperty] private PlaybackStats _currentStats;

        [ObservableProperty] private string _currentTimeText = "0:00";

        [ObservableProperty] private TimeSpan _duration;

        [ObservableProperty] private string _durationText = "0:00";

        [ObservableProperty] private string _endsAtTimeText;

        private bool _hasAutoPlayedNext;

        private int _progressReportCounter;

        private const int ProgressReportTicks =
            PlaybackConstants.ProgressReportIntervalSeconds * 1000 / PlayerConstants.PositionTimerIntervalMs;
        private TimeSpan _actualResumePosition = TimeSpan.Zero; // Track actual resume position for corruption detection
        private MediaPlaybackState _lastPlaybackState = MediaPlaybackState.None;
        private bool _hasPlaybackStateSnapshot;

        private bool _hasVideoStarted;
        private bool _directPlayRetryAttempted;

        private readonly SemaphoreSlim _initializationSemaphore = new SemaphoreSlim(1, 1);

        private bool _autoPlayNextEpisode;

        public bool IsBuffering
        {
            get
            {
                var state = GetPlaybackStateSnapshot();
                return state == MediaPlaybackState.Buffering;
            }
        }

        [ObservableProperty] private bool _isEndsAtTimeVisible;

        private bool _isIntroSkipAvailable;

        [ObservableProperty] private bool _isNextEpisodeAvailable;

        private bool _isOutroSkipAvailable;

        public bool IsPaused
        {
            get
            {
                var state = GetPlaybackStateSnapshot();
                return state == MediaPlaybackState.Paused;
            }
        }

        public bool IsPlaying
        {
            get
            {
                var state = GetPlaybackStateSnapshot();
                return state == MediaPlaybackState.Playing;
            }
        }

        [ObservableProperty] private bool _isStatsVisible;

        private TimeSpan _lastSkipCheckPosition = TimeSpan.Zero;

        [ObservableProperty] private object _navigationSourceParameter;

        private bool _nextEpisodeButtonOverlayVisible;
        private readonly PlaybackSessionState _sessionState = new PlaybackSessionState();

        private MediaPlaybackParams _playbackParams;

        private TimeSpan _position = TimeSpan.Zero;

        public TimeSpan Position
        {
            get => WithManifestOffset(_position);
            set => SetProperty(ref _position, value);
        }

        [ObservableProperty] private double _positionPercentage;

        private readonly DispatcherTimer _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(PlayerConstants.PositionTimerIntervalMs)
        };

        [ObservableProperty] private AudioTrack _selectedAudioTrack;

        [ObservableProperty] private SubtitleTrack _selectedSubtitle;

        private readonly DispatcherTimer _statsUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _bufferingTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        private DateTime? _bufferingStartTime;
        private const int BufferingTimeoutSeconds = 30;
        private readonly BufferingStateCoordinator _bufferingStateCoordinator;
        private readonly PlaybackStateCoordinator _playbackStateCoordinator;
        private readonly SeekCompletionCoordinator _seekCompletionCoordinator;
        private bool _resumeAttemptInProgress;
        private MediaSourceInfo _statsMediaSource;
        private ServerStreamDetails _serverTranscodingInfo;
        private AppPreferences _preferences;
        private TimeSpan? _introStartTime;
        private TimeSpan? _introEndTime;
        private TimeSpan? _outroStartTime;
        private TimeSpan? _outroEndTime;
        private bool _hasAutoSkippedIntro;
        private bool _hasAutoSkippedOutro;

        [ObservableProperty] private ObservableCollection<SubtitleTrack> _subtitleTracks = new();

        public MediaPlayerViewModel(ILogger<MediaPlayerViewModel> logger) : base(logger)
        {
            _playbackControlService = GetRequiredService<IPlaybackControlService>();
            _preferencesService = GetRequiredService<IPreferencesService>();
            _mediaSessionService = GetRequiredService<IMediaSessionService>();
            _mediaNavigationService = GetRequiredService<IMediaNavigationService>();
            _controllerInputService = GetRequiredService<IControllerInputService>();
            _apiClient = GetRequiredService<JellyfinApiClient>();
            _userDataService = GetRequiredService<IUserDataService>();

            _bufferingStateCoordinator = new BufferingStateCoordinator(logger, BufferingTimeoutSeconds);
            _playbackStateCoordinator = new PlaybackStateCoordinator(logger, _bufferingStateCoordinator);
            _seekCompletionCoordinator = new SeekCompletionCoordinator(logger);

            _positionTimer.Tick += OnPositionTimerTick;
            _statsUpdateTimer.Tick += OnStatsUpdateTimerTick;
            _bufferingTimeoutTimer.Tick += OnBufferingTimeoutTimerTick;

            // The overlay binds into it before the first stats update
            CurrentStats = new PlaybackStats();
        }

        public Type NavigationSourcePage => _playbackParams?.NavigationSourcePage;

        // The overlay, auto-play and Skip Outro move to a next episode; other items end
        private bool HasNextEpisode => IsNextEpisodeAvailable && CurrentItem?.Type == BaseItemDto_Type.Episode;

        public bool HasMultipleAudioTracks => AudioTracks?.Count > 1;
        public bool HasSubtitleTracks => SubtitleTracks?.Count > 1; // More than just "None" option

        public MediaPlayerElement MediaPlayerElement { get; set; }

        private MediaPlayer Player => MediaPlayerElement?.MediaPlayer;

        [RelayCommand]
        private async Task ToggleFavoriteAsync()
        {
            var item = CurrentItem;
            if (item?.Id == null)
            {
                return;
            }

            try
            {
                var updatedData = await _userDataService.ToggleFavoriteAsync(item.Id.Value, !IsFavorite);
                if (updatedData != null)
                {
                    item.UserData = updatedData;
                    IsFavorite = updatedData.IsFavorite == true;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("ToggleFavorite", ErrorCategory.Media), false);
            }
        }

        [RelayCommand]
        private async Task PlayPauseAsync()
        {
            var context = CreateErrorContext("PlayPauseAsync", ErrorCategory.Media);
            try
            {
                LastActionWasSkip = false;

                if (IsPlaying)
                {
                    Player?.Pause();
                }
                else
                {
                    Player?.Play();
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        /// <summary>
        ///     Moves the position by <paramref name="seconds" />: back no further than the start, and
        ///     forward only while the target is inside the item
        /// </summary>
        private void SeekBy(int seconds)
        {
            var session = Player?.PlaybackSession;
            if (session == null)
            {
                Logger.LogWarning("Cannot seek - MediaPlayer or PlaybackSession is null");
                return;
            }

            var target = session.Position + TimeSpan.FromSeconds(seconds);
            if (target < TimeSpan.Zero)
            {
                target = TimeSpan.Zero;
            }
            else if (seconds > 0 && target >= session.NaturalDuration)
            {
                Logger.LogWarning("Cannot seek forward {Seconds} seconds - would exceed duration {NaturalDuration}",
                    seconds, session.NaturalDuration);
                return;
            }

            Logger.LogInformation("Seeking {Seconds} seconds from {Position:hh\\:mm\\:ss} to {Target:hh\\:mm\\:ss}",
                seconds, session.Position, target);
            session.Position = target;
        }

        [RelayCommand]
        private Task SkipBackwardAsync(object parameter)
        {
            return SkipAsync("backward", GetSkipSeconds(parameter, PlayerConstants.SkipBackwardSeconds),
                async seconds =>
                {
                    if (await TryHandleHlsBackwardSeekBeforeManifestStartAsync(seconds))
                    {
                        return false;
                    }

                    LogHlsLargeBackwardSeek(seconds);
                    SeekBy(-seconds);
                    return true;
                });
        }

        [RelayCommand]
        private Task SkipForwardAsync(object parameter)
        {
            return SkipAsync("forward", GetSkipSeconds(parameter, PlayerConstants.SkipForwardSeconds),
                seconds =>
                {
                    if (!TryPrepareHlsForwardSeek(ref seconds))
                    {
                        return Task.FromResult(false);
                    }

                    SeekBy(seconds);
                    return Task.FromResult(true);
                });
        }

        // seek: seeks, or returns false when the HLS handling has already dealt with the skip
        private async Task SkipAsync(string direction, int skipSeconds, Func<int, Task<bool>> seek)
        {
            var context = CreateErrorContext(direction == "forward" ? "SkipForwardAsync" : "SkipBackwardAsync",
                ErrorCategory.Media);
            try
            {
                if (!EnsurePlaybackStarted($"skip {direction}"))
                {
                    return;
                }

                CancelResumeForUserSeek($"User initiated {direction} seek");
                NoteSeekStarted(direction);

                LastActionWasSkip = true;

                if (!await seek(skipSeconds))
                {
                    return;
                }

                await UpdatePositionImmediateAsync();

                // The view decides from the timing whether this shows or hides the controls
                ToggleControlsRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        private bool EnsurePlaybackStarted(string action)
        {
            if (_hasVideoStarted)
            {
                return true;
            }

            Logger.LogWarning("Cannot {Action} - playback has not started yet", action);
            return false;
        }

        private static int GetSkipSeconds(object parameter, int defaultSeconds)
        {
            return parameter is int seconds ? seconds : defaultSeconds;
        }

        [RelayCommand]
        private Task PlayNextEpisodeAsync()
        {
            return _mediaNavigationService.NavigateToNextAsync();
        }

        [RelayCommand]
        private async Task ChangeSubtitleAsync(SubtitleTrack subtitle)
        {
            var context = CreateErrorContext("ChangeSubtitleAsync", ErrorCategory.Media);
            try
            {
                if (subtitle != null && subtitle != SelectedSubtitle)
                {
                    Logger.LogInformation(
                        "Subtitle change requested: {SubtitleDisplayTitle} (Index={SubtitleServerStreamIndex})", subtitle.DisplayTitle, subtitle.ServerStreamIndex);
                    PrepareForPlaybackRestart();

                    await _playbackControlService.ChangeSubtitleTrackAsync(subtitle);
                    SelectedSubtitle = subtitle;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        [RelayCommand]
        private async Task ChangeAudioTrackAsync(AudioTrack audioTrack)
        {
            var context = CreateErrorContext("ChangeAudioTrackAsync", ErrorCategory.Media);
            try
            {
                if (audioTrack != null && audioTrack != SelectedAudioTrack)
                {
                    Logger.LogInformation(
                        "Audio track change requested: {AudioTrackDisplayName} (Index={AudioTrackServerStreamIndex})", audioTrack.DisplayName, audioTrack.ServerStreamIndex);
                    if (!_playbackControlService.TrySelectPlayerAudioTrack(audioTrack))
                    {
                        PrepareForPlaybackRestart();
                        await _playbackControlService.ChangeAudioTrackAsync(audioTrack);
                    }

                    SelectedAudioTrack = audioTrack;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context);
            }
        }

        public async Task InitializeAsync(MediaPlaybackParams playbackParams)
        {
            try
            {
                await _initializationSemaphore.WaitAsync();
                try
                {
                    IsLoading = true;
                    _playbackParams = playbackParams;

                    ResetHlsState();
                    ResetPlaybackStateSnapshot();

                    var appPrefs = await _preferencesService.GetAppPreferencesAsync();
                    _preferences = appPrefs;
                    _autoPlayNextEpisode = appPrefs.AutoPlayNextEpisode;

                    _playbackControlService.Initialize(MediaPlayerElement.MediaPlayer, playbackParams);

                    _controllerInputService.ActionTriggered += OnControllerActionTriggered;
                    _controllerInputService.ActionWithParameterTriggered += OnControllerActionWithParameterTriggered;

                    if (MediaPlayerElement.MediaPlayer != null)
                    {
                        MediaPlayerElement.MediaPlayer.MediaOpened += OnMediaOpened;
                        MediaPlayerElement.MediaPlayer.MediaFailed += OnMediaFailed;
                        MediaPlayerElement.MediaPlayer.SeekCompleted += OnSeekCompleted;
                        MediaPlayerElement.MediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
                    }

                    CurrentItem = playbackParams.Item;
                    NavigationSourceParameter = playbackParams.NavigationSourceParameter;

                    if (CurrentItem != null)
                    {
                        _mediaNavigationService.Initialize(playbackParams);
                        await InitializeSkipSegmentsAsync(MediaPlayerElement.MediaPlayer, CurrentItem);
                        await SetupPlaybackAsync();
                    }
                    else
                    {
                        Logger.LogError("Cannot setup playback without a valid item");
                        ErrorMessage = "Failed to load media item";
                        IsError = true;
                    }

                    _positionTimer.Start();
                }
                finally
                {
                    _initializationSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("Initialize", ErrorCategory.Media), false);
                ErrorMessage = "Failed to initialize media player";
                IsError = true;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task SetupPlaybackAsync()
        {
            _hasVideoStarted = false;
            IsIntroSkipAvailable = false;
            IsOutroSkipAvailable = false;

            // A failure reaches InitializeAsync, which reports it and shows the error
            var playbackInfo = await _playbackControlService.GetPlaybackInfoAsync();

            SubtitleTracks.ReplaceAll(_playbackControlService.GetSubtitleTracks(playbackInfo));

            // With no index requested the server applies the file's default subtitle
            SelectedSubtitle = SelectInitialTrack(SubtitleTracks, _playbackParams?.SubtitleStreamIndex,
                s => s.ServerStreamIndex, s => s.IsDefault);
            OnPropertyChanged(nameof(HasSubtitleTracks));

            AudioTracks.ReplaceAll(_playbackControlService.GetAudioTracks(playbackInfo));

            Logger.LogInformation("Tracks: {AudioTracksCount} audio, {SubtitleTracksCount} subtitle",
                AudioTracks.Count, SubtitleTracks.Count - 1);

            SelectedAudioTrack = SelectInitialTrack(AudioTracks, _playbackParams?.AudioStreamIndex,
                a => a.ServerStreamIndex, a => a.IsDefault);

            OnPropertyChanged(nameof(HasMultipleAudioTracks));

            var mediaSource = await _playbackControlService.CreateMediaSourceAsync(playbackInfo);

            _directPlayRetryAttempted = false;
            SyncCurrentStream();
            if (IsStatsVisible)
            {
                UpdatePlaybackStats();
            }

            _playbackControlService.StartPlayback(mediaSource, _playbackParams.StartPositionTicks);

            await _mediaSessionService.ReportPlaybackStartAsync(
                _playbackControlService.CreateReport(_playbackParams.StartPositionTicks ?? 0));

            FireAndForget(() => FindNextItemAsync());
        }

        /// <summary>
        ///     The track playback starts on: the one asked for, else the file's default, else the first
        /// </summary>
        private static T SelectInitialTrack<T>(IEnumerable<T> tracks, int? requestedIndex,
            Func<T, int> serverStreamIndex, Func<T, bool> isDefault) where T : class
        {
            return (requestedIndex.HasValue
                       ? tracks.FirstOrDefault(t => serverStreamIndex(t) == requestedIndex.Value)
                       : null)
                   ?? tracks.FirstOrDefault(isDefault)
                   ?? tracks.FirstOrDefault();
        }

        /// <summary>
        ///     Finds what Next plays, once per playback: the Next button, the next-episode overlay,
        ///     auto-play and Skip Outro all read the result
        /// </summary>
        private async Task FindNextItemAsync()
        {
            var context = CreateErrorContext("FindNextItem", ErrorCategory.Media, ErrorSeverity.Warning);
            try
            {
                // An episode with no queue is looked up in the series' full episode list; that
                // request waits until playback has settled
                if (!_mediaNavigationService.HasQueuedNextItem())
                {
                    await Task.Delay(PlayerConstants.NextEpisodePreloadDelayMs);
                }

                var nextItem = await _mediaNavigationService.GetNextEpisodeAsync();
                IsNextEpisodeAvailable = nextItem != null;
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        /// <summary>
        ///     Stops the stream, the player's timers and its resume at once: leaving the page must
        ///     not let the video play on, and the stop report reads the position they would move
        /// </summary>
        public void StopPlayback()
        {
            StopTimers();
            _playbackControlService.EndResume();
            _playbackControlService.StopStream();
        }

        private async void OnPositionTimerTick(object sender, object e)
        {
            if (_isDisposed)
            {
                Logger.LogDebug("[POSITION-TIMER] Timer fired after disposal, ignoring");
                return;
            }

            try
            {
                if (MediaPlayerElement?.MediaPlayer?.PlaybackSession != null)
                {
                    var session = MediaPlayerElement.MediaPlayer.PlaybackSession;

                    TimeSpan currentPosition;
                    try
                    {
                        currentPosition = session.Position;
                    }
                    catch (Exception posEx)
                    {
                        ErrorHandler.HandleError(posEx, CreateErrorContext("ReadPosition", ErrorCategory.Media));
                        return;
                    }

                    var metadataDuration = GetMetadataDuration();

                    TimeSpan duration;
                    try
                    {
                        duration = ItemDuration(session);
                    }
                    catch (Exception durEx)
                    {
                        ErrorHandler.HandleError(durEx, CreateErrorContext("ReadDuration", ErrorCategory.Media));
                        duration = metadataDuration;
                    }

                    // Guard against invalid duration during buffering or state changes
                    if (duration == TimeSpan.Zero || duration < TimeSpan.FromSeconds(1))
                    {
                        Logger.LogDebug(
                            "[POSITION-TIMER] Skipping position update - invalid duration: {Duration:hh\\:mm\\:ss}, position: {Position:hh\\:mm\\:ss}",
                            duration, currentPosition);
                        return;
                    }

                    if (_sessionState.IsHlsStream && metadataDuration > TimeSpan.FromMinutes(5) &&
                        duration < TimeSpan.FromMinutes(1))
                    {
                        Logger.LogError(
                            "[HLS-CORRUPTION] Duration corruption detected! Natural: {Duration:hh\\:mm\\:ss}, Metadata: {MetadataDuration:hh\\:mm\\:ss}, Position: {Position:hh\\:mm\\:ss}",
                            duration, metadataDuration, currentPosition);
                    }

                    TryApplyHlsManifestChangeAfterBackwardSeek(currentPosition);

                    // Update internal position so the Position property getter can add HLS offset
                    _position = currentPosition;
                    OnPropertyChanged(nameof(Position));
                    Duration = duration;

                    UpdateCustomProgressBar(Position, duration);

                    HandleAutoSkip(Position);

                    // Only when the position has moved: the segment check need not run every tick
                    var positionDelta = Math.Abs((Position - _lastSkipCheckPosition).TotalSeconds);

                    if (positionDelta >= SkipCheckThresholdSeconds)
                    {
                        _lastSkipCheckPosition = Position;

                        await UpdateSkipButtonVisibilityAsync();
                    }

                    if (++_progressReportCounter % ProgressReportTicks == 0)
                    {
                        ReportProgressIfNeeded();
                    }

                    // A DispatcherTimer tick: already on the UI thread the overlay properties need
                    CheckForAutoPlayNext();
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnPositionTimerTick", ErrorCategory.Media), false);
            }
        }

        private void OnBufferingTimeoutTimerTick(object sender, object e)
        {
            if (_isDisposed)
            {
                Logger.LogDebug("[BUFFERING-TIMER] Timer fired after disposal, ignoring");
                return;
            }

            try
            {
                if (TryHandleBufferingTimeout())
                {
                    return;
                }

                if (!IsBuffering)
                {
                    ResetBufferingTimeout();
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("OnBufferingTimeoutTimerTick", ErrorCategory.Media));
            }
        }

        private bool TryHandleBufferingTimeout()
        {
            if (!_bufferingStartTime.HasValue || !IsBuffering)
            {
                return false;
            }

            var bufferingDuration = DateTime.UtcNow - _bufferingStartTime.Value;
            if (bufferingDuration.TotalSeconds < BufferingTimeoutSeconds)
            {
                return false;
            }

            var actualPosition = GetCurrentPlaybackPosition();
            Logger.LogWarning(
                "[BUFFERING-TIMEOUT] Buffering timeout reached after {BufferingDurationTotalSeconds:F1}s at position {ActualPosition:hh\\:mm\\:ss}, HLS: {SessionStateIsHlsStream}", bufferingDuration.TotalSeconds, actualPosition, _sessionState.IsHlsStream);

            if (TryRecoverHlsBuffering())
            {
                return true;
            }

            HandleBufferingTimeoutFailure();
            return true;
        }

        /// <summary>
        ///     Called when the player reports a failure. A failed direct play is retried once as
        ///     a server stream; returns true when that retry has started, so the caller shows no
        ///     error.
        /// </summary>
        public bool TryRecoverFromMediaFailure()
        {
            if (_directPlayRetryAttempted || _statsMediaSource == null ||
                !string.IsNullOrEmpty(_statsMediaSource.TranscodingUrl))
            {
                return false;
            }

            _directPlayRetryAttempted = true;
            Logger.LogWarning("Direct play failed for {ItemName}; retrying as a server stream", CurrentItem?.Name);
            FireAndForget(async () =>
            {
                await RunOnUIThreadAsync(PrepareForPlaybackRestart).ConfigureAwait(false);
                await _playbackControlService.RetryAsServerStreamAsync().ConfigureAwait(false);
            });
            return true;
        }

        /// <summary>
        ///     Reopens the stream with the current tracks, at <paramref name="position" /> or where
        ///     playback is now, and lets the resume flow land it there
        /// </summary>
        public async Task RestartStreamAsync(string restartReason, TimeSpan? position = null)
        {
            PrepareForPlaybackRestart();
            await _playbackControlService.RestartStreamAsync(restartReason, position);
        }

        private void PrepareForPlaybackRestart()
        {
            // Allow resume flow to run again after restart.
            _hasVideoStarted = false;
            _resumeAttemptInProgress = false;
            _actualResumePosition = TimeSpan.Zero;
            ResetPlaybackStateSnapshot();
            _sessionState.ResetSeekTracking();
        }

        private void HandleBufferingTimeoutFailure()
        {
            ResetBufferingTimeout();
            Logger.LogError("[BUFFERING-TIMEOUT] Unable to recover, giving up");

            ErrorMessage = "Playback is taking too long. Please check your network connection.";
            IsError = true;

            FireAndForget(async () =>
            {
                await Task.Delay(PlayerConstants.ErrorAutoDismissDelayMs);

                await UiHelper.RunOnUIThreadAsync(() =>
                {
                    IsError = false;
                    ErrorMessage = string.Empty;
                }, logger: Logger);

                await LeavePlayerAsync();
                Logger.LogInformation("[BUFFERING-TIMEOUT] Navigated back after buffering timeout");
            });
        }

        private void ResetBufferingTimeout()
        {
            _bufferingTimeoutTimer.Stop();
            _bufferingStartTime = null;
        }

        private MediaStream StatsVideoStream =>
            _statsMediaSource?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Video);

        private MediaStream StatsAudioStream =>
            MediaStreamHelper.AudioStream(_statsMediaSource?.MediaStreams, _playbackParams?.AudioStreamIndex);

        private async void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            try
            {
                await _playbackStateCoordinator.HandlePlaybackStateChangedAsync(new PlaybackStateChangeContext
                {
                    IsDisposed = _isDisposed,
                    Session = sender,
                    SessionState = _sessionState,
                    BufferingTimeoutTimer = _bufferingTimeoutTimer,
                    SetRawPosition = position => _position = position,
                    GetDisplayPosition = () => Position,
                    GetMetadataDuration = GetMetadataDuration,
                    HandleHlsBufferingFix = HandleHlsBufferingFix,
                    RunOnUiThreadAsync = RunOnUIThreadAsync,
                    NotifyIsBufferingChanged = () => OnPropertyChanged(nameof(IsBuffering)),
                    NotifyIsPlayingChanged = () => OnPropertyChanged(nameof(IsPlaying)),
                    NotifyIsPausedChanged = () => OnPropertyChanged(nameof(IsPaused)),
                    GetBufferingStartTime = () => _bufferingStartTime,
                    SetBufferingStartTime = value => _bufferingStartTime = value,
                    GetHasManifestOffset = () => _playbackControlService.HlsManifestOffset > TimeSpan.Zero,
                    SetPlaybackState = state =>
                    {
                        _lastPlaybackState = state;
                        _hasPlaybackStateSnapshot = true;
                    },
                    GetHasVideoStarted = () => _hasVideoStarted,
                    SetHasVideoStarted = value => _hasVideoStarted = value,
                    HandleResumeOnPlaybackStartAsync = HandleResumeOnPlaybackStartAsync,
                    ApplyManifestOffsetFromBuffering = offset =>
                        SetHlsManifestOffset(offset, false, "Detected during buffering.")
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnPlaybackStateChanged", ErrorCategory.Media), false);
            }
        }

        public Task LeavePlayerAsync()
        {
            return _mediaNavigationService.NavigateBackToOriginAsync();
        }

        public async Task LeaveAfterPlaybackFailureAsync()
        {
            // The error shows before the page goes
            await UiHelper.WhenIdleAsync();
            await LeavePlayerAsync();
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            if (_isDisposed)
            {
                return;
            }

            try
            {
                var sessionSnapshot = PlaybackSessionSnapshot.Capture(
                    sender.PlaybackSession,
                    _sessionState.IsHlsStream);
                Logger.LogInformation(
                    "[MEDIA-OPENED] Natural duration: {NaturalDurationTotalSeconds}s, " +
                    "CanSeek: {SessionSnapshotCanSeek}, " +
                    "IsProtected: {SessionSnapshotIsProtected}", sessionSnapshot.NaturalDuration.TotalSeconds, sessionSnapshot.CanSeek, sessionSnapshot.IsProtected);

                AppMemory.Log(Logger, "After media opened");

                // Pass the session to avoid cross-thread access to MediaPlayerElement
                var openPosition = GetCurrentPlaybackPosition(sender.PlaybackSession);
                Logger.LogDebug("[MEDIA-OPENED] Current position: {OpenPositionTotalSeconds}s", openPosition.TotalSeconds);

                _playbackControlService.SelectChosenAudioTrack();
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("OnMediaOpened", ErrorCategory.Media));
            }
        }

        private MediaPlaybackState? GetPlaybackStateSnapshot()
        {
            if (_hasPlaybackStateSnapshot)
            {
                return _lastPlaybackState;
            }

            try
            {
                return MediaPlayerElement?.MediaPlayer?.PlaybackSession?.PlaybackState;
            }
            catch
            {
                // A closed player's session throws from every property
                return null;
            }
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            if (_isDisposed)
            {
                return;
            }

            try
            {
                var known = args.ExtendedErrorCode?.HResult switch
                {
                    -1072875854 => " (MF_E_UNSUPPORTED_BYTESTREAM_TYPE: unsupported media format or codec)",
                    -1072875802 => " (MF_E_INVALID_FORMAT)",
                    -2147024882 => " (E_OUTOFMEMORY)",
                    -1072873821 => " (MF_E_TRANSFORM_TYPE_NOT_SET: codec issue)",
                    _ => string.Empty
                };
                Logger.LogError("[MEDIA-FAILED] Error: {ArgsError}, " +
                                "ExtendedError HResult: 0x{ExtendedErrorCodeHResult:X8}{Known}, " +
                                "Message: {ArgsErrorMessage}", args.Error, args.ExtendedErrorCode?.HResult, known, args.ErrorMessage);

                AppMemory.Log(Logger, "At media failure", LogLevel.Error);
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("OnMediaFailed", ErrorCategory.Media));
            }
        }

        private void ReportProgressIfNeeded()
        {
            // Don't report progress during seeks to prevent incorrect positions
            if (_sessionState.PendingSeekCount > 0)
            {
                Logger.LogDebug(
                    "Skipping progress report - seek in progress (PendingSeeks: {SessionStatePendingSeekCount})", _sessionState.PendingSeekCount);
                return;
            }

            if (!IsPaused)
            {
                // Position includes the HLS manifest offset
                _mediaSessionService.ReportPlaybackProgress(_playbackControlService.CreateReport(Position.Ticks));
            }
        }

        // The item's length: the metadata's when known (an HLS stream's NaturalDuration covers only
        // its current manifest), else the player's
        private TimeSpan ItemDuration(MediaPlaybackSession session)
        {
            var metadataDuration = GetMetadataDuration();
            return metadataDuration > TimeSpan.Zero ? metadataDuration : session.NaturalDuration;
        }

        public TimeSpan GetMetadataDuration()
        {
            if (CurrentItem?.RunTimeTicks != null && CurrentItem.RunTimeTicks > 0)
            {
                return TimeSpan.FromTicks(CurrentItem.RunTimeTicks.Value);
            }

            return TimeSpan.Zero;
        }

        private void CheckForAutoPlayNext()
        {
            var itemDuration = Duration;
            if (!_hasAutoPlayedNext && itemDuration > TimeSpan.Zero)
            {
                var percentComplete = Position.TotalSeconds / itemDuration.TotalSeconds * 100;

                // Episodes have no Skip Credits, so the outro overlay is never in the way
                var shouldShow = percentComplete >= 95 && HasNextEpisode;
                if (shouldShow != NextEpisodeButtonOverlayVisible)
                {
                    Logger.LogDebug(
                        "[OVERLAY] NextEpisode overlay changing from {NextEpisodeButtonOverlayVisible} to {ShouldShow} at {PercentComplete:F2}% complete", NextEpisodeButtonOverlayVisible, shouldShow, percentComplete);
                    NextEpisodeButtonOverlayVisible = shouldShow;
                }

                if (_autoPlayNextEpisode && percentComplete >= PlayerConstants.AutoPlayNextThresholdPercent &&
                    HasNextEpisode)
                {
                    _hasAutoPlayedNext = true;
                    Logger.LogInformation(
                        "Auto-playing next episode at {PercentComplete:F2}% (Position={Position:hh\\:mm\\:ss}, Duration={Duration:hh\\:mm\\:ss})", percentComplete, Position, itemDuration);
                    FireAndForget(() => PlayNextEpisodeAsync());
                }
            }
        }

        private async Task UpdatePositionImmediateAsync()
        {
            if (_isDisposed)
            {
                return;
            }

            try
            {
                if (MediaPlayerElement?.MediaPlayer?.PlaybackSession == null)
                {
                    return;
                }

                await RunOnUIThreadAsync(() =>
                {
                    var session = MediaPlayerElement.MediaPlayer.PlaybackSession;
                    // Update internal position so the Position property getter can add HLS offset
                    _position = session.Position;
                    OnPropertyChanged(nameof(Position));
                    Duration = ItemDuration(session);
                    UpdateCustomProgressBar(Position, Duration);
                });
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("UpdatePositionImmediate", ErrorCategory.Media), false);
            }
        }

        private void UpdateCustomProgressBar(TimeSpan currentPosition, TimeSpan duration)
        {
            CurrentTimeText = TimeFormattingHelper.FormatTime(currentPosition);
            DurationText = TimeFormattingHelper.FormatTime(duration);

            if (duration > TimeSpan.Zero)
            {
                var percentage = currentPosition.TotalSeconds / duration.TotalSeconds * 100.0;
                percentage = Math.Max(0, Math.Min(100, percentage));

                // Only update if changed significantly to avoid excessive updates
                if (Math.Abs(PositionPercentage - percentage) > 0.1)
                {
                    PositionPercentage = percentage;
                }
            }
            else
            {
                PositionPercentage = 0.0;
            }

            UpdateEndTime();
        }

        private void UpdateEndTime()
        {
            try
            {
                if (MediaPlayerElement?.MediaPlayer?.PlaybackSession != null)
                {
                    // Both in the item's terms: the manifest offset is in Position
                    var remainingTime = Duration - Position;

                    if (remainingTime.TotalSeconds > 0)
                    {
                        var endTime = DateTime.Now.Add(remainingTime);
                        EndsAtTimeText = $"Ends at {endTime:h:mm tt}";
                        IsEndsAtTimeVisible = true;
                    }
                    else
                    {
                        IsEndsAtTimeVisible = false;
                    }
                }
                else
                {
                    IsEndsAtTimeVisible = false;
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("UpdateEndTime", ErrorCategory.Media, ErrorSeverity.Warning));
                IsEndsAtTimeVisible = false;
            }
        }

        private void StopTimers()
        {
            _positionTimer.Stop();
            _statsUpdateTimer.Stop();
            _bufferingTimeoutTimer.Stop();
            _bufferingStartTime = null;
        }

        public void HandleAppBackgroundChanged(bool isInBackground)
        {
            if (_isDisposed)
            {
                return;
            }

            if (isInBackground)
            {
                StopTimers();
                return;
            }

            RestartTimers();
            SyncBufferingTimeoutFromSession();
        }

        private void RestartTimers()
        {
            if (_isDisposed)
            {
                return;
            }

            _positionTimer.Start();
            if (IsStatsVisible)
            {
                _statsUpdateTimer.Start();
                UpdatePlaybackStats();
            }
        }

        private void SyncBufferingTimeoutFromSession()
        {
            try
            {
                var session = MediaPlayerElement?.MediaPlayer?.PlaybackSession;
                if (session == null)
                {
                    return;
                }

                var isBuffering = session.PlaybackState == MediaPlaybackState.Buffering;
                if (isBuffering)
                {
                    if (!_bufferingStartTime.HasValue)
                    {
                        _bufferingStartTime = DateTime.UtcNow;
                        _bufferingTimeoutTimer.Start();
                        Logger.LogDebug("[BUFFERING-TIMEOUT] Resume sync detected buffering; starting timer");
                    }

                    return;
                }

                _bufferingStartTime = null;
                _bufferingTimeoutTimer.Stop();
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("SyncBufferingTimeout", ErrorCategory.Media, ErrorSeverity.Warning));
            }
        }

        public Task ReportPlaybackStoppedAsync()
        {
            var report = _playbackControlService.CreateReport(Position.Ticks);
            if (report == null)
            {
                return Task.CompletedTask;
            }

            Logger.LogInformation("Reporting playback stopped at position: {Position}", TimeSpan.FromTicks(report.PositionTicks));
            return _mediaSessionService.ReportPlaybackStoppedAsync(report);
        }

        private TimeSpan GetCurrentPlaybackPosition(MediaPlaybackSession session = null)
        {
            return WithManifestOffset(GetPlayerPosition(session));
        }

        private void ResetPlaybackStateSnapshot()
        {
            _lastPlaybackState = MediaPlaybackState.None;
            _hasPlaybackStateSnapshot = false;
        }

        /// <summary>
        ///     Clears per-playback state once the stop has been reported. The page has already
        ///     stopped the timers and the player; the MediaPlayer's own handlers go with it on the
        ///     UI thread, and the singleton services are re-initialized by the next playback.
        /// </summary>
        public void CleanupRemaining()
        {
            _statsMediaSource = null;
            _serverTranscodingInfo = null;
            ResetSkipSegments();
            Logger.LogDebug("Cleared playback stats and skip segment state");
            AppMemory.Log(Logger, "After leaving the player");
        }

        protected override void DisposeManaged()
        {
            // Mark as disposed first to prevent any new operations
            _isDisposed = true;

            // Stopped and unsubscribed, or a tick could fire after disposal
            _positionTimer.Stop();
            _positionTimer.Tick -= OnPositionTimerTick;
            _statsUpdateTimer.Stop();
            _statsUpdateTimer.Tick -= OnStatsUpdateTimerTick;
            _bufferingTimeoutTimer.Stop();
            _bufferingTimeoutTimer.Tick -= OnBufferingTimeoutTimerTick;

            _controllerInputService.ActionTriggered -= OnControllerActionTriggered;
            _controllerInputService.ActionWithParameterTriggered -= OnControllerActionWithParameterTriggered;

            var player = MediaPlayerElement?.MediaPlayer;
            if (player != null)
            {
                player.MediaOpened -= OnMediaOpened;
                player.MediaFailed -= OnMediaFailed;
                player.SeekCompleted -= OnSeekCompleted;
                player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;

                // Its video surfaces and decoder are held until disposed, not when the page goes;
                // the playback service (a singleton) would otherwise keep it until the next video
                _playbackControlService.ReleasePlayer(player);
                MediaPlayerElement.SetMediaPlayer(null);
                player.Dispose();
            }

            // Only dispose transient services (ControllerInputService is transient)
            // Don't dispose singleton services as they need to maintain state
            _controllerInputService.Dispose();

            base.DisposeManaged();
        }
    }
}
