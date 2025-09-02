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
        private bool _hasPerformedInitialSeek = false;
        private bool _hasReportedPlaybackStart = false;
        private int _progressReportCounter = 0;
        // HLS stream handling
        private bool _isCurrentStreamHls = false; // Track if current stream is HLS
        private TimeSpan _hlsManifestOffset = TimeSpan.Zero; // Track the offset for HLS manifests with large seeks
        private bool _hlsManifestOffsetApplied = false; // Track if we've seeked to position 0 of new manifest
        private TimeSpan _expectedHlsSeekTarget = TimeSpan.Zero; // Track expected position after large HLS seek
        private DateTime _lastSeekTime = DateTime.MinValue; // Track when last seek was initiated
        private volatile int _pendingSeekCount = 0; // Track number of pending seeks
        private TimeSpan _actualResumePosition = TimeSpan.Zero; // Track actual resume position for corruption detection

        // Track when video playback has started
        private bool _hasVideoStarted = false;

        // Track cleanup state to prevent race conditions
        private Task _cleanupTask;
        private readonly SemaphoreSlim _initializationSemaphore = new SemaphoreSlim(1, 1);

        // Track whether auto-play next episode is enabled
        private bool _autoPlayNextEpisode = false;

        // Track if we're in the process of resuming to prevent false MediaEnded events
        public bool IsApplyingResume { get; private set; }

        [ObservableProperty] private bool _isAudioVisualizationActive;

        [ObservableProperty] private bool _isBuffering;

        [ObservableProperty] private bool _isEndsAtTimeVisible;

        private bool _isIntroSkipAvailable = false;

        private bool _isNextEpisodeAvailable = false;

        private bool _isOutroSkipAvailable = false;

        [ObservableProperty] private bool _isPaused;


        [ObservableProperty] private bool _isPlaying;

        [ObservableProperty] private bool _isShuffleEnabled;


        [ObservableProperty] private bool _isStatsOverlayVisible;

        [ObservableProperty] private bool _isStatsVisible;

        // State tracking for skip actions

        // Skip button check throttling
        private TimeSpan _lastSkipCheckPosition = TimeSpan.Zero;

        [ObservableProperty] private object _navigationSourceParameter;

        [ObservableProperty] private BaseItemDto _nextEpisode;

        private bool _nextEpisodeButtonOverlayVisible = false;
        private long _pendingSeekPositionAfterQualitySwitch = 0;
        private string _playSessionId;
        private bool _isHlsTrackChange = false; // Track when we're changing tracks on HLS

        // State tracking
        private MediaPlaybackParams _playbackParams;

        private TimeSpan _position = TimeSpan.Zero;
        public TimeSpan Position
        {
            get
            {
                // For HLS streams, we may need to add manifest offset from two sources:
                // 1. PlaybackControlService.HlsManifestOffset - for initial resume operations
                // 2. Local _hlsManifestOffset - for large seeks during playback that create new manifests

                // Use PlaybackControlService offset for resume scenarios
                if (_playbackControlService?.HlsManifestOffset > TimeSpan.Zero)
                {
                    return _position + _playbackControlService.HlsManifestOffset;
                }

                // Use local offset for large seek scenarios (after manifest change)
                if (_hlsManifestOffsetApplied && _hlsManifestOffset > TimeSpan.Zero)
                {
                    return _position + _hlsManifestOffset;
                }

                return _position;
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

        [ObservableProperty] private ObservableCollection<SubtitleTrack> _subtitleTracks = new();

        public MediaPlayerViewModel(ILogger<MediaPlayerViewModel> logger) : base(logger)
        {
            var services = App.Current.Services;

            _playbackControlService = services.GetRequiredService<IPlaybackControlService>();
            _subtitleService = services.GetRequiredService<ISubtitleService>();
            _mediaOptimizationService = services.GetRequiredService<IMediaOptimizationService>();
            _preferencesService = services.GetRequiredService<IPreferencesService>();
            _mediaPlaybackService = services.GetRequiredService<IMediaPlaybackService>();
            _navigationService = services.GetRequiredService<INavigationService>();
            _episodeQueueService = services.GetRequiredService<IEpisodeQueueService>();
            _mediaNavigationService = services.GetRequiredService<IMediaNavigationService>();
            _playbackStatisticsService = services.GetRequiredService<IPlaybackStatisticsService>();
            _mediaControllerService = services.GetRequiredService<IMediaControllerService>();
            _skipSegmentService = services.GetRequiredService<ISkipSegmentService>();
            _mediaControlService = services.GetRequiredService<IMediaControlService>();

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
                BitrateInfo = string.Empty,
                SubtitleInfo = string.Empty
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

                if (_mediaControlService == null)
                {
                    Logger.LogWarning("MediaControlService is null - cannot toggle play/pause");
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
                // Block skipping before playback has actually started
                if (!_hasVideoStarted)
                {
                    Logger.LogWarning("Cannot skip backward - playback has not started yet");
                    return;
                }

                LastActionWasSkip = true;

                if (_mediaControlService == null)
                {
                    Logger.LogWarning("MediaControlService is null - cannot skip backward");
                    return;
                }

                // Check if a specific skip amount was passed as parameter
                var skipSeconds = MediaPlayerConstants.SKIP_BACKWARD_SECONDS; // Default 10 seconds

                if (parameter is int seconds)
                {
                    skipSeconds = seconds;
                }

                // HLS WORKAROUND: Check if backward seek would go before current manifest start
                // Can be removed when server properly handles seeking in HLS streams
                if (_playbackControlService?.HlsManifestOffset > TimeSpan.Zero)
                {
                    var currentRawPosition = MediaPlayerElement?.MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
                    var targetRawPosition = currentRawPosition - TimeSpan.FromSeconds(skipSeconds);

                    if (targetRawPosition < TimeSpan.Zero)
                    {
                        // Need to restart stream at earlier position since we can't seek before manifest start
                        var actualCurrentPosition = currentRawPosition + _playbackControlService.HlsManifestOffset;
                        var actualTargetPosition = actualCurrentPosition - TimeSpan.FromSeconds(skipSeconds);

                        if (actualTargetPosition < TimeSpan.Zero)
                            actualTargetPosition = TimeSpan.Zero;

                        Logger.LogInformation($"[HLS-SEEK] Backward seek would go before manifest start. Restarting at {actualTargetPosition:mm\\:ss}");

                        // Store the target position for restart
                        var restartTicks = actualTargetPosition.Ticks;
                        var wasPlaying = IsPlaying;

                        // Update playback params for the restart
                        _playbackParams.StartPositionTicks = restartTicks;

                        // Stop current playback
                        _mediaControlService.Stop();

                        // Clear the HLS manifest offset since we're starting fresh
                        if (_playbackControlService is PlaybackControlService pcs)
                        {
                            pcs.HlsManifestOffset = TimeSpan.Zero;
                        }

                        // Re-initialize with new position
                        await InitializeAsync(_playbackParams);

                        // Resume playback if it was playing
                        if (wasPlaying)
                        {
                            _mediaControlService.Play();
                        }

                        return;
                    }
                }

                if (_isCurrentStreamHls && skipSeconds >= 60)
                {
                    var rawPosition = _mediaControlService?.MediaPlayer?.PlaybackSession?.Position ?? _position;
                    var currentPosWithOffset = rawPosition + (_playbackControlService?.HlsManifestOffset ?? TimeSpan.Zero);
                    var targetPos = currentPosWithOffset - TimeSpan.FromSeconds(skipSeconds);
                    if (targetPos < TimeSpan.Zero)
                    {
                        targetPos = TimeSpan.Zero;
                    }

                    Logger.LogInformation($"[HLS] Large backward seek: {skipSeconds}s from {currentPosWithOffset:mm\\:ss} to {targetPos:mm\\:ss} - server may create new manifest");
                }

                _mediaControlService.SeekBackward(skipSeconds);
                // Force immediate position update
                UpdatePositionImmediate();

                // Show skip indicator
                ShowSkipIndicator($"-{(skipSeconds >= 60 ? $"{skipSeconds / 60}m" : $"{skipSeconds}s")}");

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
                // Block skipping before playback has actually started
                if (!_hasVideoStarted)
                {
                    Logger.LogWarning("Cannot skip forward - playback has not started yet");
                    return;
                }

                LastActionWasSkip = true;

                if (_mediaControlService == null)
                {
                    Logger.LogWarning("MediaControlService is null - cannot skip forward");
                    return;
                }

                // Check if a specific skip amount was passed as parameter
                var skipSeconds = MediaPlayerConstants.SKIP_FORWARD_SECONDS; // Default 30 seconds

                if (parameter is int seconds)
                {
                    skipSeconds = seconds;
                }

                if (_isCurrentStreamHls)
                {
                    var rawPosition = _mediaControlService?.MediaPlayer?.PlaybackSession?.Position ?? _position;
                    var currentPosWithOffset = rawPosition + (_playbackControlService?.HlsManifestOffset ?? TimeSpan.Zero);
                    var targetPos = currentPosWithOffset + TimeSpan.FromSeconds(skipSeconds);
                    
                    // Prevent seeking too close to the end for HLS streams
                    var metadataDuration = CurrentItem?.RunTimeTicks != null && CurrentItem.RunTimeTicks > 0
                        ? TimeSpan.FromTicks(CurrentItem.RunTimeTicks.Value)
                        : TimeSpan.Zero;
                    
                    if (metadataDuration > TimeSpan.Zero && targetPos >= metadataDuration - TimeSpan.FromSeconds(30))
                    {
                        Logger.LogWarning($"[HLS] Preventing seek to {targetPos:mm\\:ss} - too close to end ({metadataDuration:mm\\:ss}). This could corrupt the HLS manifest.");
                        
                        // If we're within 30 seconds of the end, just jump to near the end but not too close
                        var safeEndPosition = metadataDuration - TimeSpan.FromSeconds(35);
                        if (currentPosWithOffset < safeEndPosition)
                        {
                            var adjustedSkip = (int)(safeEndPosition - currentPosWithOffset).TotalSeconds;
                            Logger.LogInformation($"[HLS] Adjusted skip to {adjustedSkip}s to avoid end-of-stream issues");
                            skipSeconds = adjustedSkip;
                        }
                        else
                        {
                            Logger.LogInformation("[HLS] Already close to end, skipping forward disabled");
                            return;
                        }
                    }
                    
                    if (skipSeconds >= 60)
                    {
                        _expectedHlsSeekTarget = targetPos;
                        _lastSeekTime = DateTime.UtcNow;
                        Interlocked.Increment(ref _pendingSeekCount);
                        Logger.LogInformation($"[HLS] Large seek detected: {skipSeconds}s forward from {currentPosWithOffset:mm\\:ss} to {targetPos:mm\\:ss} (pending seeks: {_pendingSeekCount})");
                    }
                }

                _mediaControlService.SeekForward(skipSeconds);
                // Force immediate position update
                UpdatePositionImmediate();

                // Show skip indicator
                ShowSkipIndicator($"+{(skipSeconds >= 60 ? $"{skipSeconds / 60}m" : $"{skipSeconds}s")}");

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

        [RelayCommand]
        private async Task SkipIntro()
        {
            var context = CreateErrorContext("SkipIntro", ErrorCategory.Media);
            try
            {
                // Block skipping before playback has actually started
                if (!_hasVideoStarted)
                {
                    Logger.LogWarning("Cannot skip intro - playback has not started yet");
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
                // Block skipping before playback has actually started
                if (!_hasVideoStarted)
                {
                    Logger.LogWarning("Cannot skip outro - playback has not started yet");
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
                    // Handle position tracking for resume after track change
                    if (_isCurrentStreamHls)
                    {
                        PrepareHlsTrackChange();
                    }
                    else
                    {
                        _pendingSeekPositionAfterQualitySwitch = Position.Ticks;
                    }

                    await _subtitleService.ChangeSubtitleTrackAsync(subtitle);
                    SelectedSubtitle = subtitle;

                    // Reset flags so resume position is applied when video is ready
                    _hasPerformedInitialSeek = false;
                    _hasVideoStarted = false;
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
                    // Handle position tracking for resume after track change
                    if (_isCurrentStreamHls)
                    {
                        PrepareHlsTrackChange();
                    }
                    else
                    {
                        _pendingSeekPositionAfterQualitySwitch = Position.Ticks;
                    }

                    await _playbackControlService.ChangeAudioTrackAsync(audioTrack);
                    SelectedAudioTrack = audioTrack;

                    // Reset flags so resume position is applied when video is ready
                    _hasPerformedInitialSeek = false;
                    _hasVideoStarted = false;
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

        [RelayCommand]
        private async Task ToggleShuffle()
        {
            var context = CreateErrorContext("ToggleShuffle", ErrorCategory.Media);
            try
            {
                await _mediaNavigationService.SetShuffleModeAsync(!IsShuffleEnabled);
                IsShuffleEnabled = _mediaNavigationService.IsShuffleEnabled();
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

                    // If we don't have the item but have the ItemId, load it
                    if (CurrentItem == null && !string.IsNullOrEmpty(playbackParams.ItemId))
                    {
                        Logger.LogInformation($"Loading item details for ID: {playbackParams.ItemId}");
                        var mediaPlaybackService = App.Current.Services.GetService<IMediaPlaybackService>();
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

                await _playbackControlService.StartPlaybackAsync(mediaSource, _playbackParams.StartPositionTicks);

                // Report playback start position
                // For HLS streams, always report 0 to avoid server restart issues
                var isHlsStream = playbackInfo.MediaSources?.FirstOrDefault()?.TranscodingUrl?.Contains(".m3u8") == true;
                _isCurrentStreamHls = isHlsStream; // Store for later use

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
                    AsyncHelper.FireAndForget(async () => await PreloadNextEpisodeAsync());
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
                    if (_isCurrentStreamHls && metadataDuration > TimeSpan.FromMinutes(5) && duration < TimeSpan.FromMinutes(1))
                    {
                        Logger.LogError($"[HLS-CORRUPTION] Duration corruption detected! Natural: {duration:mm\\:ss}, Metadata: {metadataDuration:mm\\:ss}, Position: {currentPosition:mm\\:ss}");
                    }

                    // Check for HLS manifest recreation from backward seek
                    if (_isCurrentStreamHls && _expectedHlsSeekTarget != TimeSpan.Zero)
                    {
                        var timeSinceSeek = (DateTime.UtcNow - _lastSeekTime).TotalSeconds;
                        if (timeSinceSeek < 5 && _pendingSeekCount > 0) // Recent seek
                        {
                            // Check if we're at position 0 (new manifest created)
                            if (currentPosition.TotalSeconds < 1)
                            {
                                _pendingSeekCount = Math.Max(0, _pendingSeekCount - 1);
                                Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Detected new manifest creation after backward seek (pending: {_pendingSeekCount})");
                                _hlsManifestOffset = _expectedHlsSeekTarget;
                                _hlsManifestOffsetApplied = true; // Mark as applied since we're already at position 0
                                _expectedHlsSeekTarget = TimeSpan.Zero;
                                Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Position 0 in new manifest = {_hlsManifestOffset:mm\\:ss}");
                            }
                        }
                    }

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
                // Check if we're still buffering and have exceeded the timeout
                if (_bufferingStartTime.HasValue && IsBuffering)
                {
                    var bufferingDuration = DateTime.UtcNow - _bufferingStartTime.Value;

                    if (bufferingDuration.TotalSeconds >= BUFFERING_TIMEOUT_SECONDS)
                    {
                        var actualPosition = GetCurrentPlaybackPosition();
                        Logger.LogWarning($"[BUFFERING-TIMEOUT] Buffering timeout reached after {bufferingDuration.TotalSeconds:F1}s at position {actualPosition:mm\\:ss}, HLS: {_isCurrentStreamHls}");

                        if (_isCurrentStreamHls)
                        {
                            Logger.LogWarning("[BUFFERING-TIMEOUT] HLS stream stuck - attempting recovery by toggling playback");

                            // Try recovery: pause and play to restart the stream
                            if (_mediaControlService != null)
                            {
                                _mediaControlService.Pause();
                                AsyncHelper.FireAndForget(async () =>
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
                                return; // Don't give up yet
                            }
                        }

                        // If we get here, recovery failed or not applicable
                        // Stop the timer
                        _bufferingTimeoutTimer?.Stop();

                        Logger.LogError($"[BUFFERING-TIMEOUT] Unable to recover, giving up");

                        // Reset buffering tracking
                        _bufferingStartTime = null;

                        // Show error to user
                        ErrorMessage = "Playback is taking too long. Please check your network connection.";
                        IsError = true;

                        // Navigate back after showing error for a few seconds
                        AsyncHelper.FireAndForget(async () =>
                        {
                            // Give user time to read the error message
                            await Task.Delay(3000);

                            // Clear error and navigate back
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
                }
                else if (!IsBuffering)
                {
                    // If we're no longer buffering, stop the timer
                    _bufferingTimeoutTimer?.Stop();
                    _bufferingStartTime = null;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in buffering timeout check");
            }
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
            // Check if we're disposed
            if (_isDisposed)
            {
                Logger.LogDebug("[VM-PLAYBACK-STATE] Event fired after disposal, ignoring");
                return;
            }

            try
            {
                // Log immediately to see if we even enter the handler
                Logger.LogInformation($"[VM-PLAYBACK-STATE] Handler entered");

                // Get state before doing anything else
                MediaPlaybackState newState = MediaPlaybackState.None;
                try
                {
                    newState = sender.PlaybackState;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[VM-PLAYBACK-STATE] Failed to get PlaybackState from sender");
                    return;
                }

                // Log state change with safe BufferingProgress access
                double bufferProgress = 1.0;
                try
                {
                    bufferProgress = sender?.BufferingProgress ?? 1.0;
                }
                catch (InvalidCastException)
                {
                    // Expected for HLS streams
                }

                Logger.LogInformation($"[VM-PLAYBACK-STATE] State changed to: {newState}, " +
                    $"Position: {sender?.Position.TotalSeconds:F2}s, " +
                    $"BufferingProgress: {bufferProgress:P}");

                // Ensure we're on UI thread
                await RunOnUIThreadAsync(async () =>
                {
                    try
                    {
                        var wasBuffering = IsBuffering;
                        var rawPosition = sender.Position;

                        // Update the internal position first
                        _position = rawPosition;

                        // Use the Position property which includes HLS manifest offset
                        var position = Position;

                        // BufferingProgress often throws InvalidCastException during HLS playback
                        // We'll safely access it only when needed
                        double bufferingProgress = 1.0;
                        try
                        {
                            bufferingProgress = sender.BufferingProgress;
                        }
                        catch (InvalidCastException)
                        {
                            // Expected for HLS streams - ignore
                        }

                        // CanSeek can throw COMException during media source changes
                        bool canSeek = true;
                        try
                        {
                            canSeek = sender.CanSeek;
                        }
                        catch (System.Runtime.InteropServices.COMException)
                        {
                            // Expected during media source transitions - ignore
                        }

                        Logger.LogInformation($"PlaybackStateChanged: {newState}, Position: {position.TotalSeconds:F2}s, BufferingProgress: {bufferingProgress:F2}, CanSeek: {canSeek}, WasBuffering: {wasBuffering}");

                        // Update buffering state
                        IsBuffering = newState == MediaPlaybackState.Buffering;

                        // Log buffering state changes and manage timeout timer
                        if (IsBuffering && !wasBuffering)
                        {
                            Logger.LogInformation($"Buffering started at position {position:mm\\:ss}, HLS: {_isCurrentStreamHls}");

                            // For HLS with expected seek target, check if manifest has changed
                            if (_isCurrentStreamHls && _expectedHlsSeekTarget > TimeSpan.Zero)
                            {
                                var naturalDuration = sender?.NaturalDuration;
                                var metadataDuration = GetMetadataDuration();

                                if (naturalDuration.HasValue && metadataDuration > TimeSpan.Zero)
                                {
                                    var durationDiff = Math.Abs((naturalDuration.Value - metadataDuration).TotalSeconds);
                                    if (durationDiff > 10 && naturalDuration.Value < metadataDuration)
                                    {
                                        Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Detected during buffering. Natural: {naturalDuration.Value:mm\\:ss}, Metadata: {metadataDuration:mm\\:ss}");
                                        // Set up manifest offset for the new manifest
                                        _hlsManifestOffset = _expectedHlsSeekTarget;
                                        _hlsManifestOffsetApplied = false;
                                        _expectedHlsSeekTarget = TimeSpan.Zero;
                                        Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Position 0 in new manifest = {_hlsManifestOffset:mm\\:ss}");
                                    }
                                }
                            }

                            // Track buffering start time
                            _bufferingStartTime = DateTime.UtcNow;

                            // Handle HLS-specific buffering fix (pass session to avoid cross-thread issues)
                            HandleHlsBufferingFix(sender);

                            // Start buffering timeout timer for all streams
                            // (After seek, a direct play stream might become HLS transcode)
                            _bufferingTimeoutTimer?.Start();
                            Logger.LogInformation($"[BUFFERING-TIMEOUT] Started {BUFFERING_TIMEOUT_SECONDS}s timeout timer for {(_isCurrentStreamHls ? "HLS" : "direct")} stream");
                        }
                        else if (!IsBuffering && wasBuffering)
                        {
                            Logger.LogInformation($"Buffering ended at position {position:mm\\:ss}, transitioning to {newState}");

                            // Stop buffering timeout timer
                            if (_bufferingStartTime.HasValue)
                            {
                                var bufferingDuration = DateTime.UtcNow - _bufferingStartTime.Value;
                                Logger.LogInformation($"[BUFFERING-END] Buffering completed after {bufferingDuration.TotalSeconds:F1}s");
                            }

                            _bufferingTimeoutTimer?.Stop();
                            _bufferingStartTime = null;
                            _isHlsTrackChange = false; // Reset flag if buffering ends

                            // Don't need to do anything here - resume is handled when transitioning to Playing state
                        }

                        // When we transition to Playing state, video is ready for operations
                        if (newState == MediaPlaybackState.Playing)
                        {
                            Logger.LogInformation($"Transitioned to Playing state at {position:mm\\:ss}");

                            // Mark video as started when we first transition to Playing state
                            if (!_hasVideoStarted)
                            {
                                _hasVideoStarted = true;
                                Logger.LogInformation("Video playback started");
                            }

                            // Check if we need to apply resume position
                            // This should happen on first transition to Playing state
                            if (!_hasPerformedInitialSeek && _playbackParams?.StartPositionTicks > 0)
                            {
                                Logger.LogInformation("Video playback started - checking for resume position");

                                // Track if we need to restore audio/video after HLS resume
                                var hlsResumeInProgress = false;
                                var originalVolume = 1.0;
                                var originalOpacity = 1.0;

                                // For HLS streams WITH RESUME, wait longer to avoid triggering server restart
                                // The enhanced HLS resume logic will handle retries if needed
                                if (_isCurrentStreamHls && _playbackParams?.StartPositionTicks > 0 && !_hasPerformedInitialSeek)
                                {
                                    Logger.LogInformation("[HLS-RESUME] Waiting 3 seconds for HLS manifest to stabilize before applying resume");

                                    // Mute audio and hide video to prevent spoilers during resume
                                    originalVolume = MediaPlayerElement?.MediaPlayer?.Volume ?? 1.0;
                                    originalOpacity = MediaPlayerElement?.Opacity ?? 1.0;
                                    hlsResumeInProgress = true;

                                    if (MediaPlayerElement?.MediaPlayer != null)
                                    {
                                        MediaPlayerElement.MediaPlayer.Volume = 0;
                                    }

                                    if (MediaPlayerElement != null)
                                    {
                                        MediaPlayerElement.Opacity = 0;
                                    }

                                    await Task.Delay(3000); // Longer wait to let server fully establish the stream

                                    // Check if we're still playing and haven't been interrupted
                                    if (MediaPlayerElement?.MediaPlayer?.PlaybackSession?.PlaybackState != MediaPlaybackState.Playing)
                                    {
                                        Logger.LogWarning("[HLS-RESUME] Playback state changed during wait, skipping resume");

                                        // Restore audio and video even if we're not resuming
                                        if (MediaPlayerElement?.MediaPlayer != null)
                                        {
                                            MediaPlayerElement.MediaPlayer.Volume = originalVolume;
                                        }
                                        if (MediaPlayerElement != null)
                                        {
                                            MediaPlayerElement.Opacity = originalOpacity;
                                        }

                                        return;
                                    }

                                    // Keep audio muted and video hidden until after seek completes
                                    // They'll be restored after the seek below
                                }

                                // Apply resume position now that playback has started
                                // Enhanced resume logic with retry support for both HLS and DirectPlay
                                var resumeResult = await TryApplyResumePositionAsync();

                                // May need multiple attempts for both HLS and DirectPlay streams
                                if (!resumeResult && _playbackControlService != null && _playbackParams?.StartPositionTicks > 0)
                                {
                                    var retryCount = 0;
                                    var maxRetries = _isCurrentStreamHls ? 15 : 8; // HLS needs more time for server to transcode
                                    var retryDelay = _isCurrentStreamHls ? 5000 : 1000; // HLS needs longer delays for server to restart transcode

                                    while (!resumeResult && retryCount < maxRetries)
                                    {
                                        retryCount++;

                                        // Check if still needs resume (for both HLS and DirectPlay)
                                        var stillPending = _isCurrentStreamHls ?
                                            _playbackControlService.IsHlsResumeInProgress() :
                                            !_hasPerformedInitialSeek;


                                        if (stillPending)
                                        {
                                            var streamType = _isCurrentStreamHls ? "HLS-RESUME" : "DirectPlay";
                                            Logger.LogInformation($"[{streamType}] Retry {retryCount}/{maxRetries} in {retryDelay}ms");
                                            await Task.Delay(retryDelay);

                                            resumeResult = await TryApplyResumePositionAsync();

                                            // If resume succeeded, exit immediately
                                            if (resumeResult)
                                            {
                                                break;
                                            }
                                        }
                                        else
                                        {
                                            Logger.LogInformation($"Resume no longer pending, stopping retries");
                                            break;
                                        }
                                    }

                                    if (resumeResult)
                                    {
                                        var streamType = _isCurrentStreamHls ? "HLS" : "DirectPlay";
                                        Logger.LogInformation($"[{streamType}] Successfully resumed after {retryCount} retries");

                                        // Check if we ended up at a different position than requested (common with HLS -noaccurate_seek)
                                        var actualPosition = MediaPlayerElement?.MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
                                        var targetPosition = TimeSpan.FromTicks(_playbackParams?.StartPositionTicks ?? 0);
                                        var diff = Math.Abs((actualPosition - targetPosition).TotalSeconds);

                                        if (diff > 3.0)
                                        {
                                            Logger.LogInformation($"[{streamType}] Accepted server position {actualPosition:mm\\:ss} (target was {targetPosition:mm\\:ss}, diff: {diff:F1}s)");
                                            // Note: We don't update StartPositionTicks here as it's used for progress reporting
                                        }
                                    }
                                    else
                                    {
                                        var streamType = _isCurrentStreamHls ? "HLS" : "DirectPlay";
                                        Logger.LogWarning($"[{streamType}] Failed to resume after all retries");

                                        // Show error to user when resume fails
                                        if (ErrorHandler != null)
                                        {
                                            var errorContext = new ErrorContext(
                                                source: "MediaPlayerViewModel",
                                                operation: "ResumePlayback",
                                                category: ErrorCategory.Media,
                                                severity: ErrorSeverity.Warning)
                                            {
                                                Data = new System.Collections.Generic.Dictionary<string, object>
                                                {
                                                    ["ItemName"] = CurrentItem?.Name ?? "Unknown",
                                                    ["ResumePosition"] = TimeSpan.FromTicks(_playbackParams.StartPositionTicks ?? 0).ToString(@"mm\:ss"),
                                                    ["StreamType"] = streamType
                                                }
                                            };

                                            // Mark as failed
                                            _hasPerformedInitialSeek = true;

                                            // Use non-async HandleError method to show dialog
                                            var context = CreateErrorContext("ResumePlayback", ErrorCategory.Media, ErrorSeverity.Error);

                                            // Use specific exception type for better error handling
                                            var currentPos = MediaPlayerElement?.MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
                                            var targetPos = TimeSpan.FromTicks(_playbackParams?.StartPositionTicks ?? 0);
                                            var resumeException = new ResumeStuckException(currentPos, targetPos, retryCount);

                                            ErrorHandler?.HandleError(resumeException, context, showUserMessage: true);

                                            // Small delay to allow dialog to appear before navigation
                                            await Task.Delay(100);

                                            // Navigate back after dialog attempt
                                            if (_navigationService?.CanGoBack == true)
                                            {
                                                _navigationService.GoBack();
                                            }
                                        }
                                    }
                                }

                                if (resumeResult)
                                {
                                    Logger.LogInformation("Applied resume position on playback start");
                                }

                                // If HLS resume was in progress, restore audio/video
                                if (hlsResumeInProgress)
                                {
                                    // Wait briefly for playback to stabilize
                                    await Task.Delay(500);

                                    if (MediaPlayerElement?.MediaPlayer != null)
                                    {
                                        MediaPlayerElement.MediaPlayer.Volume = originalVolume;
                                    }
                                    if (MediaPlayerElement != null)
                                    {
                                        MediaPlayerElement.Opacity = originalOpacity;
                                    }
                                }
                                else if (_isCurrentStreamHls && _playbackParams?.StartPositionTicks > 0)
                                {
                                    // For HLS, if no client-side resume was applied, it means server should handle it
                                    var resumeTime = TimeSpan.FromTicks(_playbackParams.StartPositionTicks.Value);
                                    Logger.LogInformation($"HLS stream - server should handle resume to {resumeTime:hh\\:mm\\:ss} via StartTimeTicks");

                                    // Restore audio/video if we were hiding them
                                    if (hlsResumeInProgress)
                                    {
                                        if (MediaPlayerElement?.MediaPlayer != null)
                                        {
                                            MediaPlayerElement.MediaPlayer.Volume = originalVolume;
                                        }
                                        if (MediaPlayerElement != null)
                                        {
                                            MediaPlayerElement.Opacity = originalOpacity;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception innerEx)
                    {
                        Logger.LogError(innerEx, $"Error inside RunOnUIThreadAsync for state {newState}");
                    }
                });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error in OnPlaybackStateChanged event handler (outer)");
            }
        }


        private void OnSubtitleChanged(object sender, SubtitleTrack subtitle)
        {
            SelectedSubtitle = subtitle;
        }

        private void OnNavigationStateChanged(object sender, EventArgs e)
        {
            IsNextEpisodeAvailable = _mediaNavigationService.HasNextItem();
        }

        private void OnStatsUpdated(object sender, PlaybackStats stats)
        {
            CurrentStats = stats;
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
                        if (_navigationService.CanGoBack)
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
                Logger.LogInformation(
                    $"[MEDIA-OPENED] Natural duration: {sender.PlaybackSession.NaturalDuration.TotalSeconds}s, " +
                    $"CanSeek: {sender.PlaybackSession.CanSeek}, " +
                    $"IsProtected: {sender.PlaybackSession.IsProtected}");

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

        /// <summary>
        /// Attempts to apply pending resume position with proper safeguards
        /// </summary>
        private async Task<bool> TryApplyResumePositionAsync()
        {
            Logger.LogInformation($"TryApplyResumePositionAsync called. HasPerformedInitialSeek: {_hasPerformedInitialSeek}");

            bool resumeApplied = false;

            // First check if we have a pending seek from quality/track change
            if (_pendingSeekPositionAfterQualitySwitch > 0)
            {
                var targetPosition = TimeSpan.FromTicks(_pendingSeekPositionAfterQualitySwitch);
                Logger.LogInformation($"Applying pending seek after quality/track switch to {targetPosition:mm\\:ss}");

                if (MediaPlayerElement?.MediaPlayer?.PlaybackSession != null)
                {
                    try
                    {
                        MediaPlayerElement.MediaPlayer.PlaybackSession.Position = targetPosition;
                        _pendingSeekPositionAfterQualitySwitch = 0;
                        resumeApplied = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, $"Failed to apply quality/track switch resume position");
                    }
                }
            }
            // Check for normal resume position from PlaybackControlService
            // For HLS, this will trigger the server to create a new manifest at the resume position
            // Note: ApplyPendingResumePosition returns false when it needs more retries, not just when there's no position
            else if (_playbackControlService != null)
            {
                var result = _playbackControlService.ApplyPendingResumePosition();
                if (result)
                {
                    Logger.LogInformation($"Applied pending resume position from PlaybackControlService");
                    resumeApplied = true;

                    // For HLS streams, when PlaybackControlService detects stuck buffering and seeks to position 0,
                    // we need to set up manifest offset tracking
                    if (_isCurrentStreamHls && _playbackParams?.StartPositionTicks > 0)
                    {
                        var resumePos = TimeSpan.FromTicks(_playbackParams.StartPositionTicks.Value);
                        Logger.LogInformation($"[HLS-RESUME] Setting up manifest offset tracking for position {resumePos:mm\\:ss}");
                        PrepareHlsResume(resumePos);

                        // Since PlaybackControlService already seeked to position 0, mark it as applied
                        CompleteHlsResumeFix();
                    }
                }
                // If false, it either needs more retries or there's no pending position
                // The retry loop in the caller will handle this
            }

            // Common handling for successful resume
            if (resumeApplied)
            {
                _hasPerformedInitialSeek = true;
                IsApplyingResume = true;
                Logger.LogInformation($"Resume position applied");
                return true;
            }

            // Return false - either no resume position or needs more retries
            return false;
        }


        private async void OnSeekCompleted(MediaPlayer sender, object args)
        {
            if (_isDisposed) return;

            try
            {
                var seekPosition = sender?.PlaybackSession?.Position ?? TimeSpan.Zero;
                Logger.LogInformation($"[SEEK-COMPLETED] Position after seek: {seekPosition.TotalSeconds:F2}s");

                await RunOnUIThreadAsync(() =>
            {
                var position = sender?.PlaybackSession?.Position ?? TimeSpan.Zero;
                var state = sender?.PlaybackSession?.PlaybackState;
                var naturalDuration = sender?.PlaybackSession?.NaturalDuration;
                var metadataDuration = GetMetadataDuration();

                // Decrement pending seek count
                if (_pendingSeekCount > 0)
                {
                    Interlocked.Decrement(ref _pendingSeekCount);
                }

                Logger.LogInformation($"SeekCompleted event fired. Position: {position:mm\\:ss}, State: {state}, Pending seeks: {_pendingSeekCount}");
                Logger.LogInformation($"NaturalDuration after seek: {naturalDuration:mm\\:ss}, MetadataDuration: {metadataDuration:mm\\:ss}");

                // Store the actual position for manifest corruption detection
                // This helps detect when initial resume creates a truncated manifest
                if (_isCurrentStreamHls && !_hasPerformedInitialSeek)
                {
                    _actualResumePosition = position;
                    _hasPerformedInitialSeek = true;
                }

                // For HLS streams with resume, after the initial client-side seek completes,
                // the server creates a new manifest starting at the seek position
                if (_isCurrentStreamHls && _hlsManifestOffset > TimeSpan.Zero && !_hasPerformedInitialSeek)
                {
                    // Check if we just completed the initial resume seek
                    var expectedResumePos = _hlsManifestOffset;
                    var diff = Math.Abs((position - expectedResumePos).TotalSeconds);

                    if (diff < 5) // We're close to the expected resume position
                    {
                        Logger.LogInformation($"[HLS-RESUME] Initial seek to {position:mm\\:ss} completed, server should create new manifest");
                        _hasPerformedInitialSeek = true;

                        // The server now has a new manifest starting at this position
                        // If we get stuck buffering, we'll need to seek to position 0 of that manifest
                    }
                }

                // Check if NaturalDuration has changed unexpectedly (HLS manifest recreation/corruption)
                if (naturalDuration.HasValue && metadataDuration > TimeSpan.Zero)
                {
                    var durationDiff = Math.Abs((naturalDuration.Value - metadataDuration).TotalSeconds);
                    if (durationDiff > 10) // More than 10 seconds difference
                    {
                        Logger.LogWarning($"Duration mismatch after seek! Natural: {naturalDuration:mm\\:ss}, Metadata: {metadataDuration:mm\\:ss}, Diff: {durationDiff:F1}s");

                        // Detect potential HLS manifest issues from resume
                        if (_isCurrentStreamHls && naturalDuration.Value < metadataDuration * 0.5)
                        {
                            Logger.LogWarning($"[HLS-MANIFEST] Manifest appears truncated after resume seek");
                            Logger.LogWarning($"[HLS-MANIFEST] Natural duration is only {(naturalDuration.Value.TotalSeconds / metadataDuration.TotalSeconds * 100):F1}% of expected");

                            // If we're paused, try auto-playing to recover
                            if (sender?.PlaybackSession?.PlaybackState == MediaPlaybackState.Paused)
                            {
                                Logger.LogInformation($"[HLS-RECOVERY] Attempting to recover by resuming playback");
                                _ = Task.Run(async () =>
                                {
                                    await Task.Delay(500); // Brief delay
                                    await RunOnUIThreadAsync(() =>
                                    {
                                        if (_mediaControlService != null && !IsPlaying)
                                        {
                                            Logger.LogInformation($"[HLS-RECOVERY] Auto-playing to recover from manifest issue");
                                            _mediaControlService.Play();
                                        }
                                    });
                                });
                            }
                        }

                        // Check for manifest corruption on initial resume
                        // If natural duration becomes tiny (< 1 minute) after resume, it's corrupted
                        if (_isCurrentStreamHls && naturalDuration.Value < TimeSpan.FromMinutes(1) &&
                            position > naturalDuration.Value && _actualResumePosition > TimeSpan.Zero)
                        {
                            Logger.LogError($"[HLS-CORRUPT-RESUME] Manifest corrupted after resume to {_actualResumePosition:mm\\:ss}");
                            Logger.LogError($"[HLS-CORRUPT-RESUME] Natural duration is only {naturalDuration.Value:mm\\:ss}, position is {position:mm\\:ss}");

                            // The server created a corrupt manifest when we resumed
                            // MediaEnded will fire automatically since position > duration
                            // User will need to restart playback
                            return;
                        }

                        // For HLS streams, if NaturalDuration becomes significantly shorter than metadata duration
                        // after a seek, it indicates Jellyfin created a new manifest starting at the seek position
                        if (_isCurrentStreamHls && naturalDuration.Value < metadataDuration && _expectedHlsSeekTarget > TimeSpan.Zero)
                        {
                            var percentageOfOriginal = (naturalDuration.Value.TotalSeconds / metadataDuration.TotalSeconds) * 100;
                            Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Detected new HLS manifest after seek. Natural duration is {percentageOfOriginal:F1}% of metadata duration");
                            Logger.LogInformation($"[HLS-MANIFEST-CHANGE] New manifest starts at {_expectedHlsSeekTarget:mm\\:ss}, duration: {naturalDuration.Value:mm\\:ss}");

                            // Only process the manifest change if there are no more pending seeks
                            // OR if it's been more than 2 seconds since the last seek (timeout for stuck seeks)
                            var timeSinceLastSeek = DateTime.UtcNow - _lastSeekTime;
                            var shouldProcessManifest = _pendingSeekCount == 0 || timeSinceLastSeek.TotalSeconds > 2;

                            if (shouldProcessManifest)
                            {
                                Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Processing manifest change (pending seeks: {_pendingSeekCount}, time since last seek: {timeSinceLastSeek.TotalSeconds:F1}s)");

                                // Reset pending seeks since we're processing this one
                                _pendingSeekCount = 0;

                                // The new manifest starts at the seek position
                                // Position 0 in new manifest = _expectedHlsSeekTarget in original timeline
                                _hlsManifestOffset = _expectedHlsSeekTarget;
                                _hlsManifestOffsetApplied = false; // Will be set to true after we seek to 0

                                // Now we need to seek to position 0 of the new manifest
                                Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Setting up offset tracking. Position 0 = {_hlsManifestOffset:mm\\:ss}");

                                // Clear the expected target since we've handled it
                                _expectedHlsSeekTarget = TimeSpan.Zero;

                                // Apply the HLS fix immediately to seek to position 0 of the new manifest
                                // This prevents MediaEnded from firing due to position being past the new duration
                                Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Applying immediate seek to position 0 of new manifest");
                                if (MediaPlayerElement?.MediaPlayer?.PlaybackSession != null)
                                {
                                    MediaPlayerElement.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
                                    CompleteHlsResumeFix();
                                    Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Seeked to position 0, playback should continue from {_hlsManifestOffset:mm\\:ss}");
                                }
                            }
                            else
                            {
                                Logger.LogInformation($"[HLS-MANIFEST-CHANGE] Skipping manifest offset due to {_pendingSeekCount} pending seeks");
                                // Keep the expected target for when the next SeekCompleted fires
                            }
                        }
                    }
                }


                // Clear the resume flag now that seeking is actually done
                if (IsApplyingResume)
                {
                    Logger.LogInformation($"Resume seek completed");
                    IsApplyingResume = false;
                }
                else
                {
                    Logger.LogInformation($"SeekCompleted for user-initiated seek");
                }
            });
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[ERROR] Exception in OnSeekCompleted handler");
            }
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
                    AsyncHelper.FireAndForget(async () => await PlayNextEpisode());
                }
            }
        }


        public async void UpdatePositionImmediate()
        {
            if (_isDisposed) return;

            if (MediaPlayerElement?.MediaPlayer?.PlaybackSession != null)
            {
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
        }

        private void ShowSkipIndicator(string text)
        {            // In the future, this could trigger a visual overlay
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

        /// <summary>
        /// Prepares HLS stream for resume playback
        /// </summary>
        private void PrepareHlsResume(TimeSpan resumePosition)
        {
            if (!_isCurrentStreamHls)
            {
                return;
            }

            _hlsManifestOffset = resumePosition;
            _hlsManifestOffsetApplied = false;
            Logger?.LogInformation($"[HLS] Prepared resume at {resumePosition:mm\\:ss}");
        }

        /// <summary>
        /// Prepares HLS stream for track change (subtitle/audio)
        /// </summary>
        private void PrepareHlsTrackChange()
        {
            if (!_isCurrentStreamHls)
            {
                return;
            }

            var currentPosition = Position; // This already includes offset if applied
            _hlsManifestOffset = currentPosition;
            _hlsManifestOffsetApplied = false;
            _isHlsTrackChange = true;
            _pendingSeekPositionAfterQualitySwitch = 0; // Don't seek, HLS fix will handle it

            Logger?.LogInformation($"[HLS] Track change at {currentPosition:mm\\:ss}");
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

            Logger.LogInformation($"[HLS-RESUME] Buffering at resume position, will try seeking to manifest start");

            AsyncHelper.FireAndForget(async () =>
            {
                await Task.Delay(500); // Short delay to allow manifest to load but minimize audio buffering

                if (!IsBuffering)
                {
                    return;
                }

                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    if (MediaPlayerElement?.MediaPlayer?.PlaybackSession != null)
                    {
                        var currentState = MediaPlayerElement.MediaPlayer.PlaybackSession.PlaybackState;
                        Logger.LogInformation($"[HLS-RESUME] Applying fix - current state: {currentState}, position before: {MediaPlayerElement.MediaPlayer.PlaybackSession.Position:mm\\:ss}");

                        // Pause briefly to ensure clean audio buffer transition
                        var wasPlaying = currentState == MediaPlaybackState.Playing || currentState == MediaPlaybackState.Buffering;
                        if (wasPlaying)
                        {
                            Logger.LogInformation("[HLS-RESUME] Pausing playback for clean seek");
                            MediaPlayerElement.MediaPlayer.Pause();
                        }

                        // Seek to start of new manifest
                        MediaPlayerElement.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
                        Logger.LogInformation($"[HLS-RESUME] Seeked to position 0, new position: {MediaPlayerElement.MediaPlayer.PlaybackSession.Position:mm\\:ss}");
                        CompleteHlsResumeFix();

                        // Resume if we were playing
                        if (wasPlaying)
                        {
                            Logger.LogInformation("[HLS-RESUME] Resuming playback");
                            MediaPlayerElement.MediaPlayer.Play();
                        }
                    }
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
            if (!_isCurrentStreamHls || _hlsManifestOffset <= TimeSpan.Zero)
            {
                return false;
            }

            // Apply fix for track changes or large seeks that create new manifests
            var shouldApply = _isHlsTrackChange || (_hlsManifestOffset > TimeSpan.Zero && !_hlsManifestOffsetApplied);
            if (!shouldApply)
            {
                return false;
            }

            var rawPosition = session?.Position ?? TimeSpan.Zero;
            var diff = Math.Abs((rawPosition - _hlsManifestOffset).TotalSeconds);
            return diff < 10;
        }

        /// <summary>
        /// Completes the HLS resume fix by updating flags
        /// </summary>
        private void CompleteHlsResumeFix()
        {
            _hlsManifestOffsetApplied = true;
            _isHlsTrackChange = false;
            OnPropertyChanged(nameof(Position)); // UI needs to update
        }

        /// <summary>
        /// Resets all HLS-related state
        /// </summary>
        private void ResetHlsState()
        {
            _hlsManifestOffset = TimeSpan.Zero;
            _hlsManifestOffsetApplied = false;
            _isHlsTrackChange = false;
            _isCurrentStreamHls = false;
            _expectedHlsSeekTarget = TimeSpan.Zero;
            _pendingSeekCount = 0;
            _lastSeekTime = DateTime.MinValue;
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

            _positionTimer?.Stop();
            _controlVisibilityTimer?.Stop();
            _statsUpdateTimer?.Stop();

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
