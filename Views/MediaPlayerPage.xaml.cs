using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.ViewModels;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Gelatinarm.Views
{
    /// <summary>
    ///     Refactored MediaPlayerPage with minimal code-behind
    ///     All business logic is handled by MediaPlayerViewModel and services
    /// </summary>
    public sealed partial class MediaPlayerPage : BasePage
    {
        private readonly IDialogService _dialogService;

        private readonly INavigationStateService _navigationStateService;
        private volatile int _controlVisibilityCounter = 0;
        private DispatcherTimer _controlVisibilityTimer;
        private volatile int _isDisposing = 0; // 0 = not disposing, 1 = disposing
        private MediaPlaybackState? _stateBeforeFocusLost;

        public MediaPlayerPage() : base(typeof(MediaPlayerPage))
        {
            InitializeComponent(); _navigationStateService = GetRequiredService<INavigationStateService>();
            _dialogService = GetRequiredService<IDialogService>();

            // Wire up MediaPlayerElement
            ViewModel.MediaPlayerElement = MediaPlayer;

            // Subscribe to ViewModel events
            ViewModel.ToggleControlsRequested += OnToggleControlsRequested; InitializeControlVisibilityTimer();

            // Subscribe to page events
            KeyDown += MediaPlayerPage_KeyDown;
        }

        protected override Type ViewModelType => typeof(MediaPlayerViewModel);
        public MediaPlayerViewModel ViewModel => (MediaPlayerViewModel)base.ViewModel;

        #region Helper Methods

        private async void ShowError(string message)
        {
            // Show error using DialogService
            await _dialogService.ShowErrorAsync("Playback Error", message);
        }

        #endregion

        #region Cleanup

        private void Dispose()
        {
            if (Interlocked.CompareExchange(ref _isDisposing, 1, 0) != 0)
            {
                return; // Already disposing
            }

            try
            {
                // Stop control visibility timer
                _controlVisibilityTimer?.Stop();

                // Clean up MediaPlayer
                if (MediaPlayer?.MediaPlayer != null)
                {
                    MediaPlayer.MediaPlayer.Pause();
                    MediaPlayer.Source = null;
                }

                // Dispose ViewModel
                ViewModel?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error during page disposal");
            }
        }

        #endregion

        #region Navigation

        protected override async Task InitializePageAsync(object parameter)
        {
            if (parameter is MediaPlaybackParams playbackParams)
            {
                try
                {
                    // Stop mini player if it's playing
                    var musicPlayerService = GetService<IMusicPlayerService>();
                    if (musicPlayerService != null && musicPlayerService.IsPlaying)
                    {
                        musicPlayerService.Stop();
                        Logger.LogInformation("Stopped MusicPlayer before starting video playback");
                    }

                    // Check if we already have a session for this queue (e.g., navigating between episodes)
                    var existingSession = _navigationStateService.GetCurrentPlaybackSession();

                    // Determine if we're continuing an existing session
                    bool isContinuingSession = false;

                    if (existingSession != null)
                    {
                        // For shuffle sessions, check if it's from the same originating page
                        if (playbackParams.IsShuffled && existingSession.IsShuffled)
                        {
                            isContinuingSession = existingSession.OriginatingPage == playbackParams.NavigationSourcePage;
                            Logger.LogInformation($"Shuffle session check - Continuing: {isContinuingSession}, " +
                                $"Existing page: {existingSession.OriginatingPage?.Name}, " +
                                $"New page: {playbackParams.NavigationSourcePage?.Name}");
                        }
                        // For regular queue playback, check if it's the same series/season
                        else if (!playbackParams.IsShuffled && !existingSession.IsShuffled)
                        {
                            // Check if both queues are for the same series
                            var existingFirstItem = existingSession.Queue?.FirstOrDefault();
                            var newFirstItem = playbackParams.QueueItems?.FirstOrDefault();
                            if (existingFirstItem?.SeriesId != null && newFirstItem?.SeriesId != null)
                            {
                                isContinuingSession = existingFirstItem.SeriesId == newFirstItem.SeriesId;
                            }
                        }

                        if (isContinuingSession)
                        {
                            Logger.LogInformation($"Continuing session - preserving OriginatingPageState type: {existingSession.OriginatingPageState?.GetType().Name}");
                        }
                    }

                    if (isContinuingSession)
                    {
                        // Update existing session with new index but keep original navigation info
                        existingSession.CurrentIndex = playbackParams.StartIndex >= 0
                            ? playbackParams.StartIndex
                            : playbackParams.QueueItems?.FindIndex(i =>
                                i.Id?.ToString() == playbackParams.ItemId) ?? 0;

                        Logger.LogInformation(
                            $"Continuing existing session {existingSession.SessionId}, index: {existingSession.CurrentIndex}");
                    }
                    else if (playbackParams.NavigationSourcePage != null)
                    {
                        // Create new session only if we don't have one or it's a different queue
                        var session = new PlaybackSession
                        {
                            SessionId = Guid.NewGuid().ToString(),
                            OriginatingPage = playbackParams.NavigationSourcePage,
                            OriginatingPageState = playbackParams.NavigationSourceParameter,
                            StartTime = DateTime.UtcNow,
                            IsFromContinueWatching = playbackParams.StartPositionTicks > 0,
                            Queue = playbackParams.QueueItems,
                            IsShuffled = playbackParams.IsShuffled,
                            SeriesId = null, // Will be set when item is loaded
                            SeasonId = null, // Will be set when item is loaded
                            CurrentIndex = playbackParams.StartIndex >= 0
                                ? playbackParams.StartIndex
                                : playbackParams.QueueItems?.FindIndex(i =>
                                    i.Id?.ToString() == playbackParams.ItemId) ?? 0
                        };

                        _navigationStateService.SavePlaybackSession(session);
                        Logger.LogInformation(
                            $"Created new playback session {session.SessionId} from {session.OriginatingPage?.Name}");
                        Logger.LogInformation(
                            $"OriginatingPageState type: {session.OriginatingPageState?.GetType().Name}");
                    }

                    // Initialize ViewModel with playback parameters
                    await ViewModel.InitializeAsync(playbackParams);

                    // Apply video stretch mode from preferences
                    var preferencesService = App.Current.Services.GetService<IPreferencesService>();
                    if (preferencesService != null)
                    {
                        var appPrefs = await preferencesService.GetAppPreferencesAsync();
                        if (appPrefs != null && MediaPlayer != null)
                        {
                            MediaPlayer.Stretch = appPrefs.VideoStretchMode == "UniformToFill"
                                ? Windows.UI.Xaml.Media.Stretch.UniformToFill
                                : Windows.UI.Xaml.Media.Stretch.Uniform;
                        }
                    }

                    // Update session with episode info if this is an episode
                    if (playbackParams.Item?.Type == BaseItemDto_Type.Episode)
                    {
                        var session = _navigationStateService.GetCurrentPlaybackSession();
                        if (session != null)
                        {
                            session.SeriesId = playbackParams.Item.SeriesId?.ToString();
                            session.SeasonId = playbackParams.Item.SeasonId?.ToString();
                            session.CurrentItem = playbackParams.Item;
                            _navigationStateService.SavePlaybackSession(session);
                        }

                        // Special handling for Continue Watching - build episode queue
                        if (playbackParams.NavigationSourceParameter is BaseItemDto episodeFromContinueWatching &&
                            episodeFromContinueWatching.Type == BaseItemDto_Type.Episode &&
                            episodeFromContinueWatching.SeriesId.HasValue)
                        {
                            var episodeQueueService = App.Current.Services.GetService<IEpisodeQueueService>();
                            if (episodeQueueService != null)
                            {
                                var (queue, startIndex, success) =
                                    await episodeQueueService.BuildContinueWatchingQueueAsync(
                                        episodeFromContinueWatching);
                                if (success && queue != null)
                                {
                                    playbackParams.QueueItems = queue;
                                    playbackParams.StartIndex = startIndex;

                                    // Update the session with the queue
                                    var queueSession = _navigationStateService.GetCurrentPlaybackSession();
                                    if (queueSession != null)
                                    {
                                        queueSession.Queue = queue;
                                        queueSession.CurrentIndex = startIndex;
                                        _navigationStateService.SavePlaybackSession(queueSession);
                                    }

                                    // Update the view model's navigation service with the queue
                                    var mediaNavigationService = ViewModel.GetMediaNavigationService();
                                    if (mediaNavigationService != null)
                                    {
                                        await mediaNavigationService.InitializeAsync(playbackParams,
                                            episodeFromContinueWatching);
                                    }
                                }
                            }
                        }
                    }

                    // Don't start the control visibility timer yet - wait for playback to actually begin
                    // Timer will be started in OnMediaOpened event
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to initialize media playback");
                    ShowError("Failed to start playback");
                    GoBack();
                }
            }
            else
            {
                Logger.LogError("Invalid navigation parameter");
                GoBack();
            }
        }

        protected override void OnNavigatingAway()
        {
            if (ViewModel?.IsPlaying == true)
            {
                FireAndForget(() => ViewModel.StopPlayback(), "StopPlayback");
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            Logger.LogInformation("MediaPlayerPage: OnNavigatedFrom called");

            // CRITICAL: Stop playback immediately to prevent video from continuing in background
            try
            {
                // Stop the media player first - this must be synchronous and immediate
                if (ViewModel?.MediaPlayerElement?.MediaPlayer != null)
                {
                    ViewModel.MediaPlayerElement.MediaPlayer.Pause();
                    ViewModel.MediaPlayerElement.MediaPlayer.Source = null;
                    Logger.LogInformation("MediaPlayerPage: Media player stopped immediately");
                }

                // Stop all timers immediately
                _controlVisibilityTimer?.Stop();
                ViewModel?.StopTimers();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to stop media player immediately");
            }

            // Start async cleanup and reporting - let it complete in the background naturally
            if (ViewModel != null)
            {
                // Create a proper async task that will complete in the background
                // We don't await it (would block UI) but we let it run to completion
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Report playback stopped
                        await ViewModel.ReportPlaybackStoppedAsync();
                        Logger.LogInformation("MediaPlayerPage: Playback stop reported successfully");

                        // Do remaining cleanup
                        await ViewModel.CleanupRemainingAsync();
                        Logger.LogInformation("MediaPlayerPage: CleanupRemainingAsync completed");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Failed during async cleanup");
                    }
                });
            }

            base.OnNavigatedFrom(e);

            // Final cleanup
            Dispose();
        }

        private void GoBack()
        {
            // Check if we have a saved playback session
            var session = _navigationStateService?.GetCurrentPlaybackSession();
            if (session != null)
            {
                // If this is a multi-episode session, save the current state for returning
                if (session.IsMultiEpisodeSession && session.EpisodesWatched > 0)
                {
                    // Save state so user can continue from where they left off
                    Logger.LogInformation(
                        $"Saving state for multi-episode session with {session.EpisodesWatched} episodes watched");

                    if (session.OriginatingPage != null)
                    {
                        _navigationStateService.SaveReturnState(session.OriginatingPage,
                            new
                            {
                                LastWatchedEpisodeId = session.CurrentItem?.Id,
                                session.EpisodesWatched,
                                session.SessionId
                            });
                    }
                }

                // Navigate back to the originating page with state
                if (session.OriginatingPage != null)
                {
                    Logger.LogInformation($"Returning to {session.OriginatingPage.Name} with saved state");
                    
                    // Use the current episode from the session for navigation to ensure correct info display
                    object navigationParameter = session.OriginatingPageState;
                    
                    // If we've been watching episodes and the originating page is SeasonDetailsPage,
                    // navigate with the current episode so it displays correctly
                    if (session.CurrentItem != null && 
                        session.CurrentItem.Type == BaseItemDto_Type.Episode &&
                        session.OriginatingPage == typeof(SeasonDetailsPage))
                    {
                        Logger.LogInformation($"Using current episode '{session.CurrentItem.Name}' for navigation back to SeasonDetailsPage");
                        navigationParameter = session.CurrentItem;
                    }
                    
                    _navigationStateService.ClearPlaybackSession();

                    // Always navigate forward with the updated parameter to ensure correct state
                    NavigationService.Navigate(session.OriginatingPage, navigationParameter);

                    return;
                }
            }

            // If no session or no originating page, use standard back navigation
            if (NavigationService.CanGoBack)
            {
                NavigationService.GoBack();
            }
            else
            {
                NavigationService.Navigate(typeof(LibraryPage));
            }
        }

        #endregion

        #region Page Lifecycle

        protected override async Task OnPageLoadedAsync()
        {
            // Initialize control opacity to 0 (they'll fade in when shown)
            if (CustomControlsOverlay != null)
            {
                CustomControlsOverlay.Opacity = 0;
            }

            if (InfoOverlay != null)
            {
                InfoOverlay.Opacity = 0;
            }

            // Focus management - set initial focus to MediaPlayer
            var focusResult = MediaPlayer.Focus(FocusState.Programmatic);
            Logger?.LogInformation($"OnPageLoaded: MediaPlayer focus result: {focusResult}");

            // MediaPlayer handles keyboard input when controls are hidden

            // Prevent focus from escaping downward from control buttons
            ConfigureControlButtonsFocusNavigation();

            // Prevent analog stick navigation by disabling XY focus navigation when controls are hidden
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Disabled;

            // Subscribe to MediaPlayer events
            if (MediaPlayer?.MediaPlayer != null)
            {
                MediaPlayer.MediaPlayer.MediaFailed += OnMediaFailed;
                MediaPlayer.MediaPlayer.MediaEnded += OnMediaEnded;
                MediaPlayer.MediaPlayer.SeekCompleted += OnSeekCompleted;
            }

            // Subscribe to ViewModel property changes
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Subscribe to skip button availability events
            ViewModel.SkipButtonBecameAvailable += OnSkipButtonBecameAvailable;

            // Add window activation handlers for proper video pause/resume
            Window.Current.Activated += Window_Activated;
            Window.Current.VisibilityChanged += Window_VisibilityChanged;

            await Task.CompletedTask;
        }

        protected override void OnPageUnloadedCore()
        {
            // Unsubscribe from events
            if (MediaPlayer?.MediaPlayer != null)
            {
                MediaPlayer.MediaPlayer.MediaFailed -= OnMediaFailed;
                MediaPlayer.MediaPlayer.MediaEnded -= OnMediaEnded;
                MediaPlayer.MediaPlayer.SeekCompleted -= OnSeekCompleted;
            }

            if (ViewModel != null)
            {
                ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                ViewModel.SkipButtonBecameAvailable -= OnSkipButtonBecameAvailable;
                ViewModel.ToggleControlsRequested -= OnToggleControlsRequested;
            }

            // Unsubscribe from page events
            KeyDown -= MediaPlayerPage_KeyDown;

            // Unsubscribe from window events
            Window.Current.Activated -= Window_Activated;
            Window.Current.VisibilityChanged -= Window_VisibilityChanged;

            // Stop timers
            _controlVisibilityTimer?.Stop();

            // Cleanup
            Dispose();
        }

        #endregion

        #region MediaPlayer Events

        private async void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            Logger.LogError($"Media playback failed: {args.Error} - {args.ErrorMessage}");

            await UIHelper.RunOnUIThreadAsync(() =>
            {
                ShowError($"Playback failed: {args.ErrorMessage}");
            }, Dispatcher, Logger);
        }

        private async void OnMediaEnded(MediaPlayer sender, object args)
        {
            // Log detailed information about the MediaEnded event
            var naturalDuration = sender.PlaybackSession?.NaturalDuration;
            var rawPosition = sender.PlaybackSession?.Position ?? TimeSpan.Zero;
            // Use ViewModel.Position which already includes HLS offset
            var currentPosition = ViewModel?.Position ?? rawPosition;
            var metadataDuration = ViewModel?.CurrentItem?.RunTimeTicks != null && ViewModel.CurrentItem.RunTimeTicks > 0
                ? TimeSpan.FromTicks(ViewModel.CurrentItem.RunTimeTicks.Value)
                : TimeSpan.Zero;

            Logger.LogInformation($"MediaEnded event - RawPosition: {rawPosition:mm\\:ss}, " +
                $"Position (with offset): {currentPosition:mm\\:ss}, " +
                $"NaturalDuration: {naturalDuration:mm\\:ss}, " +
                $"MetadataDuration: {metadataDuration:mm\\:ss}");

            // Ignore MediaEnded if we're in the process of applying a resume position
            // This can happen with HLS streams where seeking near the end triggers a false MediaEnded
            if (ViewModel?.IsApplyingResume == true)
            {
                Logger.LogWarning("Ignoring MediaEnded event during resume operation");
                return;
            }

            // Additional safeguard: Check if we're actually near the end using metadata duration
            if (metadataDuration > TimeSpan.Zero)
            {
                var percentComplete = currentPosition.TotalSeconds / metadataDuration.TotalSeconds * 100;
                var percentOfNatural = naturalDuration.HasValue && naturalDuration.Value > TimeSpan.Zero
                    ? rawPosition.TotalSeconds / naturalDuration.Value.TotalSeconds * 100  // Compare raw to raw!
                    : 0;

                // If we're not actually near the end (less than 95%), this is likely a false MediaEnded
                // Special case: HLS manifest corruption causes NaturalDuration to become very short
                var isHlsCorruption = naturalDuration.HasValue &&
                                     naturalDuration.Value < TimeSpan.FromMinutes(1) &&
                                     metadataDuration > TimeSpan.FromMinutes(5) &&
                                     rawPosition > naturalDuration.Value;  // Compare raw to raw!

                if (percentComplete < 95)
                {
                    Logger.LogWarning($"Premature MediaEnded at {percentComplete:F2}% of metadata duration " +
                        $"({percentOfNatural:F2}% of natural duration)");
                    Logger.LogWarning($"Position={currentPosition:mm\\:ss}, " +
                        $"MetadataDuration={metadataDuration:mm\\:ss}, " +
                        $"NaturalDuration={naturalDuration:mm\\:ss}");

                    if (isHlsCorruption)
                    {
                        Logger.LogError("[HLS-CORRUPT] Detected HLS manifest corruption - natural duration is impossibly short");

                        // Show error message to user
                        _ = ShowHlsCorruptionError();
                    }

                    return;
                }
            }

            // ViewModel will handle auto-play and navigation
            await UIHelper.RunOnUIThreadAsync(() =>
            {
                // If no auto-play occurred, navigate back
                if (!ViewModel.IsPlaying)
                {
                    // Update session if we watched episodes
                    var session = _navigationStateService?.GetCurrentPlaybackSession();
                    if (session != null && session.CurrentItem?.Type == BaseItemDto_Type.Episode)
                    {
                        session.EpisodesWatched++;
                        if (!string.IsNullOrEmpty(session.CurrentItem.Id?.ToString()))
                        {
                            session.WatchedEpisodeIds.Add(session.CurrentItem.Id.ToString());
                        }

                        session.IsMultiEpisodeSession = session.EpisodesWatched > 1;
                        _navigationStateService.SavePlaybackSession(session);
                    }

                    GoBack();
                }
            }, Dispatcher, Logger);
        }

        private async void OnSeekCompleted(MediaPlayer sender, object args)
        {
            // Ensure logging happens on UI thread to avoid threading issues
            await UIHelper.RunOnUIThreadAsync(() =>
            {
                Logger.LogInformation("Seek completed");
            }, Dispatcher, Logger);
        }

        #endregion

        #region Skip Button Focus Management

        private void OnSkipButtonBecameAvailable(object sender, SkipSegmentType segmentType)
        {
            // Focus the appropriate skip button when it becomes available
            AsyncHelper.FireAndForget(async () => await UIHelper.RunOnUIThreadAsync(() =>
            {
                Button buttonToFocus = null;

                switch (segmentType)
                {
                    case SkipSegmentType.Intro:
                        if (SkipIntroButtonOverlay?.Visibility == Visibility.Visible)
                        {
                            buttonToFocus = SkipIntroButtonOverlay;
                        }

                        break;
                    case SkipSegmentType.Outro:
                        // For outro, check both skip outro and next episode buttons
                        if (NextEpisodeButtonOverlay?.Visibility == Visibility.Visible)
                        {
                            buttonToFocus = NextEpisodeButtonOverlay;
                        }
                        else if (SkipOutroButtonOverlay?.Visibility == Visibility.Visible)
                        {
                            buttonToFocus = SkipOutroButtonOverlay;
                        }

                        break;
                }

                // Focus the button in these scenarios:
                // 1. Controls are hidden (always focus skip buttons when they appear)
                // 2. Controls are visible but no control currently has focus
                if (buttonToFocus != null)
                {
                    var shouldFocus = false;

                    if (!CheckControlVisibility())
                    {
                        // Always focus when controls are hidden
                        shouldFocus = true;
                        Logger.LogInformation($"Will focus {buttonToFocus.Name} - controls are hidden");
                    }
                    else
                    {
                        // When controls are visible, only focus if nothing else has focus
                        var focusedElement = FocusManager.GetFocusedElement() as Control;
                        if (focusedElement == null || focusedElement == MediaPlayer)
                        {
                            shouldFocus = true;
                            Logger.LogInformation($"Will focus {buttonToFocus.Name} - no other control has focus");
                        }
                    }

                    if (shouldFocus)
                    {
                        buttonToFocus.Focus(FocusState.Programmatic);
                        Logger.LogInformation($"Focused {buttonToFocus.Name} when it became available");
                    }
                }
            }, Dispatcher, Logger));
        }

        private void SkipButton_Loaded(object sender, RoutedEventArgs e)
        {
            // When a skip button becomes visible (loaded), focus it if video is playing and controls are hidden
            if (sender is Button button && button.Visibility == Visibility.Visible && !ViewModel.IsPaused)
            {
                // Small delay to ensure button is fully rendered and layout has stabilized
                AsyncHelper.FireAndForget(async () => await UIHelper.RunOnUIThreadAsync(async () =>
                {
                    // Wait a bit to ensure button is fully rendered
                    await Task.Delay(100);

                    // Only focus if:
                    // 1. Button is still visible
                    // 2. Video is still playing
                    // 3. Media controls are hidden (so we don't steal focus from control buttons)
                    if (button.Visibility == Visibility.Visible &&
                        !ViewModel.IsPaused &&
                        !CheckControlVisibility())
                    {
                        button.Focus(FocusState.Programmatic);
                        Logger.LogInformation($"Focused {button.Name} when it became visible (controls hidden)");
                    }
                    else if (button.Visibility == Visibility.Visible && CheckControlVisibility())
                    {
                        Logger.LogInformation($"{button.Name} became visible but controls are shown - not focusing");
                    }
                }, Dispatcher, Logger));
            }
        }

        #endregion

        #region Control Visibility Management

        private void InitializeControlVisibilityTimer()
        {
            _controlVisibilityTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100) // Check every 100ms
            };
            _controlVisibilityTimer.Tick += OnControlVisibilityTimerTick;
        }


        private void ShowControls(bool skipFocus = false, bool updateMediaController = true,
            bool clearSkipFlagAfterDelay = false)
        {
            try
            {
                // Defensive check for disposal
                if (_isDisposing == 1 || CustomControlsOverlay == null || InfoOverlay == null)
                {
                    Logger?.LogWarning("Cannot show controls - page is being disposed or controls are null");
                    return;
                }

                // Show controls instantly
                CustomControlsOverlay.Visibility = Visibility.Visible;
                CustomControlsOverlay.Opacity = 1.0;
                InfoOverlay.Visibility = Visibility.Visible;
                InfoOverlay.Opacity = 1.0;

                Logger?.LogInformation(
                    $"Showing controls (skipFocus={skipFocus}, updateMediaController={updateMediaController}, clearSkipFlagAfterDelay={clearSkipFlagAfterDelay})"); ViewModel.AreControlsVisible = true;

                // Enable XY focus navigation for analog stick when controls are visible
                XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;

                // Notify MediaControllerService if requested
                if (updateMediaController && ViewModel != null)
                {
                    ViewModel.SetControlsVisible(true);
                    Logger?.LogInformation("Notified MediaControllerService of visibility change to true");
                }

                // Reset the timer for auto-hide
                ResetControlVisibilityTimer();

                // Clear skip flag after delay if requested (for brief skip feedback)
                if (clearSkipFlagAfterDelay)
                {
                    var clearSkipFlagTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    clearSkipFlagTimer.Tick += (s, e) =>
                    {
                        clearSkipFlagTimer.Stop();
                        ViewModel?.ClearSkipFlag();
                        Logger?.LogInformation("Cleared skip flag after showing controls briefly");
                    };
                    clearSkipFlagTimer.Start();
                }

                // Set focus to allow navigation (unless skipFocus is true)
                if (!skipFocus)
                {
                    // When paused, prioritize focus on custom play/pause button
                    if (ViewModel.IsPaused && CustomPlayPauseButton != null)
                    {
                        CustomPlayPauseButton.Focus(FocusState.Programmatic);
                        Logger?.LogInformation("Set focus to custom play/pause button");
                    }
                    // Otherwise focus on first available button
                    else if (ViewModel.AreControlsVisible)
                    {
                        var firstButton = GetFirstFocusableButton();
                        if (firstButton != null)
                        {
                            firstButton.Focus(FocusState.Programmatic);
                            Logger?.LogInformation($"Set focus to {firstButton.Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ShowControls");
                // Fallback: show without focus management
                CustomControlsOverlay.Visibility = Visibility.Visible;
                InfoOverlay.Visibility = Visibility.Visible;
                ResetControlVisibilityTimer();
            }
        }


        private void HideControls()
        {
            try
            {
                // Defensive check for disposal
                if (_isDisposing == 1 || CustomControlsOverlay == null || InfoOverlay == null)
                {
                    Logger?.LogWarning("Cannot hide controls - page is being disposed or controls are null");
                    return;
                }

                // Hide controls instantly
                CustomControlsOverlay.Visibility = Visibility.Collapsed;
                InfoOverlay.Visibility = Visibility.Collapsed;

                Logger?.LogInformation("Hiding controls"); ViewModel.AreControlsVisible = false;

                // Disable XY focus navigation when controls are hidden to prevent analog stick navigation
                XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Disabled;

                // Notify MediaControllerService
                if (ViewModel != null)
                {
                    ViewModel.SetControlsVisible(false);
                    Logger?.LogInformation("Notified MediaControllerService of visibility change to false");
                }

                // Check for visible overlay buttons and set focus appropriately
                Button visibleOverlayButton = null;

                // Priority order for overlay buttons
                if (SkipIntroButtonOverlay?.Visibility == Visibility.Visible)
                {
                    visibleOverlayButton = SkipIntroButtonOverlay;
                }
                else if (SkipOutroButtonOverlay?.Visibility == Visibility.Visible)
                {
                    visibleOverlayButton = SkipOutroButtonOverlay;
                }
                else if (NextEpisodeButtonOverlay?.Visibility == Visibility.Visible)
                {
                    visibleOverlayButton = NextEpisodeButtonOverlay;
                }

                if (visibleOverlayButton != null)
                {
                    // Set focus to the visible overlay button
                    var focusResult = visibleOverlayButton.Focus(FocusState.Programmatic);
                    Logger?.LogInformation($"Controls hidden, set focus to overlay button {visibleOverlayButton.Name}: {focusResult}");
                }
                else
                {
                    // No overlay buttons visible, safe to move focus to MediaPlayer
                    var focusResult = MediaPlayer?.Focus(FocusState.Programmatic);
                    Logger?.LogInformation($"Controls hidden, MediaPlayer focus result: {focusResult}");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in HideControlsNew");
                // Fallback: hide without focus management
                CustomControlsOverlay.Visibility = Visibility.Collapsed;
                InfoOverlay.Visibility = Visibility.Collapsed;
            }
        }


        /// <summary>
        ///     Unified control visibility management
        /// </summary>
        private bool CheckControlVisibility(ControlVisibilityCheck check = ControlVisibilityCheck.AreVisible)
        {
            switch (check)
            {
                case ControlVisibilityCheck.AreVisible:
                    return CustomControlsOverlay?.Visibility == Visibility.Visible;
                case ControlVisibilityCheck.AreFlyoutsOpen:
                    return SubtitlesFlyout?.IsOpen == true || AudioFlyout?.IsOpen == true;
                case ControlVisibilityCheck.ShouldAutoHide:
                    return !CheckControlVisibility(ControlVisibilityCheck.AreFlyoutsOpen) &&
                           CheckControlVisibility();
                default:
                    return false;
            }
        }

        private enum ControlVisibilityCheck
        {
            AreVisible,
            AreFlyoutsOpen,
            ShouldAutoHide
        }

        private Control GetFirstFocusableButton()
        {
            // Priority order: Play/Pause -> Skip buttons -> Episodes/Next -> Subtitles -> Audio -> Quality -> Stats -> Favorite
            if (CustomPlayPauseButton != null && CustomPlayPauseButton.Visibility == Visibility.Visible &&
                CustomPlayPauseButton.IsEnabled)
            {
                return CustomPlayPauseButton;
            }

            if (CustomSkipBackButton != null && CustomSkipBackButton.Visibility == Visibility.Visible &&
                CustomSkipBackButton.IsEnabled)
            {
                return CustomSkipBackButton;
            }

            if (EpisodesButton != null && EpisodesButton.Visibility == Visibility.Visible && EpisodesButton.IsEnabled)
            {
                return EpisodesButton;
            }

            if (SubtitlesButton != null && SubtitlesButton.Visibility == Visibility.Visible &&
                SubtitlesButton.IsEnabled)
            {
                return SubtitlesButton;
            }

            if (AudioButton != null && AudioButton.Visibility == Visibility.Visible && AudioButton.IsEnabled)
            {
                return AudioButton;
            }

            if (StatsButton != null && StatsButton.Visibility == Visibility.Visible && StatsButton.IsEnabled)
            {
                return StatsButton;
            }

            if (FavoriteButton != null && FavoriteButton.Visibility == Visibility.Visible && FavoriteButton.IsEnabled)
            {
                return FavoriteButton;
            }

            return null;
        }


        private void ResetControlVisibilityTimer()
        {
            Interlocked.Exchange(ref _controlVisibilityCounter, 0);
            _controlVisibilityTimer.Stop();
            _controlVisibilityTimer.Start();
        }

        private async void OnControlVisibilityTimerTick(object sender, object e)
        {
            // Auto-hide controls after user-configured delay
            if (CheckControlVisibility() &&
                MediaPlayer?.MediaPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing)
            {
                Interlocked.Increment(ref _controlVisibilityCounter);

                // Get user preferences for hide delay
                var preferencesService = App.Current.Services.GetRequiredService<IPreferencesService>();
                var preferences = await preferencesService.GetAppPreferencesAsync();

                // Check if configured delay has passed
                // Timer ticks every 100ms, so divide seconds by 0.1 to get tick count
                var hideDelayTicks = (preferences?.ControlsHideDelay ?? 5) * 10; // Default to 5 seconds

                if (_controlVisibilityCounter >= hideDelayTicks)
                {
                    // Only hide if no flyouts are open
                    if (!CheckControlVisibility(ControlVisibilityCheck.AreFlyoutsOpen))
                    {
                        Logger?.LogInformation(
                            $"Auto-hiding controls after {preferences?.ControlsHideDelay ?? 5} seconds of inactivity");
                        HideControls();
                        Interlocked.Exchange(ref _controlVisibilityCounter, 0);
                    }
                    else
                    {
                        // Reset counter if flyouts are open
                        Interlocked.Exchange(ref _controlVisibilityCounter, 0);
                    }
                }
            }
            else
            {
                // Reset counter if not playing or controls are hidden
                Interlocked.Exchange(ref _controlVisibilityCounter, 0);
            }
        }

        #endregion

        #region UI Event Handlers

        private void SubtitlesButton_Click(object sender, RoutedEventArgs e)
        {
            SubtitlesFlyout.ShowAt(sender as FrameworkElement);
        }

        private void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            AudioFlyout.ShowAt(sender as FrameworkElement);
        }

        private async void AudioList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is AudioTrack selectedAudio)
            {
                Logger?.LogInformation($"AudioList_ItemClick - Selected audio: {selectedAudio.DisplayName}");

                // Debug logging
                Logger?.LogInformation($"ViewModel is null: {ViewModel == null}");
                Logger?.LogInformation($"ChangeAudioTrackCommand is null: {ViewModel?.ChangeAudioTrackCommand == null}");

                // Execute the ChangeAudioTrack command
                if (ViewModel?.ChangeAudioTrackCommand != null)
                {
                    var canExecute = ViewModel.ChangeAudioTrackCommand.CanExecute(selectedAudio);
                    Logger?.LogInformation($"ChangeAudioTrackCommand.CanExecute returned: {canExecute}");

                    if (canExecute)
                    {
                        await ViewModel.ChangeAudioTrackCommand.ExecuteAsync(selectedAudio);

                        // Close the flyout after selection
                        AudioFlyout.Hide();
                    }
                }
                else
                {
                    Logger?.LogError("Cannot execute audio track change - ViewModel or Command is null");
                }
            }
        }

        private async void SubtitlesList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SubtitleTrack selectedSubtitle)
            {
                Logger?.LogInformation($"SubtitlesList_ItemClick - Selected subtitle: {selectedSubtitle.DisplayTitle}");

                // Debug logging
                Logger?.LogInformation($"ViewModel is null: {ViewModel == null}");
                Logger?.LogInformation($"ChangeSubtitleCommand is null: {ViewModel?.ChangeSubtitleCommand == null}");

                // Execute the ChangeSubtitle command
                if (ViewModel?.ChangeSubtitleCommand != null)
                {
                    var canExecute = ViewModel.ChangeSubtitleCommand.CanExecute(selectedSubtitle);
                    Logger?.LogInformation($"ChangeSubtitleCommand.CanExecute returned: {canExecute}");

                    if (canExecute)
                    {
                        await ViewModel.ChangeSubtitleCommand.ExecuteAsync(selectedSubtitle);

                        // Close the flyout after selection
                        SubtitlesFlyout.Hide();
                    }
                }
                else
                {
                    Logger?.LogError("Cannot execute subtitle change - ViewModel or Command is null");
                }
            }
        }

        private void EpisodesButton_Click(object sender, RoutedEventArgs e)
        {
            // Navigate to episodes list
            if (ViewModel.CurrentItem?.SeriesId != null && ViewModel.CurrentItem.Type == BaseItemDto_Type.Episode)
            {
                // Create a navigation parameter that includes the episode and a flag indicating this came from Episodes button
                var navigationParam = new EpisodeNavigationParameter
                {
                    Episode = ViewModel.CurrentItem,
                    FromEpisodesButton = true,
                    OriginalSourcePage = ViewModel.NavigationSourcePage,
                    OriginalSourceParameter = ViewModel.NavigationSourceParameter
                };

                Logger?.LogInformation(
                    $"EpisodesButton_Click - CurrentItem: {ViewModel.CurrentItem?.Name} (Type: {ViewModel.CurrentItem?.Type})");
                Logger?.LogInformation($"EpisodesButton_Click - CurrentItem ID: {ViewModel.CurrentItem?.Id}");
                Logger?.LogInformation(
                    $"EpisodesButton_Click - CurrentItem SeasonId: {ViewModel.CurrentItem?.SeasonId}");
                Logger?.LogInformation(
                    $"EpisodesButton_Click - OriginalSourcePage: {ViewModel.NavigationSourcePage?.Name}");

                // Store the current back stack count before navigation
                var backStackCountBefore = Frame?.BackStack?.Count ?? 0;

                // Navigate to SeasonDetailsPage
                if (NavigationService.Navigate(typeof(SeasonDetailsPage), navigationParam))
                {
                    // After successful navigation, MediaPlayerPage will be at the top of the back stack
                    // Remove it so Back from SeasonDetailsPage goes to the page before MediaPlayerPage
                    if (Frame?.BackStack?.Count > backStackCountBefore)
                    {
                        // The navigation added an entry, remove the MediaPlayerPage entry
                        for (int i = Frame.BackStack.Count - 1; i >= 0; i--)
                        {
                            if (Frame?.BackStack?[i].SourcePageType == typeof(MediaPlayerPage))
                            {
                                Frame.BackStack.RemoveAt(i);
                                Logger?.LogInformation($"Removed MediaPlayerPage from back stack at index {i} after navigating to SeasonDetailsPage");
                                break; // Only remove the most recent MediaPlayerPage entry
                            }
                        }
                    }
                }
            }
            else
            {
                Logger?.LogWarning(
                    "EpisodesButton_Click - Cannot navigate: CurrentItem is not an episode or has no SeriesId");
            }
        }

        // Quality selection not implemented in this version
        private async void QualityList_ItemClick(object sender, ItemClickEventArgs e)
        {
            // Not implemented in this version
            await Task.CompletedTask;
        }

        private async void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Toggle favorite status
                if (ViewModel.CurrentItem?.Id != null)
                {
                    var userDataService = App.Current.Services.GetRequiredService<IUserDataService>();
                    var isFavorite = ViewModel.CurrentItem.UserData?.IsFavorite ?? false;
                    var itemId = ViewModel.CurrentItem.Id.Value;

                    // Use UserDataService to toggle favorite
                    var updatedData = await userDataService.ToggleFavoriteAsync(itemId, !isFavorite); if (updatedData != null)
                    {
                        ViewModel.CurrentItem.UserData = updatedData;
                    }
                    else
                    {
                        // Fallback if service didn't return data
                        if (ViewModel.CurrentItem.UserData == null)
                        {
                            ViewModel.CurrentItem.UserData = new UserItemDataDto();
                        }

                        ViewModel.CurrentItem.UserData.IsFavorite = !isFavorite;
                    }

                    // Force UI update by reassigning
                    var temp = ViewModel.CurrentItem;
                    ViewModel.CurrentItem = null;
                    ViewModel.CurrentItem = temp;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to toggle favorite");
            }
        }


        private void SeekSlider_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            // Show seek thumb when user starts seeking
            SeekThumb.Visibility = Visibility.Visible;
        }

        #endregion

        #region Focus Navigation

        private void ConfigureControlButtonsFocusNavigation()
        {
            // Prevent focus from moving down from any control button to MediaPlayer
            if (ControlButtonsGrid != null)
            {
                // Find all buttons in the control grid using recursive search
                FindAndConfigureButtons(ControlButtonsGrid);
            }
        }

        private void FindAndConfigureButtons(DependencyObject parent)
        {
            var childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                // If it's a button, configure its focus navigation
                if (child is Button button)
                {
                    // Set XYFocusDown to the button itself to prevent downward navigation
                    button.XYFocusDown = button;
                    Logger?.LogDebug($"Set XYFocusDown on {button.Name ?? "unnamed button"} to prevent downward navigation");
                }

                // Recursively search children
                FindAndConfigureButtons(child);
            }
        }

        #endregion

        #region Controller Input Override

        private async void MediaPlayerPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Log all key presses for debugging
            Logger?.LogInformation(
                $"MediaPlayerPage_KeyDown: Key={e.Key}, OriginalSource={e.OriginalSource?.GetType().Name}");

            // Defensive check for disposal
            if (_isDisposing == 1 || ViewModel == null)
            {
                Logger?.LogWarning($"Ignoring key {e.Key} - disposing: {_isDisposing == 1}");
                return;
            }

            // Reset control visibility timer on any input while controls are visible
            // This keeps controls visible while user is actively navigating
            if (CheckControlVisibility())
            {
                ResetControlVisibilityTimer();
            }

            // Delegate all input handling to MediaControllerService
            // Use the same instance that the ViewModel is subscribed to
            var mediaControllerService = ViewModel?.GetMediaControllerService();
            if (mediaControllerService != null)
            {
                var handled = await mediaControllerService.HandleKeyDownAsync(e.Key);
                e.Handled = handled;
                Logger?.LogInformation($"MediaControllerService handled key {e.Key}: {handled}");
            }
            else
            {
                Logger?.LogError("MediaControllerService is null or ViewModel not available!");
            }
        }

        /// <summary>
        ///     Ensure controls are visible (show if not already visible)
        /// </summary>
        private void EnsureControlsVisible(bool animate = true, bool skipFocus = false)
        {
            if (!CheckControlVisibility())
            {
                ShowControls(skipFocus);
            }
        }

        #endregion

        #region ViewModel Property Changes

        private void OnToggleControlsRequested(object sender, EventArgs e)
        {
            // Check if this is a skip action
            if (ViewModel?.LastActionWasSkip == true)
            {
                // For all skip actions, show controls briefly without stealing focus
                Logger?.LogInformation("Skip action detected - showing controls briefly");
                // Show controls briefly for skip actions
                ShowControls(true, false, true);
            }
            else
            {
                // Normal toggle behavior
                if (CheckControlVisibility())
                {
                    HideControls();
                }
                else
                {
                    ShowControls();
                }
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Handle any UI-specific updates based on ViewModel changes
            switch (e.PropertyName)
            {
                case nameof(ViewModel.IsBuffering):
                    Logger?.LogInformation($"IsBuffering property changed to: {ViewModel.IsBuffering}");
                    if (ViewModel.IsBuffering)
                    {
                        // Check if we're in the middle of a skip action
                        if (ViewModel?.LastActionWasSkip == true)
                        {
                            // Show controls without focus for skip actions
                            ShowControls(true);
                        }
                        else
                        {
                            ShowControls();
                        }

                        _controlVisibilityTimer.Stop();
                    }
                    else
                    {
                        // Buffering ended - ensure controls can be hidden again
                        Logger?.LogInformation("Buffering ended, resetting control visibility timer");
                        ResetControlVisibilityTimer();

                        // Also ensure focus is properly managed after buffering
                        if (!CheckControlVisibility())
                        {
                            var focusResult = MediaPlayer?.Focus(FocusState.Programmatic);
                            Logger?.LogInformation($"Reset focus after buffering ended: {focusResult}");
                        }
                    }

                    break;

                case nameof(ViewModel.IsError):
                    if (ViewModel.IsError)
                    {
                        ShowError(ViewModel.ErrorMessage);
                    }

                    break;

                case nameof(ViewModel.IsPlaying):
                    if (ViewModel.IsPlaying)
                    {
                        Logger.LogInformation("Playback started - starting control visibility timer");
                        // Start the auto-hide timer now that playback has actually begun
                        ResetControlVisibilityTimer();
                    }
                    else
                    {
                        // Stop the timer when not playing
                        _controlVisibilityTimer?.Stop();
                    }

                    break;

                case nameof(ViewModel.IsPaused):
                    Logger.LogInformation($"IsPaused property changed to: {ViewModel.IsPaused}");
                    if (ViewModel.IsPaused)
                    {
                        // Show controls immediately when paused (no animation for instant feedback)
                        Logger.LogInformation("Showing controls because video is paused");
                        ShowControls();

                        // Focus on play/pause button when paused, unless last action was skip
                        if (!ViewModel.LastActionWasSkip)
                        {
                            var currentFocus = FocusManager.GetFocusedElement() as FrameworkElement;
                            if (!(currentFocus is Button || currentFocus is Slider))
                            {
                                CustomPlayPauseButton?.Focus(FocusState.Programmatic);
                                Logger.LogInformation("Set focus to play/pause button on pause");
                            }
                        }
                    }

                    break;
            }
        }

        #endregion

        #region Window Event Handlers

        private async void Window_Activated(object sender, WindowActivatedEventArgs e)
        {
            try
            {
                if (MediaPlayer?.MediaPlayer?.PlaybackSession == null)
                {
                    return;
                }

                // Get user preferences
                var preferencesService = App.Current.Services.GetRequiredService<IPreferencesService>();
                var preferences = await preferencesService.GetAppPreferencesAsync();
                var pauseOnFocusLoss = preferences?.PauseOnFocusLoss ?? false;

                if (e.WindowActivationState == CoreWindowActivationState.Deactivated)
                {
                    // App lost focus (e.g., Xbox guide opened)
                    if (MediaPlayer?.MediaPlayer?.PlaybackSession == null)
                    {
                        return;
                    }
                    var currentState = MediaPlayer.MediaPlayer.PlaybackSession.PlaybackState;
                    Logger?.LogInformation($"Window deactivated, current playback state: {currentState}");

                    if (pauseOnFocusLoss && currentState == MediaPlaybackState.Playing)
                    {
                        // Remember that we were playing
                        _stateBeforeFocusLost = currentState;
                        MediaPlayer.MediaPlayer.Pause();
                        Logger?.LogInformation("Paused video due to window deactivation (PauseOnFocusLoss is enabled)");
                    }
                    else if (!pauseOnFocusLoss && currentState == MediaPlaybackState.Playing)
                    {
                        Logger?.LogInformation(
                            "Window deactivated but continuing playback (PauseOnFocusLoss is disabled)");
                    }
                }
                else
                {
                    // App regained focus
                    Logger?.LogInformation($"Window activated, state before focus lost: {_stateBeforeFocusLost}");

                    if (pauseOnFocusLoss && _stateBeforeFocusLost == MediaPlaybackState.Playing)
                    {
                        // Resume playback only if we paused it
                        MediaPlayer.MediaPlayer.Play();
                        Logger?.LogInformation("Resumed video after window activation");
                    }

                    _stateBeforeFocusLost = null;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in Window_Activated handler");
            }
        }

        private void Window_VisibilityChanged(object sender, VisibilityChangedEventArgs e)
        {
            try
            {
                if (MediaPlayer?.MediaPlayer?.PlaybackSession == null)
                {
                    return;
                }

                Logger?.LogInformation($"Window visibility changed: Visible={e.Visible}");

                if (!e.Visible)
                {
                    // Window is being hidden/minimized
                    if (MediaPlayer?.MediaPlayer?.PlaybackSession == null)
                    {
                        return;
                    }
                    var currentState = MediaPlayer.MediaPlayer.PlaybackSession.PlaybackState;

                    if (currentState == MediaPlaybackState.Playing)
                    {
                        _stateBeforeFocusLost = currentState;
                        MediaPlayer.MediaPlayer.Pause();
                        Logger?.LogInformation("Paused video due to window becoming hidden");
                    }
                }
                else if (_stateBeforeFocusLost == MediaPlaybackState.Playing)
                {
                    // Window is visible again and we were playing before
                    MediaPlayer.MediaPlayer.Play();
                    Logger?.LogInformation("Resumed video after window became visible");
                    _stateBeforeFocusLost = null;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in Window_VisibilityChanged handler");
            }
        }

        #endregion

        #region Helper Methods

        #endregion

        private async Task ShowHlsCorruptionError()
        {
            try
            {
                var dialogService = GetService<IDialogService>();
                if (dialogService != null)
                {
                    var result = await dialogService.ShowConfirmationAsync(
                        "Playback Error",
                        "The video stream became corrupted at this position. This is a known issue when resuming certain videos. " +
                        "Would you like to restart playback from the beginning?");

                    if (result == true && ViewModel != null)
                    {
                        // Restart playback from the beginning
                        Logger?.LogInformation("User chose to restart playback after HLS corruption");

                        // Clear resume position and restart
                        await ViewModel.StopPlayback();

                        // Start fresh playback without resume position
                        var playbackParams = new MediaPlaybackParams
                        {
                            Item = ViewModel.CurrentItem,
                            ItemId = ViewModel.CurrentItem?.Id?.ToString(),
                            StartPositionTicks = 0, // Start from beginning
                            NavigationSourceParameter = ViewModel.NavigationSourceParameter
                        };

                        await ViewModel.InitializeAsync(playbackParams);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Failed to show HLS corruption error dialog");
            }
        }
    }
}
