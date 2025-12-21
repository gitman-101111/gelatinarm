using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Gelatinarm.ViewModels
{
    /// <summary>
    ///     ViewModel for the MediaPlayerPage handling all media playback logic
    /// </summary>
    public partial class MediaPlayerViewModel : BaseViewModel
    {
        private const double SKIP_CHECK_THRESHOLD_SECONDS = 0.5;
        private volatile bool _isDisposed = false; // Track disposal state
        private readonly IEpisodeQueueService _episodeQueueService;
        private readonly IMediaControlService _mediaControlService;
        private readonly IMediaControllerService _mediaControllerService;
        private readonly IMediaNavigationService _mediaNavigationService;
        private readonly IMediaOptimizationService _mediaOptimizationService;
        private readonly IMediaPlaybackService _mediaPlaybackService;

        private readonly INavigationService _navigationService;

        // Services
        private readonly IPlaybackControlService _playbackControlService;
        private readonly IPlaybackStatisticsService _playbackStatisticsService;
        private readonly IPreferencesService _preferencesService;
        private readonly ISkipSegmentService _skipSegmentService;
        private readonly ISubtitleService _subtitleService;

        [ObservableProperty] private bool _areControlsVisible = true;

        [ObservableProperty] private ObservableCollection<AudioTrack> _audioTracks = new();

        private DispatcherTimer _controlVisibilityTimer;

        // Observable properties
        [ObservableProperty] private BaseItemDto _currentItem;

        [ObservableProperty] private PlaybackStats _currentStats;

        [ObservableProperty] private string _currentTimeText = "0:00";

        [ObservableProperty] private TimeSpan _duration;

        [ObservableProperty] private string _durationText = "0:00";

        [ObservableProperty] private string _endsAtTimeText;

        private bool _hasAutoPlayedNext = false;

        // Track/quality change tracking
        private bool _hasReportedPlaybackStart = false;
        private int _progressReportCounter = 0;
        private TimeSpan _actualResumePosition = TimeSpan.Zero; // Track actual resume position for corruption detection

        // Track when video playback has started
        private bool _hasVideoStarted = false;

        // Track cleanup state to prevent race conditions
        private Task _cleanupTask;
        private readonly SemaphoreSlim _initializationSemaphore = new SemaphoreSlim(1, 1);

        // Track whether auto-play next episode is enabled
        private bool _autoPlayNextEpisode = false;

        [ObservableProperty] private bool _isAudioVisualizationActive;

        [ObservableProperty] private bool _isBuffering;

        [ObservableProperty] private bool _isEndsAtTimeVisible;

        private bool _isIntroSkipAvailable = false;

        private bool _isNextEpisodeAvailable = false;

        private bool _isOutroSkipAvailable = false;

        [ObservableProperty] private bool _isPaused;
        [ObservableProperty] private bool _isPlaying;

        [ObservableProperty] private bool _isStatsOverlayVisible;

        [ObservableProperty] private bool _isStatsVisible;

        // Skip button check throttling
        private TimeSpan _lastSkipCheckPosition = TimeSpan.Zero;

        [ObservableProperty] private object _navigationSourceParameter;
        private MediaPlaybackParams _currentPlaybackParams;

        [ObservableProperty] private BaseItemDto _nextEpisode;

        private bool _nextEpisodeButtonOverlayVisible = false;
        private string _playSessionId;
        private readonly PlaybackSessionState _sessionState = new PlaybackSessionState();

        // State tracking
        private MediaPlaybackParams _playbackParams;

        private TimeSpan _position = TimeSpan.Zero;
        public TimeSpan Position
        {
            get
            {
                // For HLS streams, we may need to add manifest offset from two sources:
                // 1. PlaybackControlService.HlsManifestOffset - for initial resume operations
                // 2. Local session offset - for large seeks during playback that create new manifests

                // Use PlaybackControlService offset for resume scenarios
                return _sessionState.GetDisplayPosition(_position, _playbackControlService?.HlsManifestOffset ?? TimeSpan.Zero);
            }
            set => SetProperty(ref _position, value);
        }

        [ObservableProperty] private double _positionPercentage;

        // Timers
        private DispatcherTimer _positionTimer;
        private CancellationTokenSource _progressReportCancellationTokenSource;



        [ObservableProperty] private AudioTrack _selectedAudioTrack;


        [ObservableProperty] private SubtitleTrack _selectedSubtitle;

        [ObservableProperty] private string _statsText;

        private DispatcherTimer _statsUpdateTimer;
        private DispatcherTimer _bufferingTimeoutTimer;
        private DateTime? _bufferingStartTime;
        private const int BUFFERING_TIMEOUT_SECONDS = 30; // Default buffering timeout
        private readonly ResumeRetryCoordinator _resumeRetryCoordinator;
        private readonly BufferingStateCoordinator _bufferingStateCoordinator;
        private readonly PlaybackStateOrchestrator _playbackStateOrchestrator;
        private readonly ResumeFlowCoordinator _resumeFlowCoordinator;
        private readonly SeekCompletionCoordinator _seekCompletionCoordinator;
        private bool _resumeAttemptInProgress;

        [ObservableProperty] private ObservableCollection<SubtitleTrack> _subtitleTracks = new();

        public MediaPlayerViewModel(ILogger<MediaPlayerViewModel> logger) : base(logger)
        {
            _playbackControlService = GetRequiredService<IPlaybackControlService>();
            _subtitleService = GetRequiredService<ISubtitleService>();
            _mediaOptimizationService = GetRequiredService<IMediaOptimizationService>();
            _preferencesService = GetRequiredService<IPreferencesService>();
            _mediaPlaybackService = GetRequiredService<IMediaPlaybackService>();
            _navigationService = GetRequiredService<INavigationService>();
            _episodeQueueService = GetRequiredService<IEpisodeQueueService>();
            _mediaNavigationService = GetRequiredService<IMediaNavigationService>();
            _playbackStatisticsService = GetRequiredService<IPlaybackStatisticsService>();
            _mediaControllerService = GetRequiredService<IMediaControllerService>();
            _skipSegmentService = GetRequiredService<ISkipSegmentService>();
            _mediaControlService = GetRequiredService<IMediaControlService>();

            _resumeRetryCoordinator = new ResumeRetryCoordinator(logger);
            _bufferingStateCoordinator = new BufferingStateCoordinator(logger, BUFFERING_TIMEOUT_SECONDS);
            _playbackStateOrchestrator = new PlaybackStateOrchestrator(logger, _bufferingStateCoordinator);
            _resumeFlowCoordinator = new ResumeFlowCoordinator(logger, _playbackControlService, _resumeRetryCoordinator);
            _seekCompletionCoordinator = new SeekCompletionCoordinator(logger);
            InitializeTimers();
        }

        public Type NavigationSourcePage => _playbackParams?.NavigationSourcePage;

        public bool IsIntroSkipAvailable
        {
            get => _isIntroSkipAvailable;
            set
            {
                if (SetProperty(ref _isIntroSkipAvailable, value))
                {
                    // Notify when intro skip becomes available
                    if (value)
                    {
                        OnSkipButtonBecameAvailable(SkipSegmentType.Intro);
                    }
                }
            }
        }

        public bool IsOutroSkipAvailable
        {
            get => _isOutroSkipAvailable;
            set
            {
                if (SetProperty(ref _isOutroSkipAvailable, value))
                {
                    // Notify when outro skip becomes available
                    if (value)
                    {
                        OnSkipButtonBecameAvailable(SkipSegmentType.Outro);
                    }
                }
            }
        }

        public bool IsNextEpisodeAvailable
        {
            get => _isNextEpisodeAvailable;
            set => SetProperty(ref _isNextEpisodeAvailable, value);
        }

        // Computed property for Episodes button visibility
        // Hide it when the Next Episode overlay is visible to avoid duplicate buttons
        public bool IsEpisodesButtonVisible => IsNextEpisodeAvailable && !NextEpisodeButtonOverlayVisible;
        public bool NextEpisodeButtonOverlayVisible
        {
            get => _nextEpisodeButtonOverlayVisible;
            set
            {
                if (SetProperty(ref _nextEpisodeButtonOverlayVisible, value))
                {
                    // Notify when next episode button becomes available
                    if (value)
                    {
                        OnSkipButtonBecameAvailable(SkipSegmentType.Outro); // Reuse outro type for next episode
                    }

                    // Notify that Episodes button visibility might have changed
                    OnPropertyChanged(nameof(IsEpisodesButtonVisible));
                }
            }
        }

        // Computed properties for UI visibility
        public bool HasMultipleAudioTracks => AudioTracks?.Count > 1;
        public bool HasSubtitleTracks => SubtitleTracks?.Count > 1; // More than just "None" option
        public bool LastActionWasSkip { get; private set; }

        // Media player reference
        public MediaPlayerElement MediaPlayerElement { get; set; }

        // Event for skip button availability
        public event EventHandler<SkipSegmentType> SkipButtonBecameAvailable;

        // Event for requesting control visibility toggle
        public event EventHandler ToggleControlsRequested;

        private void InitializeTimers()
        {
            _positionTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(MediaPlayerConstants.POSITION_TIMER_INTERVAL_MS)
            };
            _positionTimer.Tick += OnPositionTimerTick;

            _controlVisibilityTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(MediaPlayerConstants.CONTROLS_HIDE_CHECK_INTERVAL_MS)
            };
            _controlVisibilityTimer.Tick += OnControlVisibilityTimerTick;

            _statsUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _statsUpdateTimer.Tick += OnStatsUpdateTimerTick;

            // Initialize buffering timeout timer
            _bufferingTimeoutTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _bufferingTimeoutTimer.Tick += OnBufferingTimeoutTimerTick;

            // Initialize CurrentStats to prevent null reference exceptions
            CurrentStats = new PlaybackStats
            {
                VideoInfo = string.Empty,
                BufferInfo = string.Empty,
                NetworkInfo = string.Empty,
                PlaybackInfo = string.Empty,
                BitrateInfo = string.Empty
            };
        }

        // Commands
        [RelayCommand]
        private async Task PlayPause()
        {
            var context = CreateErrorContext("PlayPause", ErrorCategory.Media);
            try
            {
                LastActionWasSkip = false; // Clear skip flag when play/pause is pressed

                if (!EnsureMediaControlService("toggle play/pause"))
                {
                    return;
                }

                if (IsPlaying)
                {
                    _mediaControlService.Pause();
                    // Update state immediately for responsive UI
                    IsPlaying = false;
                    IsPaused = true;
                }
                else
                {
                    _mediaControlService.Play();
                    // Update state immediately for responsive UI
                    IsPlaying = true;
                    IsPaused = false;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, true);
            }
        }

        [RelayCommand]
        private async Task Stop()
        {
            var context = CreateErrorContext("Stop", ErrorCategory.Media);
            try
            {
                _mediaControlService.Stop();
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        [RelayCommand]
        private async Task SkipBackward(object parameter)
        {
            var context = CreateErrorContext("SkipBackward", ErrorCategory.Media);
            try
            {
                if (!EnsurePlaybackStarted("skip backward"))
                {
                    return;
                }

                CancelResumeForUserSeek("User initiated backward seek");

                LastActionWasSkip = true;

                if (!EnsureMediaControlService("skip backward"))
                {
                    return;
                }

                var skipSeconds = GetSkipSeconds(parameter, MediaPlayerConstants.SKIP_BACKWARD_SECONDS);

                if (await TryHandleHlsBackwardSeekBeforeManifestStartAsync(skipSeconds))
                {
                    return;
                }

                LogHlsLargeBackwardSeek(skipSeconds);

                _mediaControlService.SeekBackward(skipSeconds);
                // Force immediate position update
                await UpdatePositionImmediateAsync();

                // Show skip indicator
                ShowSkipIndicator(FormatSkipIndicator(skipSeconds, forward: false));

                // Always notify about skip - the view will decide how to handle it based on timing
                ToggleControlsRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        [RelayCommand]
        private async Task SkipForward(object parameter)
        {
            var context = CreateErrorContext("SkipForward", ErrorCategory.Media);
            try
            {
                if (!EnsurePlaybackStarted("skip forward"))
                {
                    return;
                }

                CancelResumeForUserSeek("User initiated forward seek");

                LastActionWasSkip = true;

                if (!EnsureMediaControlService("skip forward"))
                {
                    return;
                }

                var skipSeconds = GetSkipSeconds(parameter, MediaPlayerConstants.SKIP_FORWARD_SECONDS);

                if (!TryPrepareHlsForwardSeek(ref skipSeconds))
                {
                    return;
                }

                _mediaControlService.SeekForward(skipSeconds);
                // Force immediate position update
                await UpdatePositionImmediateAsync();

                // Show skip indicator
                ShowSkipIndicator(FormatSkipIndicator(skipSeconds, forward: true));

                // Always notify about skip - the view will decide how to handle it based on timing
                ToggleControlsRequested?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        private void CancelResumeForUserSeek(string reason)
        {
            if (_playbackParams?.StartPositionTicks == null || _playbackParams.StartPositionTicks == 0)
            {
                return;
            }

            Logger?.LogInformation($"[RESUME-CANCEL] {reason} - clearing resume target");
            _playbackParams.StartPositionTicks = null;
            _sessionState.PendingSeekPositionAfterQualitySwitch = 0;
            _sessionState.HasPerformedInitialSeek = true;
            _playbackControlService?.CancelPendingResume(reason);
        }

        private bool EnsurePlaybackStarted(string action)
        {
            if (_hasVideoStarted)
            {
                return true;
            }

            Logger.LogWarning($"Cannot {action} - playback has not started yet");
            return false;
        }

        private bool EnsureMediaControlService(string action)
        {
            if (_mediaControlService != null)
            {
                return true;
            }

            Logger.LogWarning($"MediaControlService is null - cannot {action}");
            return false;
        }

        private static int GetSkipSeconds(object parameter, int defaultSeconds)
        {
            return parameter is int seconds ? seconds : defaultSeconds;
        }

        private static string FormatSkipIndicator(int seconds, bool forward)
        {
            var direction = forward ? "+" : "-";
            var label = seconds >= 60 ? $"{seconds / 60}m" : $"{seconds}s";
            return $"{direction}{label}";
        }

        private TimeSpan GetCurrentRawPosition()
        {
            var offset = _playbackControlService?.HlsManifestOffset ?? TimeSpan.Zero;
            var rawPosition = GetCurrentPlaybackPosition() - offset;
            return rawPosition < TimeSpan.Zero ? TimeSpan.Zero : rawPosition;
        }

        private async Task<bool> TryHandleHlsBackwardSeekBeforeManifestStartAsync(int skipSeconds)
        {
            if (_playbackControlService?.HlsManifestOffset <= TimeSpan.Zero)
            {
                return false;
            }

            var currentRawPosition = GetCurrentRawPosition();
            var targetRawPosition = currentRawPosition - TimeSpan.FromSeconds(skipSeconds);

            if (targetRawPosition >= TimeSpan.Zero)
            {
                return false;
            }

            var actualCurrentPosition = GetCurrentPlaybackPosition();
            var actualTargetPosition = actualCurrentPosition - TimeSpan.FromSeconds(skipSeconds);

            if (actualTargetPosition < TimeSpan.Zero)
            {
                actualTargetPosition = TimeSpan.Zero;
            }

            Logger.LogInformation($"[HLS-SEEK] Backward seek would go before manifest start. Restarting at {actualTargetPosition:mm\\:ss}");

            var restartTicks = actualTargetPosition.Ticks;
            var wasPlaying = IsPlaying;

            _playbackParams.StartPositionTicks = restartTicks;
            _mediaControlService.Stop();

            if (_playbackControlService is PlaybackControlService pcs)
            {
                pcs.HlsManifestOffset = TimeSpan.Zero;
            }

            await InitializeAsync(_playbackParams);

            if (wasPlaying)
            {
                _mediaControlService.Play();
            }

            return true;
        }

        private void LogHlsLargeBackwardSeek(int skipSeconds)
        {
            if (!_sessionState.IsHlsStream || skipSeconds < 60)
            {
                return;
            }

            var currentPosWithOffset = GetCurrentPlaybackPosition();
            var targetPos = currentPosWithOffset - TimeSpan.FromSeconds(skipSeconds);

            if (targetPos < TimeSpan.Zero)
            {
                targetPos = TimeSpan.Zero;
            }

            Logger.LogInformation($"[HLS] Large backward seek: {skipSeconds}s from {currentPosWithOffset:mm\\:ss} to {targetPos:mm\\:ss} - server may create new manifest");
        }

        private bool TryPrepareHlsForwardSeek(ref int skipSeconds)
        {
            if (!_sessionState.IsHlsStream)
            {
                return true;
            }

            var rawPosition = GetCurrentRawPosition();
            var hlsManifestOffset = _playbackControlService?.HlsManifestOffset ?? TimeSpan.Zero;
            var currentPosWithOffset = rawPosition + hlsManifestOffset;
            var targetPos = currentPosWithOffset + TimeSpan.FromSeconds(skipSeconds);

            var metadataDuration = CurrentItem?.RunTimeTicks != null && CurrentItem.RunTimeTicks > 0
                ? TimeSpan.FromTicks(CurrentItem.RunTimeTicks.Value)
                : TimeSpan.Zero;

            if (metadataDuration > TimeSpan.Zero && targetPos >= metadataDuration - TimeSpan.FromSeconds(30))
            {
                Logger.LogWarning($"[HLS] Preventing seek to {targetPos:mm\\:ss} - too close to end ({metadataDuration:mm\\:ss}). This could corrupt the HLS manifest.");

                var safeEndPosition = metadataDuration - TimeSpan.FromSeconds(35);
                if (currentPosWithOffset < safeEndPosition)
                {
                    var adjustedSkip = (int)(safeEndPosition - currentPosWithOffset).TotalSeconds;
                    Logger.LogInformation($"[HLS] Adjusted skip to {adjustedSkip}s to avoid end-of-stream issues");
                    skipSeconds = adjustedSkip;
                    targetPos = currentPosWithOffset + TimeSpan.FromSeconds(skipSeconds);
                }
                else
                {
                    Logger.LogInformation("[HLS] Already close to end, skipping forward disabled");
                    return false;
                }
            }

            if (skipSeconds >= 60 && hlsManifestOffset > TimeSpan.Zero)
            {
                Logger.LogInformation($"[HLS-MANIFEST-OFFSET] Large seek with offset. Current raw: {rawPosition:mm\\:ss}, offset: {hlsManifestOffset:mm\\:ss}, target absolute: {targetPos:mm\\:ss}");
                _mediaControlService.SeekTo(rawPosition + TimeSpan.FromSeconds(skipSeconds));
                _sessionState.RecordLargeSeek(targetPos);
                return false;
            }

            if (skipSeconds >= 60)
            {
                _sessionState.RecordLargeSeek(targetPos);
                Logger.LogInformation($"[HLS] Large seek detected: {skipSeconds}s forward from {currentPosWithOffset:mm\\:ss} to {targetPos:mm\\:ss} (pending seeks: {_sessionState.PendingSeekCount})");
            }

            return true;
        }

        [RelayCommand]
        private async Task SkipIntro()
        {
            var context = CreateErrorContext("SkipIntro", ErrorCategory.Media);
            try
            {
                if (!EnsurePlaybackStarted("skip intro"))
                {
                    return;
                }

                LastActionWasSkip = true;
                await _skipSegmentService.SkipIntroAsync();

                // Hide the skip intro button after skipping
                IsIntroSkipAvailable = false;
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        [RelayCommand]
        private async Task SkipOutro()
        {
            var context = CreateErrorContext("SkipOutro", ErrorCategory.Media);
            try
            {
                if (!EnsurePlaybackStarted("skip outro"))
                {
                    return;
                }

                LastActionWasSkip = true;
                await _skipSegmentService.SkipOutroAsync();

                // Hide the skip outro button after skipping
                IsOutroSkipAvailable = false;
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        [RelayCommand]
        private async Task PlayNextEpisode()
        {
            var context = CreateErrorContext("PlayNextEpisode", ErrorCategory.Media);
            try
            {
                await _mediaNavigationService.NavigateToNextAsync();
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }


        [RelayCommand]
        private async Task ChangeSubtitle(SubtitleTrack subtitle)
        {
            var context = CreateErrorContext("ChangeSubtitle", ErrorCategory.Media);
            bool success;
            try
            {
                if (subtitle != SelectedSubtitle)
                {
                    Logger?.LogInformation(
                        $"Subtitle change requested: {subtitle?.DisplayTitle} (Index={subtitle?.ServerStreamIndex})");
                    PrepareResumeForTrackChange();

                    await _subtitleService.ChangeSubtitleTrackAsync(subtitle);
                    SelectedSubtitle = subtitle;

                    ResetResumeFlagsAfterTrackChange();
                }

                success = true;
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    success = await ErrorHandler.HandleErrorAsync(ex, context, false, true);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                    success = false;
                }
            }

            if (!success)
            {
            }
        }

        [RelayCommand]
        private async Task ChangeAudioTrack(AudioTrack audioTrack)
        {
            var context = CreateErrorContext("ChangeAudioTrack", ErrorCategory.Media);
            bool success;
            try
            {
                if (audioTrack != null && audioTrack != SelectedAudioTrack)
                {
                    Logger?.LogInformation(
                        $"Audio track change requested: {audioTrack.DisplayName} (Index={audioTrack.ServerStreamIndex})");
                    PrepareResumeForTrackChange();

                    await _playbackControlService.ChangeAudioTrackAsync(audioTrack);
                    SelectedAudioTrack = audioTrack;

                    ResetResumeFlagsAfterTrackChange();
                }

                success = true;
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    success = await ErrorHandler.HandleErrorAsync(ex, context, false, true);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                    success = false;
                }
            }

            if (!success)
            {
            }
        }

        [RelayCommand]
        private void ToggleStats()
        {
            _playbackStatisticsService.ToggleVisibility();
            IsStatsVisible = _playbackStatisticsService.IsVisible;
        }

        private void PrepareResumeForTrackChange()
        {
            if (_sessionState.IsHlsStream)
            {
                PrepareHlsTrackChange();
            }
            else
            {
                _sessionState.PendingSeekPositionAfterQualitySwitch = Position.Ticks;
            }
        }

        private void ResetResumeFlagsAfterTrackChange()
        {
            // Reset flags so resume position is applied when video is ready
            _sessionState.HasPerformedInitialSeek = false;
            _hasVideoStarted = false;
        }

        [RelayCommand]
        private async Task PlayPrevious()
        {
            var context = CreateErrorContext("PlayPrevious", ErrorCategory.Media);
            try
            {
                if (_mediaNavigationService.HasPreviousItem())
                {
                    await _mediaNavigationService.NavigateToPreviousAsync();
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        [RelayCommand]
        private async Task PlayNext()
        {
            var context = CreateErrorContext("PlayNext", ErrorCategory.Media);
            try
            {
                if (_mediaNavigationService.HasNextItem())
                {
                    await _mediaNavigationService.NavigateToNextAsync();
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }


        // Public methods
        public async Task InitializeAsync(MediaPlaybackParams playbackParams)
        {
            try
            {
                // Ensure only one initialization happens at a time
                await _initializationSemaphore.WaitAsync();
                try
                {
                    // Wait for any pending cleanup to complete before initializing
                    if (_cleanupTask != null && !_cleanupTask.IsCompleted)
                    {
                        Logger.LogInformation("Waiting for previous cleanup to complete before initializing");
                        await _cleanupTask;
                        Logger.LogInformation("Previous cleanup completed, proceeding with initialization");
                    }

                    IsLoading = true;
                    _playbackParams = playbackParams;

                    // Reset HLS state for new playback
                    ResetHlsState();

                    // Load user preferences
                    var appPrefs = await _preferencesService.GetAppPreferencesAsync();
                    _autoPlayNextEpisode = appPrefs.AutoPlayNextEpisode;

                    // Initialize services with playback params
                    await _playbackControlService.InitializeAsync(MediaPlayerElement.MediaPlayer, playbackParams);
                    await _subtitleService.InitializeAsync(MediaPlayerElement.MediaPlayer, playbackParams);
                    if (_mediaPlaybackService is IMediaSessionService sessionService)
                    {
                        await sessionService.InitializeAsync(playbackParams);
                    }
                    await _playbackStatisticsService.InitializeAsync(MediaPlayerElement.MediaPlayer);
                    await _mediaControllerService.InitializeAsync(MediaPlayerElement.MediaPlayer);
                    await _mediaControlService.InitializeAsync(MediaPlayerElement.MediaPlayer);

                    // Subscribe to service events
                    _subtitleService.SubtitleChanged += OnSubtitleChanged;
                    _mediaNavigationService.NavigationStateChanged += OnNavigationStateChanged;
                    _playbackStatisticsService.StatsUpdated += OnStatsUpdated;
                    _mediaControllerService.ActionTriggered += OnControllerActionTriggered;
                    _mediaControllerService.ActionWithParameterTriggered += OnControllerActionWithParameterTriggered;
                    _skipSegmentService.SegmentAvailabilityChanged += OnSegmentAvailabilityChanged;
                    _skipSegmentService.SegmentSkipped += OnSegmentSkipped;

                    // Subscribe to MediaPlayer events
                    if (MediaPlayerElement?.MediaPlayer != null)
                    {
                        MediaPlayerElement.MediaPlayer.MediaOpened += OnMediaOpened;
                        MediaPlayerElement.MediaPlayer.MediaFailed += OnMediaFailed;
                        MediaPlayerElement.MediaPlayer.SeekCompleted += OnSeekCompleted;
                        MediaPlayerElement.MediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
                    }

                    // Load media item details
                    CurrentItem = playbackParams.Item;
                    NavigationSourceParameter = playbackParams.NavigationSourceParameter;
                    _currentPlaybackParams = playbackParams;

                    // If we don't have the item but have the ItemId, load it
                    if (CurrentItem == null && !string.IsNullOrEmpty(playbackParams.ItemId))
                    {
                        Logger.LogInformation($"Loading item details for ID: {playbackParams.ItemId}");
                        var mediaPlaybackService = GetService<IMediaPlaybackService>();
                        if (mediaPlaybackService != null)
                        {
                            CurrentItem =
                                await mediaPlaybackService.GetItemAsync(playbackParams.ItemId, CancellationToken.None);
                            playbackParams.Item = CurrentItem; // Update the params with the loaded item

                            // Update MediaSessionService with the loaded item
                            if (_mediaPlaybackService is IMediaSessionService mediaSessionService)
                            {
                                mediaSessionService.UpdateCurrentItem(CurrentItem);
                            }
                        }
                    }

                    // Initialize navigation service after setting CurrentItem
                    if (CurrentItem != null)
                    {
                        await _mediaNavigationService.InitializeAsync(playbackParams, CurrentItem);

                        // Initialize skip segment service now that we have CurrentItem
                        await _skipSegmentService.InitializeAsync(MediaPlayerElement.MediaPlayer, CurrentItem);
                    }
                    else
                    {
                        Logger.LogWarning("Cannot initialize navigation service without a valid item");
                    }

                    // Set up playback only if we have a valid item
                    if (CurrentItem != null)
                    {
                        await SetupPlaybackAsync();
                    }
                    else
                    {
                        Logger.LogError("Cannot setup playback without a valid item");
                        ErrorMessage = "Failed to load media item";
                        IsError = true;
                    }

                    // Initialize cancellation token for progress reporting
                    _progressReportCancellationTokenSource?.Cancel();
                    _progressReportCancellationTokenSource?.Dispose();
                    _progressReportCancellationTokenSource = new CancellationTokenSource();

                    // Start timers
                    _positionTimer.Start();
                    _controlVisibilityTimer.Start();
                }
                finally
                {
                    // Always release the semaphore
                    _initializationSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error initializing MediaPlayerViewModel");
                ErrorMessage = "Failed to initialize media player";
                IsError = true;
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task InitializeAsync(AudioPlaybackParams playbackParams)
        {
            try
            {
                IsLoading = true;
                // Convert AudioPlaybackParams to MediaPlaybackParams for internal use
                _playbackParams = new MediaPlaybackParams
                {
                    Item = playbackParams.Item,
                    ItemId = playbackParams.ItemId,
                    MediaSourceId = playbackParams.MediaSourceId,
                    StartPositionTicks = playbackParams.StartPositionTicks,
                    QueueItems = playbackParams.QueueItems,
                    StartIndex = playbackParams.StartIndex,
                    IsShuffled = playbackParams.IsShuffled,
                    NavigationSourcePage = playbackParams.NavigationSourcePage,
                    NavigationSourceParameter = playbackParams.NavigationSourceParameter
                };

                // Call the main initialization
                await InitializeAsync(_playbackParams);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to initialize audio playback");
                throw;
            }
        }


        private async Task SetupPlaybackAsync()
        {
            // Reset playback state flags
            _hasVideoStarted = false;
            IsIntroSkipAvailable = false;
            IsOutroSkipAvailable = false;

            try
            {
                // Get playback info
                var playbackInfo = await _playbackControlService.GetPlaybackInfoAsync(CurrentItem);
                if (playbackInfo == null)
                {
                    throw new InvalidOperationException("Failed to get playback info - server returned null");
                }

                _playSessionId = playbackInfo.PlaySessionId;

                // Quality selection not implemented in this version

                // Load subtitle tracks
                var subtitles = await _subtitleService.GetSubtitleTracksAsync(playbackInfo);
                SubtitleTracks.Clear();
                foreach (var subtitle in subtitles)
                {
                    SubtitleTracks.Add(subtitle);
                }

                // Notify UI about subtitle track availability
                OnPropertyChanged(nameof(HasSubtitleTracks));

                // Load audio tracks
                var audioTracks = await _playbackControlService.GetAudioTracksAsync(playbackInfo);
                AudioTracks.Clear();
                foreach (var audio in audioTracks)
                {
                    AudioTracks.Add(audio);
                }

                // Log current state for debugging
                Logger.LogInformation($"Audio tracks from playback info: {AudioTracks.Count}");
                Logger.LogInformation($"CurrentItem has MediaStreams: {CurrentItem?.MediaStreams != null}");
                if (CurrentItem?.MediaStreams != null)
                {
                    var audioStreamCount = CurrentItem.MediaStreams.Count(s => s.Type == MediaStream_Type.Audio);
                    Logger.LogInformation($"Audio streams in CurrentItem.MediaStreams: {audioStreamCount}");
                }

                // If we only got one audio track from playback info but the item has more,
                // load all audio tracks from the item's MediaStreams
                if (AudioTracks.Count <= 1 && CurrentItem?.MediaStreams?.Any(s => s.Type == MediaStream_Type.Audio) == true)
                {
                    var itemAudioStreams = CurrentItem?.MediaStreams?.Where(s => s.Type == MediaStream_Type.Audio).ToList() ?? new List<MediaStream>();
                    if (itemAudioStreams.Count > 1)
                    {
                        Logger.LogInformation($"Found {itemAudioStreams.Count} audio tracks in item MediaStreams, loading all tracks");
                        AudioTracks.Clear();

                        foreach (var stream in itemAudioStreams)
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

                            string displayCodec;
                            switch (codec.ToLower())
                            {
                                case "ac3":
                                    displayCodec = "Dolby Digital";
                                    break;
                                case "eac3":
                                    displayCodec = "Dolby Digital+";
                                    break;
                                case "truehd":
                                    displayCodec = "Dolby TrueHD";
                                    break;
                                case "dts":
                                    displayCodec = "DTS";
                                    break;
                                default:
                                    displayCodec = codec.ToUpper();
                                    break;
                            }

                            var audioTrack = new AudioTrack
                            {
                                ServerStreamIndex = stream.Index ?? 0,
                                Language = language,
                                DisplayName = $"{language} - {displayCodec} {channelLayout}",
                                IsDefault = stream.IsDefault ?? false
                            };

                            AudioTracks.Add(audioTrack);
                        }

                        // Set selected audio track
                        if (_playbackParams?.AudioStreamIndex.HasValue == true)
                        {
                            SelectedAudioTrack = AudioTracks.FirstOrDefault(a => a.ServerStreamIndex == _playbackParams.AudioStreamIndex.Value)
                                               ?? AudioTracks.FirstOrDefault(a => a.IsDefault)
                                               ?? AudioTracks.FirstOrDefault();
                        }
                        else
                        {
                            SelectedAudioTrack = AudioTracks.FirstOrDefault(a => a.IsDefault)
                                               ?? AudioTracks.FirstOrDefault();
                        }
                    }
                }

                // Notify UI about audio track availability
                OnPropertyChanged(nameof(HasMultipleAudioTracks));

                // Create media source and start playback
                var mediaSource = await _playbackControlService.CreateMediaSourceAsync(playbackInfo);
                if (mediaSource == null)
                {
                    throw new InvalidOperationException("Failed to create media source");
                }

                // Pass the selected media source info to the statistics service
                var currentMediaSource = _playbackControlService.GetCurrentMediaSource();
                if (currentMediaSource != null)
                {
                    _playbackStatisticsService.SetMediaSourceInfo(currentMediaSource);
                }

                await _playbackControlService.StartPlaybackAsync(mediaSource, _playbackParams.StartPositionTicks);

                // Report playback start position
                // For HLS streams, always report 0 to avoid server restart issues
                var isHlsStream = playbackInfo.MediaSources?.FirstOrDefault()?.TranscodingUrl?.Contains(".m3u8") == true;
                _sessionState.IsHlsStream = isHlsStream; // Store for later use

                // For HLS streams with resume, we'll do a client-side seek after playback starts
                // We don't use manifest offset tracking for initial resume since the server
                // starts the manifest from the beginning, not from the resume position
                if (isHlsStream && _playbackParams.StartPositionTicks > 0)
                {
                    Logger.LogInformation($"[HLS] Will apply client-side resume to {TimeSpan.FromTicks(_playbackParams.StartPositionTicks.Value):mm\\:ss}");
                    // Don't set up offset tracking - this is a normal client-side seek
                }

                var reportPosition = isHlsStream ? 0 : (_playbackParams.StartPositionTicks ?? 0);

                if (_mediaPlaybackService is IMediaSessionService sessionService)
                {
                    await sessionService.ReportPlaybackStartAsync(_playSessionId, reportPosition);
                    _hasReportedPlaybackStart = true;
                }

                // Preload next episode if applicable
                if (CurrentItem.Type == BaseItemDto_Type.Episode)
                {
                    FireAndForget(async () => await PreloadNextEpisodeAsync());
                }

            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error setting up playback");
                throw new InvalidOperationException("Failed to setup playback", ex);
            }
        }


        private async Task PreloadNextEpisodeAsync()
        {
            var context = CreateErrorContext("PreloadNextEpisode", ErrorCategory.Media, ErrorSeverity.Warning);
            try
            {
                await Task.Delay(MediaPlayerConstants.NEXT_EPISODE_PRELOAD_DELAY_MS);
                await _mediaNavigationService.PreloadNextItemAsync();
                NextEpisode = await _mediaNavigationService.GetNextEpisodeAsync();
                IsNextEpisodeAvailable = _mediaNavigationService.HasNextItem();
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        public async Task StopPlayback()
        {
            var context = CreateErrorContext("StopPlayback", ErrorCategory.Media);
            try
            {
                _mediaControlService.Stop();
                _hasVideoStarted = false;
                ResetHlsState();
            }
            catch (Exception ex)
            {
                if (ErrorHandler != null)
                {
                    context.Source = context.Source ?? GetType().Name;
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
                else
                {
                    Logger?.LogError(ex, $"Error in {GetType().Name}.{context?.Operation}");
                    ErrorMessage = ex.Message;
                    IsError = true;
                }
            }
        }

        private async Task PlayMediaItemAsync(BaseItemDto item, long startPositionTicks)
        {
            try
            {
                if (item == null)
                {
                    Logger.LogError("Cannot play null item");
                    ErrorMessage = "Invalid media item";
                    IsError = true;
                    return;
                }

                // Stop current playback
                try
                {
                    _mediaControlService.Stop();
                }
                catch (Exception stopEx)
                {
                    Logger.LogWarning(stopEx, "Error stopping current playback - continuing anyway");
                }

                // Create new playback params
                var newParams = new MediaPlaybackParams
                {
                    Item = item,
                    ItemId = item.Id?.ToString(),
                    StartPositionTicks = startPositionTicks,
                    MediaSourceId = _playbackParams?.MediaSourceId,
                    AudioStreamIndex = _playbackParams?.AudioStreamIndex,
                    SubtitleStreamIndex = _playbackParams?.SubtitleStreamIndex,
                    NavigationSourcePage = _playbackParams?.NavigationSourcePage,
                    NavigationSourceParameter = _playbackParams?.NavigationSourceParameter
                };

                // Reinitialize with new item
                await InitializeAsync(newParams);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error playing media item");
                ErrorMessage = $"Failed to play media: {ex.Message}";
                IsError = true;

                // Offer recovery options
                if (_navigationService.CanGoBack)
                {
                    Logger.LogInformation("Navigating back after playback failure");
                    await Task.Delay(2000); // Give user time to see error
                    _navigationService.GoBack();
                }
            }
        }

        // Timer event handlers
        private async void OnPositionTimerTick(object sender, object e)
        {
            // Check if we're disposed
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

                    TimeSpan currentPosition = TimeSpan.Zero;
                    TimeSpan duration = TimeSpan.Zero;

                    // Safely get position
                    try
                    {
                        currentPosition = session.Position;
                    }
                    catch (Exception posEx)
                    {
                        Logger.LogError($"[POSITION-TIMER] Failed to get Position - HResult: 0x{posEx.HResult:X8}");
                        return;
                    }

                    // Prefer metadata duration over NaturalDuration for HLS streams
                    var metadataDuration = GetMetadataDuration();

                    // Safely get natural duration
                    try
                    {
                        duration = metadataDuration > TimeSpan.Zero ? metadataDuration : session.NaturalDuration;
                    }
                    catch (Exception durEx)
                    {
                        Logger.LogError($"[POSITION-TIMER] Failed to get NaturalDuration - HResult: 0x{durEx.HResult:X8}");
                        duration = metadataDuration; // Fall back to metadata
                    }

                    // Guard against invalid duration during buffering or state changes
                    if (duration == TimeSpan.Zero || duration < TimeSpan.FromSeconds(1))
                    {
                        // Don't update duration if it's invalid
                        Logger.LogDebug($"[POSITION-TIMER] Skipping position update - invalid duration: {duration:mm\\:ss}, position: {currentPosition:mm\\:ss}");
                        return;
                    }

                    // Log if we detect HLS corruption (duration becomes unreasonably short)
                    if (_sessionState.IsHlsStream && metadataDuration > TimeSpan.FromMinutes(5) && duration < TimeSpan.FromMinutes(1))
                    {
                        Logger.LogError($"[HLS-CORRUPTION] Duration corruption detected! Natural: {duration:mm\\:ss}, Metadata: {metadataDuration:mm\\:ss}, Position: {currentPosition:mm\\:ss}");
                    }

                    TryApplyHlsManifestChangeAfterBackwardSeek(currentPosition);

                    // Update internal position so the Position property getter can add HLS offset
                    _position = currentPosition;
                    OnPropertyChanged(nameof(Position)); // Notify UI of position change
                    Duration = duration;

                    // Always update progress bar and time displays (use Position property for actual position)
                    UpdateCustomProgressBar(Position, duration);

                    // Update playback state with error handling
                    try
                    {
                        var state = session.PlaybackState;
                        IsPlaying = state == MediaPlaybackState.Playing;
                        IsPaused = state == MediaPlaybackState.Paused;
                        IsBuffering = state == MediaPlaybackState.Buffering;
                    }
                    catch (Exception stateEx)
                    {
                        Logger.LogError($"[POSITION-TIMER] Failed to get PlaybackState - HResult: 0x{stateEx.HResult:X8}");
                    }

                    // Resume position is applied when we transition to Playing state
                    // This ensures we only resume after playback has actually started

                    // Update buffering state from PlaybackState
                    // This replaces the BufferingManagementService

                    // Check for intro/outro skip availability with error handling
                    try
                    {
                        await _skipSegmentService.HandleAutoSkipAsync(Position);
                    }
                    catch (Exception skipEx)
                    {
                        Logger.LogDebug($"[POSITION-TIMER] Skip segment check failed: {skipEx.Message}");
                    }

                    // Check skip button visibility for all playing content
                    // Check if position has changed significantly to reduce frequency of skip button checks
                    var positionDelta = Math.Abs((Position - _lastSkipCheckPosition).TotalSeconds);

                    if (positionDelta >= SKIP_CHECK_THRESHOLD_SECONDS)
                    {
                        _lastSkipCheckPosition = Position;

                        // Update skip button visibility based on current position
                        await UpdateSkipButtonVisibilityAsync();
                    }

                    // Report progress periodically on background thread (non-blocking)
                    _ = Task.Run(async () => await ReportProgressIfNeeded(), _progressReportCancellationTokenSource?.Token ?? CancellationToken.None);

                    // Check for auto-play next - ensure this runs on UI thread since it updates UI properties
                    await RunOnUIThreadAsync(() =>
                    {
                        CheckForAutoPlayNext();
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in position timer tick");
            }
        }

        private void OnControlVisibilityTimerTick(object sender, object e)
        {
            // Control visibility logic will be handled by the view
        }

        private void OnBufferingTimeoutTimerTick(object sender, object e)
        {
            // Check if we're disposed
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
                Logger.LogError(ex, "Error in buffering timeout check");
            }
        }

        private bool TryHandleBufferingTimeout()
        {
            if (!_bufferingStartTime.HasValue || !IsBuffering)
            {
                return false;
            }

            var bufferingDuration = DateTime.UtcNow - _bufferingStartTime.Value;
            if (bufferingDuration.TotalSeconds < BUFFERING_TIMEOUT_SECONDS)
            {
                return false;
            }

            var actualPosition = GetCurrentPlaybackPosition();
            Logger.LogWarning($"[BUFFERING-TIMEOUT] Buffering timeout reached after {bufferingDuration.TotalSeconds:F1}s at position {actualPosition:mm\\:ss}, HLS: {_sessionState.IsHlsStream}");

            if (TryRecoverHlsBuffering())
            {
                return true;
            }

            HandleBufferingTimeoutFailure();
            return true;
        }

        private bool TryRecoverHlsBuffering()
        {
            if (!_sessionState.IsHlsStream)
            {
                return false;
            }

            Logger.LogWarning("[BUFFERING-TIMEOUT] HLS stream stuck - attempting recovery by toggling playback");

            if (_mediaControlService == null)
            {
                return false;
            }

            _mediaControlService.Pause();
            FireAndForget(async () =>
            {
                await Task.Delay(500);
                await RunOnUIThreadAsync(() =>
                {
                    if (_mediaControlService != null)
                    {
                        Logger.LogInformation("[BUFFERING-RECOVERY] Attempting to resume playback");
                        _mediaControlService.Play();
                    }
                });
            });

            // Give recovery attempt 10 more seconds
            _bufferingStartTime = DateTime.UtcNow.AddSeconds(-20); // Reset to 20s ago so we get 10 more seconds
            Logger.LogInformation("[BUFFERING-RECOVERY] Recovery attempted, extending timeout by 10 seconds");
            return true;
        }

        private void HandleBufferingTimeoutFailure()
        {
            ResetBufferingTimeout();
            Logger.LogError("[BUFFERING-TIMEOUT] Unable to recover, giving up");

            ErrorMessage = "Playback is taking too long. Please check your network connection.";
            IsError = true;

            FireAndForget(async () =>
            {
                await Task.Delay(3000);

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    IsError = false;
                    ErrorMessage = string.Empty;

                    if (_navigationService?.CanGoBack == true)
                    {
                        _navigationService.GoBack();
                        Logger.LogInformation("[BUFFERING-TIMEOUT] Navigated back after buffering timeout");
                    }
                }, logger: Logger);
            });
        }

        private void ResetBufferingTimeout()
        {
            _bufferingTimeoutTimer?.Stop();
            _bufferingStartTime = null;
        }

        private void OnStatsUpdateTimerTick(object sender, object e)
        {
            // Check if we're disposed
            if (_isDisposed)
            {
                Logger.LogDebug("[STATS-TIMER] Timer fired after disposal, ignoring");
                return;
            }

            try
            {
                if (IsStatsOverlayVisible)
                {
                    // Generate stats text directly
                    if (MediaPlayerElement?.MediaPlayer?.PlaybackSession != null)
                    {
                        var session = MediaPlayerElement.MediaPlayer.PlaybackSession;
                        var stats = new System.Text.StringBuilder();

                        // Safely get playback state
                        try
                        {
                            stats.AppendLine($"State: {session.PlaybackState}");
                        }
                        catch (Exception stateEx)
                        {
                            stats.AppendLine($"State: Error (0x{stateEx.HResult:X8})");
                        }

                        // Safely get position
                        try
                        {
                            var statsPosition = GetCurrentPlaybackPosition();
                            stats.AppendLine($"Position: {statsPosition:mm\\:ss} / {session.NaturalDuration:mm\\:ss}");
                        }
                        catch (Exception posEx)
                        {
                            stats.AppendLine($"Position: Error (0x{posEx.HResult:X8})");
                        }

                        // Buffer information (may not be available for all stream types)
                        try
                        {
                            stats.AppendLine($"Buffer: {session.BufferingProgress:P0}");
                        }
                        catch
                        {
                            stats.AppendLine("Buffer: N/A");
                        }

                        StatsText = stats.ToString();
                    }
                    else
                    {
                        StatsText = "No playback data available";
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[STATS-TIMER] Error updating stats");
                if (ex.HResult != 0)
                {
                    Logger.LogError($"[STATS-TIMER] HResult: 0x{ex.HResult:X8}");
                }
                StatsText = "Stats update error";
            }
        }

        // Service event handlers

        private async void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            await _playbackStateOrchestrator.HandlePlaybackStateChangedAsync(new PlaybackStateChangeContext
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
                GetIsBuffering = () => IsBuffering,
                SetIsBuffering = value => IsBuffering = value,
                GetBufferingStartTime = () => _bufferingStartTime,
                SetBufferingStartTime = value => _bufferingStartTime = value,
                GetHasVideoStarted = () => _hasVideoStarted,
                SetHasVideoStarted = value => _hasVideoStarted = value,
                HandleResumeOnPlaybackStartAsync = HandleResumeOnPlaybackStartAsync
            });
        }


        private void OnSubtitleChanged(object sender, SubtitleTrack subtitle)
        {
            SelectedSubtitle = subtitle;
        }

        private void OnNavigationStateChanged(object sender, EventArgs e)
        {
            IsNextEpisodeAvailable = _mediaNavigationService.HasNextItem();
        }

        partial void OnCurrentItemChanged(BaseItemDto value)
        {
        }

        private void OnStatsUpdated(object sender, PlaybackStats stats)
        {
            CurrentStats = stats;
        }

        private async Task HandleResumeOnPlaybackStartAsync()
        {
            if (_resumeAttemptInProgress)
            {
                Logger.LogDebug("Resume attempt already in progress, skipping duplicate call");
                return;
            }

            _resumeAttemptInProgress = true;
            try
            {
                var outcome = await _resumeFlowCoordinator.HandleResumeOnPlaybackStartAsync(new ResumeFlowContext
                {
                    SessionState = _sessionState,
                    PlaybackParams = _playbackParams,
                    GetCurrentPosition = () => GetCurrentPlaybackPosition(),
                    OnHlsResumeFixCompleted = CompleteHlsResumeFix,
                    OnResumeFailedAsync = HandleResumeFailureAsync
                });

                if (outcome.Success)
                {
                    Logger.LogInformation("Applied resume position on playback start");
                }
            }
            finally
            {
                _resumeAttemptInProgress = false;
            }
        }

        private async Task HandleResumeFailureAsync(ResumeFailureContext failureContext)
        {
            if (ErrorHandler == null)
            {
                return;
            }

            _sessionState.HasPerformedInitialSeek = true;

            var context = CreateErrorContext("ResumePlayback", ErrorCategory.Media, ErrorSeverity.Error);
            var resumeException = new ResumeStuckException(failureContext.CurrentPosition, failureContext.TargetPosition, failureContext.RetryCount);

            ErrorHandler?.HandleError(resumeException, context, showUserMessage: true);

            await Task.Delay(100);

            if (_navigationService?.CanGoBack == true)
            {
                _navigationService.GoBack();
            }
        }

        public void SetControlsVisible(bool visible)
        {
            _mediaControllerService?.SetControlsVisible(visible);
        }

        public void ClearSkipFlag()
        {
            LastActionWasSkip = false;
        }

        private async void OnControllerActionWithParameterTriggered(object sender,
            (MediaAction action, object parameter) args)
        {
            try
            {
                Logger.LogInformation($"Controller action with parameter: {args.action}, parameter: {args.parameter}");

                switch (args.action)
                {
                    case MediaAction.FastForward:
                        // Trigger - skip forward by parameter seconds (should be 600 for 10 minutes)
                        if (args.parameter is int forwardSeconds)
                        {
                            await SkipForward(forwardSeconds);
                        }
                        break;
                    case MediaAction.Rewind:
                        // Trigger - skip backward by parameter seconds (should be 600 for 10 minutes)
                        if (args.parameter is int backwardSeconds)
                        {
                            await SkipBackward(backwardSeconds);
                        }
                        break;
                    default:
                        // For other actions with parameters, just call the regular action
                        OnControllerActionTriggered(sender, args.action);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error handling controller action with parameter: {args.action}");
            }
        }

        private async void OnControllerActionTriggered(object sender, MediaAction action)
        {
            try
            {
                Logger.LogInformation($"OnControllerActionTriggered called with action: {action}");
                switch (action)
                {
                    case MediaAction.PlayPause:
                        await PlayPause();
                        break;
                    case MediaAction.Stop:
                        await Stop();
                        break;
                    case MediaAction.Next:
                        await PlayNext();
                        break;
                    case MediaAction.Previous:
                        await PlayPrevious();
                        break;
                    case MediaAction.FastForward:
                        // D-pad right - 30 second skip
                        await SkipForward(null);
                        break;
                    case MediaAction.Rewind:
                        // D-pad left - 10 second skip
                        await SkipBackward(null);
                        break;
                    case MediaAction.SkipIntro:
                        await SkipIntro();
                        break;
                    case MediaAction.SkipOutro:
                        await SkipOutro();
                        break;
                    case MediaAction.ShowStats:
                        ToggleStats();
                        break;
                    case MediaAction.NavigateBack:
                        // Navigate back to the previous page
                        // Use smart navigation if we have a navigation source page
                        if (_currentPlaybackParams?.NavigationSourcePage != null)
                        {
                            Logger.LogInformation($"Smart back navigation: Going to {_currentPlaybackParams.NavigationSourcePage.Name}");
                            _navigationService.Navigate(_currentPlaybackParams.NavigationSourcePage, _currentPlaybackParams.NavigationSourceParameter);
                        }
                        else if (_navigationService.CanGoBack)
                        {
                            _navigationService.GoBack();
                        }

                        break;
                    case MediaAction.ShowInfo:
                        // Toggle controls (Y, Up, Down buttons)
                        // Fire event to request control visibility toggle
                        ToggleControlsRequested?.Invoke(this, EventArgs.Empty);
                        break;
                        // Note: VolumeUp, VolumeDown, Mute actions are handled by Xbox system
                        // Audio/subtitle selection is done through UI flyouts
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error handling controller action: {action}");
            }
        }

        private async void OnSegmentAvailabilityChanged(object sender, EventArgs e)
        {
            // Update skip button visibility based on current position
            // Don't wait for video frame as it may not fire for HLS
            await UpdateSkipButtonVisibilityAsync();
        }

        private void OnSegmentSkipped(object sender, SkipSegmentType segmentType)
        {
            Logger.LogInformation($"Skipped {segmentType} segment");
        }

        // NOTE: VideoFrameAvailable event removed - it requires IsVideoFrameServerEnabled=true
        // which is meant for frame processing (e.g., external subtitles, filters)
        // For detecting playback readiness, we use PlaybackSession state instead

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            if (_isDisposed) return;

            try
            {
                var sessionSnapshot = PlaybackSessionSnapshot.Capture(
                    sender.PlaybackSession,
                    skipBufferingProgress: _sessionState.IsHlsStream);
                Logger.LogInformation(
                    $"[MEDIA-OPENED] Natural duration: {sessionSnapshot.NaturalDuration.TotalSeconds}s, " +
                    $"CanSeek: {sessionSnapshot.CanSeek}, " +
                    $"IsProtected: {sessionSnapshot.IsProtected}");

                // Log memory after media opens
                var memoryUsage = Windows.System.MemoryManager.AppMemoryUsage / (1024.0 * 1024.0);
                var memoryLimit = Windows.System.MemoryManager.AppMemoryUsageLimit / (1024.0 * 1024.0);
                Logger.LogInformation($"[MEMORY] After media opened: {memoryUsage:F2} MB / {memoryLimit:F2} MB");

                // Pass the session to avoid cross-thread access to MediaPlayerElement
                var openPosition = GetCurrentPlaybackPosition(sender.PlaybackSession);
                Logger.LogInformation($"[MEDIA-OPENED] Current position: {openPosition.TotalSeconds}s");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[ERROR] Exception in OnMediaOpened handler");
            }
        }


        // Helper methods

        /// <summary>
        /// Updates skip button visibility based on current playback position
        /// </summary>
        private async Task UpdateSkipButtonVisibilityAsync()
        {
            if (_skipSegmentService == null)
            {
                return;
            }

            var currentSegment = _skipSegmentService.GetCurrentSegmentType(Position);

            // Track previous state for logging
            var wasIntroAvailable = IsIntroSkipAvailable;
            var wasOutroAvailable = IsOutroSkipAvailable;

            // Update UI properties on UI thread
            await RunOnUIThreadAsync(() =>
            {
                IsIntroSkipAvailable = currentSegment == SkipSegmentType.Intro;
                // Only show Skip Credits for movies, not episodes
                IsOutroSkipAvailable = currentSegment == SkipSegmentType.Outro &&
                                       CurrentItem?.Type == BaseItemDto_Type.Movie;
            });

            // Log when skip button visibility changes
            if (IsIntroSkipAvailable != wasIntroAvailable)
            {
                Logger.LogInformation(
                    $"Intro skip button visibility changed to: {IsIntroSkipAvailable} at position {Position:mm\\:ss}");
            }

            if (IsOutroSkipAvailable != wasOutroAvailable)
            {
                Logger.LogInformation(
                    $"Outro skip button visibility changed to: {IsOutroSkipAvailable} at position {Position:mm\\:ss}");
            }
        }

        private async void OnSeekCompleted(MediaPlayer sender, object args)
        {
            if (_isDisposed) return;

            try
            {
                await RunOnUIThreadAsync(() =>
                {
                    var position = sender?.PlaybackSession?.Position ?? TimeSpan.Zero;
                    var state = sender?.PlaybackSession?.PlaybackState;
                    var naturalDuration = sender?.PlaybackSession?.NaturalDuration;
                    var metadataDuration = GetMetadataDuration();

                    _seekCompletionCoordinator.HandleSeekCompleted(new SeekCompletionContext
                    {
                        Position = position,
                        PlaybackState = state,
                        NaturalDuration = naturalDuration,
                        MetadataDuration = metadataDuration,
                        IsHlsStream = _sessionState.IsHlsStream,
                        HasPerformedInitialSeek = _sessionState.HasPerformedInitialSeek,
                        PendingSeekCount = _sessionState.PendingSeekCount,
                        ActualResumePosition = _actualResumePosition,
                        DecrementPendingSeek = _sessionState.DecrementPendingSeek,
                        SetActualResumePosition = resumePosition => _actualResumePosition = resumePosition,
                        MarkInitialSeekPerformed = () => _sessionState.HasPerformedInitialSeek = true,
                        AttemptHlsRecovery = AttemptHlsRecoveryAfterSeek,
                        TryHandleHlsManifestChange = TryHandleHlsManifestChange
                    });
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[ERROR] Exception in OnSeekCompleted handler");
            }
        }

        private void AttemptHlsRecoveryAfterSeek()
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await RunOnUIThreadAsync(() =>
                {
                    if (_mediaControlService != null && !IsPlaying)
                    {
                        Logger.LogInformation("[HLS-RECOVERY] Auto-playing to recover from manifest issue");
                        _mediaControlService.Play();
                    }
                });
            });
        }

        private void TryHandleHlsManifestChange(TimeSpan position, TimeSpan naturalDuration, TimeSpan metadataDuration)
        {
            if (!_sessionState.IsHlsStream || naturalDuration >= metadataDuration || _sessionState.ExpectedHlsSeekTarget <= TimeSpan.Zero)
            {
                return;
            }

            var percentageOfOriginal = (naturalDuration.TotalSeconds / metadataDuration.TotalSeconds) * 100;
            Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Detected new HLS manifest after seek. Natural duration is {percentageOfOriginal:F1}% of metadata duration");
            Logger.LogInformation($"[HLS-MANIFEST-CHANGE] New manifest starts at {_sessionState.ExpectedHlsSeekTarget:mm\\:ss}, duration: {naturalDuration:mm\\:ss}");

            var timeSinceLastSeek = DateTime.UtcNow - _sessionState.LastSeekTime;
            var shouldProcessManifest = _sessionState.PendingSeekCount == 0 || timeSinceLastSeek.TotalSeconds > 2;

            if (!shouldProcessManifest)
            {
                Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Skipping manifest offset due to {_sessionState.PendingSeekCount} pending seeks");
                return;
            }

            Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Processing manifest change (pending seeks: {_sessionState.PendingSeekCount}, time since last seek: {timeSinceLastSeek.TotalSeconds:F1}s)");

            _sessionState.PendingSeekCount = 0;
            SetHlsManifestOffset(_sessionState.ExpectedHlsSeekTarget, false, "Setting up offset tracking.");

            Logger.LogInformation("[HLS-MANIFEST-CHANGE] Applying immediate seek to position 0 of new manifest");
            if (MediaPlayerElement?.MediaPlayer?.PlaybackSession != null)
            {
                MediaPlayerElement.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
                CompleteHlsResumeFix();
                Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Seeked to position 0, playback should continue from {_sessionState.HlsManifestOffset:mm\\:ss}");
            }
        }

        private void TryApplyHlsManifestChangeAfterBackwardSeek(TimeSpan currentPosition)
        {
            if (!_sessionState.IsHlsStream || _sessionState.ExpectedHlsSeekTarget == TimeSpan.Zero)
            {
                return;
            }

            var timeSinceSeek = (DateTime.UtcNow - _sessionState.LastSeekTime).TotalSeconds;
            if (timeSinceSeek >= 5 || _sessionState.PendingSeekCount <= 0)
            {
                return;
            }

            if (currentPosition.TotalSeconds >= 1)
            {
                return;
            }

            _sessionState.PendingSeekCount = Math.Max(0, _sessionState.PendingSeekCount - 1);
            Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Detected new manifest creation after backward seek (pending: {_sessionState.PendingSeekCount})");
            SetHlsManifestOffset(_sessionState.ExpectedHlsSeekTarget, true, "Detected new manifest creation after backward seek.");
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            if (_isDisposed) return;

            try
            {
                Logger.LogError($"[MEDIA-FAILED] Error: {args.Error}, " +
                    $"ExtendedError HResult: 0x{args.ExtendedErrorCode?.HResult:X8}, " +
                    $"Message: {args.ErrorMessage}");

                // Log memory at time of failure
                var memoryUsage = Windows.System.MemoryManager.AppMemoryUsage / (1024.0 * 1024.0);
                var memoryLimit = Windows.System.MemoryManager.AppMemoryUsageLimit / (1024.0 * 1024.0);
                Logger.LogError($"[MEMORY] At media failure: {memoryUsage:F2} MB / {memoryLimit:F2} MB");

                // Log common media error HRESULTs
                if (args.ExtendedErrorCode != null)
                {
                    var hresult = args.ExtendedErrorCode.HResult;
                    switch (hresult)
                    {
                        case -1072875854: // 0xC00D36B2 - MF_E_UNSUPPORTED_BYTESTREAM_TYPE
                            Logger.LogError("[MEDIA-FAILED] Unsupported media format or codec");
                            break;
                        case -1072875802: // 0xC00D36E6 - MF_E_INVALID_FORMAT
                            Logger.LogError("[MEDIA-FAILED] Invalid media format");
                            break;
                        case -2147024882: // 0x8007000E - E_OUTOFMEMORY
                            Logger.LogError("[MEDIA-FAILED] Out of memory");
                            break;
                        case -1072873821: // 0xC00D3EA3 - MF_E_TRANSFORM_TYPE_NOT_SET
                            Logger.LogError("[MEDIA-FAILED] Media transform type not set (codec issue)");
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[ERROR] Exception in OnMediaFailed handler");
            }
        }

        private async void OnSkipButtonBecameAvailable(SkipSegmentType segmentType)
        {
            Logger.LogInformation($"{segmentType} skip button became available");

            // Ensure event is raised on UI thread
            await RunOnUIThreadAsync(() =>
            {
                SkipButtonBecameAvailable?.Invoke(this, segmentType);
            });
        }

        private async Task ReportProgressIfNeeded()
        {
            try
            {
                // Don't report progress during seeks to prevent incorrect positions
                if (_sessionState.PendingSeekCount > 0)
                {
                    Logger.LogDebug($"Skipping progress report - seek in progress (PendingSeeks: {_sessionState.PendingSeekCount})");
                    return;
                }

                if (_hasReportedPlaybackStart && !string.IsNullOrEmpty(_playSessionId) && !IsPaused)
                {
                    if (_mediaPlaybackService is IMediaSessionService sessionService)
                    {
                        // Use the actual position including HLS manifest offset for progress reporting
                        var actualPosition = Position; // This property already includes the offset
                        var positionTicks = actualPosition.Ticks;
                        var positionSeconds = positionTicks / 10000000.0;
                        var metadataDuration = GetMetadataDuration();

                        // Log detailed position info periodically (every 10 reports)
                        _progressReportCounter++;
                        // Progress reporting happens frequently, only log in debug builds
#if DEBUG
                        if (_progressReportCounter % 50 == 0) // Log every 50 reports (~12.5 seconds)
                        {
                            Logger.LogDebug($"Progress Report #{_progressReportCounter}: Position={Position:mm\\:ss}, Percentage={(positionSeconds / metadataDuration.TotalSeconds * 100):F1}%");
                        }
#endif

                        await sessionService.ReportPlaybackProgressAsync(_playSessionId, positionTicks, IsPaused);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                Logger.LogDebug("Progress reporting cancelled");
            }
            catch (Exception ex)
            {
                Logger.LogDebug(ex, "Error reporting progress");
            }
        }

        private TimeSpan GetMetadataDuration()
        {
            // Get duration from item metadata which is more reliable than MediaPlayer's NaturalDuration for HLS streams
            if (CurrentItem?.RunTimeTicks != null && CurrentItem.RunTimeTicks > 0)
            {
                return TimeSpan.FromTicks(CurrentItem.RunTimeTicks.Value);
            }
            return TimeSpan.Zero;
        }

        private void CheckForAutoPlayNext()
        {
            // Use metadata duration instead of MediaPlayer's Duration which can be incorrect for HLS streams
            var metadataDuration = GetMetadataDuration();
            if (metadataDuration == TimeSpan.Zero)
            {
                // Fallback to MediaPlayer's Duration if metadata not available
                metadataDuration = Duration;
            }

            // Additional safeguard: Don't check for auto-play if duration seems incorrect
            // If MediaPlayer reports a duration that's significantly different from metadata, skip
            if (metadataDuration > TimeSpan.Zero && Duration > TimeSpan.Zero)
            {
                var durationDifference = Math.Abs((metadataDuration - Duration).TotalSeconds);
                if (durationDifference > 60) // More than 1 minute difference
                {
                    Logger.LogWarning($"Duration mismatch detected - Metadata: {metadataDuration:mm\\:ss}, MediaPlayer: {Duration:mm\\:ss}. Skipping auto-play check.");
                    return;
                }
            }

            if (!_hasAutoPlayedNext && metadataDuration > TimeSpan.Zero)
            {
                var percentComplete = Position.TotalSeconds / metadataDuration.TotalSeconds * 100;

                // Show next episode button overlay when near the end (for episodes only)
                // Since we don't show Skip Credits for episodes, we don't need to check IsOutroSkipAvailable
                var shouldShow = percentComplete >= 95 && NextEpisode != null;
                if (shouldShow != NextEpisodeButtonOverlayVisible)
                {
                    Logger.LogInformation($"[OVERLAY] NextEpisode overlay changing from {NextEpisodeButtonOverlayVisible} to {shouldShow} at {percentComplete:F2}% complete");
                    NextEpisodeButtonOverlayVisible = shouldShow;
                }

                // Only auto-play if the preference is enabled
                if (_autoPlayNextEpisode && percentComplete >= MediaPlayerConstants.AUTO_PLAY_NEXT_THRESHOLD_PERCENT && NextEpisode != null)
                {
                    _hasAutoPlayedNext = true;
                    Logger.LogWarning($"Auto-playing next episode at {percentComplete:F2}% (Position={Position:mm\\:ss}, MetadataDuration={metadataDuration:mm\\:ss})");
                    FireAndForget(async () => await PlayNextEpisode());
                }
            }
        }


        public async Task UpdatePositionImmediateAsync()
        {
            if (_isDisposed) return;

            try
            {
                if (MediaPlayerElement?.MediaPlayer?.PlaybackSession == null)
                {
                    return;
                }

                // Ensure UI updates happen on UI thread
                await RunOnUIThreadAsync(() =>
                {
                    var session = MediaPlayerElement.MediaPlayer.PlaybackSession;
                    // Update internal position so the Position property getter can add HLS offset
                    _position = session.Position;
                    OnPropertyChanged(nameof(Position));
                    Duration = session.NaturalDuration;
                    UpdateCustomProgressBar(Position, Duration);
                });
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error updating position immediately");
            }
        }

        private void ShowSkipIndicator(string text)
        {
            // In the future, this could trigger a visual overlay
        }


        private void UpdateCustomProgressBar(TimeSpan currentPosition, TimeSpan duration)
        {
            // Use metadata duration if available as it's more reliable for HLS streams
            var metadataDuration = GetMetadataDuration();
            if (metadataDuration > TimeSpan.Zero)
            {
                duration = metadataDuration;
            }

            // Update time displays
            CurrentTimeText = TimeFormattingHelper.FormatTime(currentPosition);
            DurationText = TimeFormattingHelper.FormatTime(duration);

            // Update progress bar
            if (duration > TimeSpan.Zero)
            {
                var percentage = currentPosition.TotalSeconds / duration.TotalSeconds * 100.0;
                percentage = Math.Max(0, Math.Min(100, percentage)); // Clamp between 0-100

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

            // Force property change notification for UI updates
            OnPropertyChanged(nameof(Position));
            OnPropertyChanged(nameof(CurrentTimeText));
            OnPropertyChanged(nameof(DurationText));
            OnPropertyChanged(nameof(PositionPercentage));

            // Update end time
            UpdateEndTime();
        }

        private void UpdateEndTime()
        {
            try
            {
                if (MediaPlayerElement?.MediaPlayer?.PlaybackSession != null)
                {
                    var session = MediaPlayerElement.MediaPlayer.PlaybackSession;
                    var duration = session.NaturalDuration;

                    TimeSpan remainingTime;
                    if (_playbackControlService?.HlsManifestOffset > TimeSpan.Zero)
                    {
                        var fullMediaDuration = GetMetadataDuration();
                        var actualPosition = GetCurrentPlaybackPosition();
                        remainingTime = fullMediaDuration - actualPosition;
                    }
                    else
                    {
                        var position = session.Position;
                        remainingTime = duration - position;
                    }

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
                Logger?.LogWarning(ex, "Error updating end time");
                IsEndsAtTimeVisible = false;
            }
        }

        public IMediaNavigationService GetMediaNavigationService()
        {
            return _mediaNavigationService;
        }

        public IMediaControllerService GetMediaControllerService()
        {
            return _mediaControllerService;
        }

        // Stop all timers immediately - called when navigating away
        public void StopTimers()
        {
            try
            {
                // Cancel background progress reporting tasks
                _progressReportCancellationTokenSource?.Cancel();

                _positionTimer?.Stop();
                _controlVisibilityTimer?.Stop();
                _statsUpdateTimer?.Stop();
                _bufferingTimeoutTimer?.Stop();
                _bufferingStartTime = null;
                Logger.LogInformation("All timers stopped and background tasks cancelled");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to stop timers");
            }
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
        }

        private void RestartTimers()
        {
            if (_isDisposed)
            {
                return;
            }

            _progressReportCancellationTokenSource?.Dispose();
            _progressReportCancellationTokenSource = new CancellationTokenSource();

            _positionTimer?.Start();
            _controlVisibilityTimer?.Start();
            _statsUpdateTimer?.Start();

            if (IsBuffering)
            {
                _bufferingTimeoutTimer?.Start();
            }
            else
            {
                _bufferingTimeoutTimer?.Stop();
            }
        }

        // Report playback stopped - critical operation that must complete
        public async Task ReportPlaybackStoppedAsync()
        {
            // Note: Timer stopping moved to StopTimers() which is called earlier

            // Report playback stopped if we have a session
            if (_hasReportedPlaybackStart && !string.IsNullOrEmpty(_playSessionId))
            {
                try
                {
                    var position = Position.Ticks;
                    Logger.LogInformation($"Reporting playback stopped at position: {TimeSpan.FromTicks(position)}");
                    if (_mediaPlaybackService is IMediaSessionService sessionService)
                    {
                        await sessionService.ReportPlaybackStoppedAsync(_playSessionId, position);
                        Logger.LogInformation("Playback stopped reported successfully");
                    }
                    _hasReportedPlaybackStart = false; // Prevent duplicate reports
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to report playback stopped");
                }
            }
        }

        #region HLS Handling Helpers

        /// <summary>
        /// Gets the current playback position, automatically including HLS offset when appropriate
        /// </summary>
        private TimeSpan GetCurrentPlaybackPosition(MediaPlaybackSession session = null)
        {
            TimeSpan rawPosition = TimeSpan.Zero;

            try
            {
                // Use provided session first (safe from any thread)
                if (session != null)
                {
                    rawPosition = session.Position;
                }
                // Only access MediaPlayerElement if we're on UI thread
                else if (Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.HasThreadAccess)
                {
                    rawPosition = MediaPlayerElement?.MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
                }
                else
                {
                    // If called from background thread without session, use cached position
                    rawPosition = _position;
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug($"[GetCurrentPlaybackPosition] Error getting position: {ex.Message}");
                rawPosition = _position; // Fall back to cached position
            }

            // Include HLS offset from PlaybackControlService if available
            if (_playbackControlService?.HlsManifestOffset > TimeSpan.Zero)
            {
                return rawPosition + _playbackControlService.HlsManifestOffset;
            }

            return rawPosition;
        }

        private void SetHlsManifestOffset(TimeSpan offset, bool markApplied, string context)
        {
            _sessionState.HlsManifestOffset = offset;
            _sessionState.HlsManifestOffsetApplied = markApplied;
            _sessionState.ExpectedHlsSeekTarget = TimeSpan.Zero;

            Logger.LogInformation($"[HLS-MANIFEST-CHANGE] {context} Position 0 in new manifest = {offset:mm\\:ss}");
        }

        /// <summary>
        /// Prepares track change resume for HLS streams
        /// </summary>
        private void PrepareHlsTrackChange()
        {
            if (!_sessionState.IsHlsStream)
            {
                return;
            }

            var currentPosition = Position; // Includes offset if present
            _sessionState.PendingSeekPositionAfterQualitySwitch = currentPosition.Ticks;
            _sessionState.HlsManifestOffset = TimeSpan.Zero;
            _sessionState.HlsManifestOffsetApplied = false;
            _sessionState.IsHlsTrackChange = false;

            Logger?.LogInformation($"[HLS] Track change will resume to {currentPosition:hh\\:mm\\:ss}");
        }

        /// <summary>
        /// Handles the HLS buffering fix by seeking to position 0 of the new manifest
        /// </summary>
        private void HandleHlsBufferingFix(MediaPlaybackSession session = null)
        {
            if (!ShouldApplyHlsResumeFix(session))
            {
                return;
            }

            Logger.LogInformation("[HLS-RESUME] Buffering at resume position, will try seeking to manifest start");

            FireAndForget(async () =>
            {
                await Task.Delay(500); // Short delay to allow manifest to load but minimize audio buffering

                if (!IsBuffering)
                {
                    return;
                }

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    ApplyHlsManifestOffsetSeek(MediaPlayerElement?.MediaPlayer?.PlaybackSession);
                }, logger: Logger);
            });
        }

        /// <summary>
        /// Checks if we should apply the HLS resume fix
        /// </summary>
        private bool ShouldApplyHlsResumeFix(MediaPlaybackSession session = null)
        {
            // Only apply fix if we have a manifest offset (from track change or large seek)
            // Initial resume doesn't use manifest offset - it's just a client-side seek
            if (!_sessionState.IsHlsStream || _sessionState.HlsManifestOffset <= TimeSpan.Zero)
            {
                return false;
            }

            // Apply fix for track changes or large seeks that create new manifests
            var shouldApply = _sessionState.IsHlsTrackChange ||
                (_sessionState.HlsManifestOffset > TimeSpan.Zero && !_sessionState.HlsManifestOffsetApplied);
            if (!shouldApply)
            {
                return false;
            }

            var rawPosition = session?.Position ?? TimeSpan.Zero;
            var diff = Math.Abs((rawPosition - _sessionState.HlsManifestOffset).TotalSeconds);
            return diff < 10;
        }

        private void ApplyHlsManifestOffsetSeek(MediaPlaybackSession session)
        {
            if (session == null)
            {
                return;
            }

            var currentState = session.PlaybackState;
            Logger.LogInformation($"[HLS-RESUME] Applying fix - current state: {currentState}, position before: {session.Position:mm\\:ss}");

            // Pause briefly to ensure clean audio buffer transition
            var wasPlaying = currentState == MediaPlaybackState.Playing || currentState == MediaPlaybackState.Buffering;
            if (wasPlaying)
            {
                Logger.LogInformation("[HLS-RESUME] Pausing playback for clean seek");
                session.MediaPlayer.Pause();
            }

            session.Position = TimeSpan.Zero;
            Logger.LogInformation($"[HLS-RESUME] Seeked to position 0, new position: {session.Position:mm\\:ss}");
            CompleteHlsResumeFix();

            if (wasPlaying)
            {
                Logger.LogInformation("[HLS-RESUME] Resuming playback");
                session.MediaPlayer.Play();
            }
        }

        /// <summary>
        /// Completes the HLS resume fix by updating flags
        /// </summary>
        private void CompleteHlsResumeFix()
        {
            _sessionState.HlsManifestOffsetApplied = true;
            _sessionState.IsHlsTrackChange = false;
            OnPropertyChanged(nameof(Position)); // UI needs to update
        }

        /// <summary>
        /// Resets all HLS-related state
        /// </summary>
        private void ResetHlsState()
        {
            _sessionState.Reset();
            _actualResumePosition = TimeSpan.Zero;
        }

        #endregion

        // Cleanup remaining resources after stop is reported
        public async Task CleanupRemainingAsync()
        {
            Logger.LogInformation("CleanupRemainingAsync called");

            // Store the cleanup task so InitializeAsync can wait for it if needed
            _cleanupTask = CleanupRemainingAsyncInternal();
            await _cleanupTask;
        }

        private async Task CleanupRemainingAsyncInternal()
        {
            Logger.LogInformation("CleanupRemainingAsyncInternal started");

            // Note: Timers and media player already stopped synchronously in OnNavigatedFrom
            // This method now handles only async cleanup that doesn't block navigation

            // Don't dispose singleton services - they need to maintain state across episode transitions
            // Singletons: MediaControlService, PlaybackControlService, SubtitleService, MediaNavigationService,
            //            PlaybackStatisticsService, SkipSegmentService
            // These will be re-initialized with the new MediaPlayer in the next episode's InitializeAsync call.

            // Stop statistics updates (but don't dispose the service)
            try
            {
                _playbackStatisticsService?.StopUpdating();
                Logger.LogInformation("PlaybackStatisticsService updates stopped");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to stop statistics updates during cleanup");
            }

            // Note: MediaPlayer event cleanup skipped here to avoid cross-thread access
            // MediaPlayerElement can only be accessed from UI thread
            // Event handlers will be cleaned up when MediaPlayerPage disposes the MediaPlayer
            Logger.LogInformation("MediaPlayer event cleanup deferred to UI thread disposal");

            await Task.CompletedTask;
            Logger.LogInformation("CleanupRemainingAsyncInternal completed");
        }

        protected override void DisposeManaged()
        {
            // Mark as disposed first to prevent any new operations
            _isDisposed = true;

            // Cancel and dispose cancellation token source
            _progressReportCancellationTokenSource?.Cancel();
            _progressReportCancellationTokenSource?.Dispose();

            // Stop and unsubscribe from timer events to prevent post-disposal firing
            if (_positionTimer != null)
            {
                _positionTimer.Stop();
                _positionTimer.Tick -= OnPositionTimerTick;
            }

            if (_controlVisibilityTimer != null)
            {
                _controlVisibilityTimer.Stop();
                _controlVisibilityTimer.Tick -= OnControlVisibilityTimerTick;
            }

            if (_statsUpdateTimer != null)
            {
                _statsUpdateTimer.Stop();
                _statsUpdateTimer.Tick -= OnStatsUpdateTimerTick;
            }

            if (_bufferingTimeoutTimer != null)
            {
                _bufferingTimeoutTimer.Stop();
                _bufferingTimeoutTimer.Tick -= OnBufferingTimeoutTimerTick;
            }

            // Unsubscribe from events


            if (_subtitleService != null)
            {
                _subtitleService.SubtitleChanged -= OnSubtitleChanged;
            }

            if (_mediaNavigationService != null)
            {
                _mediaNavigationService.NavigationStateChanged -= OnNavigationStateChanged;
            }

            if (_playbackStatisticsService != null)
            {
                _playbackStatisticsService.StatsUpdated -= OnStatsUpdated;
            }

            if (_mediaControllerService != null)
            {
                _mediaControllerService.ActionTriggered -= OnControllerActionTriggered;
                _mediaControllerService.ActionWithParameterTriggered -= OnControllerActionWithParameterTriggered;
            }

            if (_skipSegmentService != null)
            {
                _skipSegmentService.SegmentAvailabilityChanged -= OnSegmentAvailabilityChanged;
                _skipSegmentService.SegmentSkipped -= OnSegmentSkipped;
            }

            if (MediaPlayerElement?.MediaPlayer != null)
            {
                MediaPlayerElement.MediaPlayer.MediaOpened -= OnMediaOpened;
                MediaPlayerElement.MediaPlayer.MediaFailed -= OnMediaFailed;
                MediaPlayerElement.MediaPlayer.SeekCompleted -= OnSeekCompleted;
                MediaPlayerElement.MediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            }

            // Only dispose transient services (MediaControllerService is transient)
            // Don't dispose singleton services as they need to maintain state
            _mediaControllerService?.Dispose();

            base.DisposeManaged();
        }
    }
}
