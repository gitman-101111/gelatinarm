using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Gelatinarm.Music;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public sealed partial class MediaPlayerPage : BasePage
    {
        private readonly IMusicPlayerService _musicPlayerService;
        private volatile int _controlVisibilityCounter;
        private DispatcherTimer _controlVisibilityTimer;
        private volatile int _isDisposing; // 0 = not disposing, 1 = disposing
        private volatile bool _isInBackground;
        private MediaPlaybackState? _stateBeforeFocusLost;
        private DateTime _lastMediaFailureUtc = DateTime.MinValue;

        public MediaPlayerPage() : base(typeof(MediaPlayerPage))
        {
            InitializeComponent();
            _musicPlayerService = GetRequiredService<IMusicPlayerService>();

            ViewModel.MediaPlayerElement = MediaPlayer;

            ViewModel.ToggleControlsRequested += OnToggleControlsRequested;
            InitializeControlVisibilityTimer();

            KeyDown += MediaPlayerPage_KeyDown;
        }

        protected override Type ViewModelType => typeof(MediaPlayerViewModel);

        // Back leaves the way playback ends: to where it started, on the item now playing
        // (Next may have moved past the one the page below was showing). Before the view model
        // has an item, the queue service still holds the previous playback, so the Frame goes back.
        protected override bool HandleBackNavigation(BackRequestedEventArgs e)
        {
            if (ViewModel.CurrentItem == null)
            {
                return false;
            }

            e.Handled = true;
            FireAndForget(() => ViewModel.LeavePlayerAsync(), nameof(MediaPlayerViewModel.LeavePlayerAsync));
            return true;
        }

        public new MediaPlayerViewModel ViewModel => (MediaPlayerViewModel)base.ViewModel;

        private static readonly TimeSpan MediaFailureDuplicateWindow = TimeSpan.FromSeconds(2);

        private void ShowError(string message)
        {
            FireAndForget(() => DialogService.ShowMessageAsync("Playback Error", message));
        }

        private void DisposePlayback()
        {
            if (Interlocked.CompareExchange(ref _isDisposing, 1, 0) != 0)
            {
                return;
            }

            try
            {
                _controlVisibilityTimer?.Stop();
                ViewModel.Dispose();
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("Dispose", ErrorCategory.Media));
            }
        }

        protected override async Task InitializePageAsync(object parameter)
        {
            if (parameter is MediaPlaybackParams playbackParams)
            {
                // Paused music too: it cannot be heard, or resumed from the mini player, while a
                // video plays
                _musicPlayerService.Stop();

                await ViewModel.InitializeAsync(playbackParams);

                var appPrefs = await PreferencesService.GetAppPreferencesAsync();
                if (appPrefs != null)
                {
                    MediaPlayer.Stretch = appPrefs.VideoStretchMode == "UniformToFill"
                        ? Stretch.UniformToFill
                        : Stretch.Uniform;
                }

                // The control-hide timer starts in OnMediaOpened, once playback has really begun
            }
            else
            {
                Logger.LogError("Invalid navigation parameter");
                NavigationService.GoBack();
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            // At once, or the video plays on behind the next page
            try
            {
                ViewModel.StopPlayback();
                _controlVisibilityTimer?.Stop();
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("OnNavigatedFrom", ErrorCategory.Media));
            }

            // Started here, not on the pool: the report reads this playback's session, item and
            // position before its first await, and next-episode navigation initializes the next
            // playback on these same singletons as soon as this returns. The local keeps this
            // playback's view model for the continuation.
            var viewModel = ViewModel;
            var stopReport = viewModel.ReportPlaybackStoppedAsync();
            FireAndForget(async () =>
            {
                try
                {
                    await stopReport;
                    viewModel.CleanupRemaining();
                }
                catch (Exception ex)
                {
                    ErrorHandler.HandleError(ex, CreateErrorContext("OnNavigatedFrom", ErrorCategory.Media));
                }
            }, "ReportPlaybackStopped");

            base.OnNavigatedFrom(e);

            DisposePlayback();
        }

        protected override Task OnPageLoadedAsync()
        {
            CustomControlsOverlay.Opacity = 0;

            InfoOverlay.Opacity = 0;

            // The player element takes keyboard input while the controls are hidden
            var focusResult = MediaPlayer.Focus(FocusState.Programmatic);
            Logger.LogDebug("OnPageLoaded: MediaPlayer focus result: {FocusResult}", focusResult);

            ConfigureControlButtonsFocusNavigation();

            // Prevent analog stick navigation by disabling XY focus navigation when controls are hidden
            XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Disabled;

            if (MediaPlayer.MediaPlayer != null)
            {
                MediaPlayer.MediaPlayer.MediaFailed += OnMediaFailed;
                MediaPlayer.MediaPlayer.MediaEnded += OnMediaEnded;
            }

            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            ViewModel.SkipButtonBecameAvailable += OnSkipButtonBecameAvailable;

            Window.Current.Activated += Window_Activated;
            Window.Current.VisibilityChanged += Window_VisibilityChanged;

            return Task.CompletedTask;
        }

        protected override void OnPageUnloadedCore()
        {
            if (MediaPlayer.MediaPlayer != null)
            {
                MediaPlayer.MediaPlayer.MediaFailed -= OnMediaFailed;
                MediaPlayer.MediaPlayer.MediaEnded -= OnMediaEnded;
            }

            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.SkipButtonBecameAvailable -= OnSkipButtonBecameAvailable;
            ViewModel.ToggleControlsRequested -= OnToggleControlsRequested;

            KeyDown -= MediaPlayerPage_KeyDown;

            Window.Current.Activated -= Window_Activated;
            Window.Current.VisibilityChanged -= Window_VisibilityChanged;

            _controlVisibilityTimer?.Stop();

            DisposePlayback();
        }

        private async void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            try
            {
                // The view model logs the failure in detail. The player can raise MediaFailed twice
                // for one failure; acting on the second would open a second MessageDialog
                // (E_ACCESSDENIED while one is open) or, after a direct-play retry has started,
                // abandon the retry.
                var now = DateTime.UtcNow;
                if (now - _lastMediaFailureUtc < MediaFailureDuplicateWindow)
                {
                    return;
                }

                _lastMediaFailureUtc = now;

                if (ViewModel.TryRecoverFromMediaFailure())
                {
                    return;
                }

                await UiHelper.RunOnUIThreadAsync(async () =>
                {
                    await DialogService.ShowMessageAsync("Playback Error", $"Playback failed: {args.ErrorMessage}");

                    // The failed source cannot be resumed; staying would leave a frozen page
                    // that keeps reporting the same position to the server.
                    await ViewModel.LeaveAfterPlaybackFailureAsync();
                }, Logger, Dispatcher);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnMediaFailed", ErrorCategory.Media), false);
            }
        }

        private async void OnMediaEnded(MediaPlayer sender, object args)
        {
            try
            {
                var naturalDuration = sender.PlaybackSession?.NaturalDuration;
                var rawPosition = sender.PlaybackSession?.Position ?? TimeSpan.Zero;
                // Use ViewModel.Position which already includes HLS offset
                var currentPosition = ViewModel.Position;
                var metadataDuration = ViewModel.GetMetadataDuration();

                Logger.LogInformation("MediaEnded event - RawPosition: {RawPosition:hh\\:mm\\:ss}, " +
                                      "Position (with offset): {CurrentPosition:hh\\:mm\\:ss}, " +
                                      "NaturalDuration: {NaturalDuration:hh\\:mm\\:ss}, " +
                                      "MetadataDuration: {MetadataDuration:hh\\:mm\\:ss}", rawPosition, currentPosition, naturalDuration, metadataDuration);

                // A seek on an HLS stream can raise a false MediaEnded: act only near the end of the
                // item by its metadata length
                if (metadataDuration > TimeSpan.Zero)
                {
                    var percentComplete = currentPosition.TotalSeconds / metadataDuration.TotalSeconds * 100;
                    var percentOfNatural = naturalDuration > TimeSpan.Zero
                        ? rawPosition.TotalSeconds / naturalDuration.Value.TotalSeconds * 100 // Compare raw to raw!
                        : 0;

                    // HLS manifest corruption makes NaturalDuration impossibly short
                    var isHlsCorruption = naturalDuration < TimeSpan.FromMinutes(1) &&
                                          metadataDuration > TimeSpan.FromMinutes(5) &&
                                          rawPosition > naturalDuration; // Compare raw to raw!

                    if (percentComplete < 95)
                    {
                        Logger.LogWarning("Premature MediaEnded at {PercentComplete:F2}% of metadata duration " +
                                          "({PercentOfNatural:F2}% of natural duration): Position={CurrentPosition:hh\\:mm\\:ss}, " +
                                          "MetadataDuration={MetadataDuration:hh\\:mm\\:ss}, NaturalDuration={NaturalDuration:hh\\:mm\\:ss}",
                            percentComplete, percentOfNatural, currentPosition, metadataDuration, naturalDuration);

                        if (isHlsCorruption)
                        {
                            Logger.LogError(
                                "[HLS-CORRUPT] Detected HLS manifest corruption - natural duration is impossibly short");

                            FireAndForget(ShowHlsCorruptionErrorAsync, nameof(ShowHlsCorruptionErrorAsync));
                        }

                        return;
                    }
                }

                // Auto-play of the next item, if any, has already started by now
                if (!ViewModel.IsPlaying)
                {
                    await ViewModel.LeavePlayerAsync();
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnMediaEnded", ErrorCategory.Media), false);
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isInBackground &&
                (e.PropertyName == nameof(ViewModel.IsBuffering) ||
                 e.PropertyName == nameof(ViewModel.IsPaused) ||
                 e.PropertyName == nameof(ViewModel.IsPlaying)))
            {
                Logger.LogDebug("Skipping UI updates while in background for {EPropertyName}", e.PropertyName);
                return;
            }

            switch (e.PropertyName)
            {
                case nameof(ViewModel.IsBuffering):
                    Logger.LogDebug("IsBuffering property changed to: {ViewModelIsBuffering}", ViewModel.IsBuffering);
                    if (ViewModel.IsBuffering)
                    {
                        if (ViewModel.LastActionWasSkip)
                        {
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
                        Logger.LogDebug("Buffering ended, resetting control visibility timer");
                        ResetControlVisibilityTimer();

                        if (!CheckControlVisibility())
                        {
                            var focusResult = MediaPlayer.Focus(FocusState.Programmatic);
                            Logger.LogDebug("Reset focus after buffering ended: {FocusResult}", focusResult);
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
                        Logger.LogDebug("Playback started - starting control visibility timer");
                        ResetControlVisibilityTimer();
                    }
                    else
                    {
                        _controlVisibilityTimer?.Stop();
                    }

                    break;

                case nameof(ViewModel.IsPaused):
                    Logger.LogDebug("IsPaused property changed to: {ViewModelIsPaused}", ViewModel.IsPaused);
                    if (ViewModel.IsPaused)
                    {
                        Logger.LogDebug("Showing controls because video is paused");
                        ShowControls();

                        if (!ViewModel.LastActionWasSkip)
                        {
                            var currentFocus = FocusManager.GetFocusedElement() as FrameworkElement;
                            if (currentFocus is not Button)
                            {
                                CustomPlayPauseButton.Focus(FocusState.Programmatic);
                                Logger.LogDebug("Set focus to play/pause button on pause");
                            }
                        }
                    }

                    break;
            }
        }

        private async void Window_Activated(object sender, WindowActivatedEventArgs e)
        {
            try
            {
                if (MediaPlayer.MediaPlayer.PlaybackSession == null)
                {
                    return;
                }

                var preferences = await PreferencesService.GetAppPreferencesAsync();
                var pauseOnFocusLoss = preferences?.PauseOnFocusLoss == true;

                if (e.WindowActivationState == CoreWindowActivationState.Deactivated)
                {
                    // App lost focus (e.g., Xbox guide opened)
                    var currentState = MediaPlayer.MediaPlayer.PlaybackSession.PlaybackState;
                    Logger.LogDebug("Window deactivated, current playback state: {CurrentState}", currentState);

                    if (pauseOnFocusLoss && currentState == MediaPlaybackState.Playing)
                    {
                        _stateBeforeFocusLost = currentState;
                        MediaPlayer.MediaPlayer.Pause();
                        Logger.LogInformation("Paused video due to window deactivation (PauseOnFocusLoss is enabled)");
                    }
                    else if (!pauseOnFocusLoss && currentState == MediaPlaybackState.Playing)
                    {
                        Logger.LogDebug(
                            "Window deactivated but continuing playback (PauseOnFocusLoss is disabled)");
                    }
                }
                else
                {
                    Logger.LogDebug("Window activated, state before focus lost: {StateBeforeFocusLost}", _stateBeforeFocusLost);

                    if (pauseOnFocusLoss && _stateBeforeFocusLost == MediaPlaybackState.Playing)
                    {
                        // Resume playback only if we paused it
                        MediaPlayer.MediaPlayer.Play();
                        Logger.LogInformation("Resumed video after window activation");
                    }

                    _stateBeforeFocusLost = null;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("Window_Activated", ErrorCategory.Media), false);
            }
        }

        private void Window_VisibilityChanged(object sender, VisibilityChangedEventArgs e)
        {
            try
            {
                Logger.LogDebug(
                    "Window visibility changed: Visible={EVisible}, WasInBackground={IsInBackground}", e.Visible, _isInBackground);
                HandleAppBackgroundChanged(!e.Visible);
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("Window_VisibilityChanged", ErrorCategory.Media));
            }
        }

        public Task ReportPlaybackStoppedAsync()
        {
            return ViewModel.ReportPlaybackStoppedAsync();
        }

        // Window.VisibilityChanged is the one route here: App's background events do not call in
        private void HandleAppBackgroundChanged(bool isInBackground)
        {
            if (_isDisposing == 1 || _isInBackground == isInBackground)
            {
                return;
            }

            _isInBackground = isInBackground;
            Logger.LogDebug(
                "App background change: IsInBackground={IsInBackground}, PlaybackState={PlaybackState}",
                isInBackground, MediaPlayer.MediaPlayer.PlaybackSession?.PlaybackState);

            if (isInBackground)
            {
                Logger.LogDebug("App entered background - pausing playback and stopping timers");
                _controlVisibilityTimer?.Stop();
                ViewModel.HandleAppBackgroundChanged(true);

                if (MediaPlayer.MediaPlayer.PlaybackSession != null)
                {
                    var currentState = MediaPlayer.MediaPlayer.PlaybackSession.PlaybackState;
                    if (currentState == MediaPlaybackState.Playing)
                    {
                        _stateBeforeFocusLost = currentState;
                        MediaPlayer.MediaPlayer.Pause();
                        Logger.LogInformation("Paused video due to app entering background");
                    }
                }
            }
            else
            {
                Logger.LogDebug("App leaving background - resuming timers");
                ViewModel.HandleAppBackgroundChanged(false);

                if (_stateBeforeFocusLost == MediaPlaybackState.Playing)
                {
                    MediaPlayer.MediaPlayer.Play();
                    Logger.LogInformation("Resumed video after app returned to foreground");
                }

                _stateBeforeFocusLost = null;

                if (ViewModel.IsPlaying)
                {
                    ResetControlVisibilityTimer();
                }
            }
        }

        private async Task ShowHlsCorruptionErrorAsync()
        {
            try
            {
                var result = await DialogService.ShowConfirmationAsync(
                    "Playback Error",
                    "The video stream became corrupted at this position. This is a known issue when resuming certain videos. " +
                    "Would you like to restart playback from the beginning?");

                if (result)
                {
                    Logger.LogInformation("User chose to restart playback after HLS corruption");

                    await ViewModel.RestartStreamAsync("restart after HLS corruption", TimeSpan.Zero);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("ShowHlsCorruptionErrorAsync", ErrorCategory.Media), false);
            }
        }
    }
}
