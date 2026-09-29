using System;
using System.Threading;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public sealed partial class MediaPlayerPage
    {
        private void OnSkipButtonBecameAvailable(object sender, SkipSegmentType segmentType)
        {
            FireAndForget(() => UiHelper.RunOnUIThreadAsync(() =>
            {
                Button buttonToFocus = null;

                switch (segmentType)
                {
                    case SkipSegmentType.Intro:
                        if (SkipIntroButtonOverlay.Visibility == Visibility.Visible)
                        {
                            buttonToFocus = SkipIntroButtonOverlay;
                        }

                        break;
                    case SkipSegmentType.Outro:
                        if (NextEpisodeButtonOverlay.Visibility == Visibility.Visible)
                        {
                            buttonToFocus = NextEpisodeButtonOverlay;
                        }
                        else if (SkipOutroButtonOverlay.Visibility == Visibility.Visible)
                        {
                            buttonToFocus = SkipOutroButtonOverlay;
                        }

                        break;
                }

                if (buttonToFocus != null)
                {
                    var shouldFocus = false;

                    if (!CheckControlVisibility())
                    {
                        shouldFocus = true;
                        Logger.LogDebug("Will focus {ButtonToFocusName} - controls are hidden", buttonToFocus.Name);
                    }
                    else if (FocusManager.GetFocusedElement() is not Control focusedElement || focusedElement == MediaPlayer)
                    {
                        shouldFocus = true;
                        Logger.LogDebug("Will focus {ButtonToFocusName} - no other control has focus", buttonToFocus.Name);
                    }

                    if (shouldFocus)
                    {
                        buttonToFocus.Focus(FocusState.Programmatic);
                        Logger.LogDebug("Focused {ButtonToFocusName} when it became available", buttonToFocus.Name);
                    }
                }
            }, Logger, Dispatcher));
        }

        private void SkipButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Visibility == Visibility.Visible && !ViewModel.IsPaused)
            {
                // Once the button has laid out; it takes no focus before
                FireAndForget(() => UiHelper.RunOnUIThreadAsync(async () =>
                {
                    await UiHelper.WhenIdleAsync(Dispatcher);

                    // Not while the controls show: they would lose focus to it
                    if (button.Visibility == Visibility.Visible &&
                        !ViewModel.IsPaused &&
                        !CheckControlVisibility())
                    {
                        button.Focus(FocusState.Programmatic);
                        Logger.LogDebug("Focused {ButtonName} when it became visible (controls hidden)", button.Name);
                    }
                    else if (button.Visibility == Visibility.Visible && CheckControlVisibility())
                    {
                        Logger.LogDebug("{ButtonName} became visible but controls are shown - not focusing", button.Name);
                    }
                }, Logger, Dispatcher));
            }
        }

        private void InitializeControlVisibilityTimer()
        {
            _controlVisibilityTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _controlVisibilityTimer.Tick += OnControlVisibilityTimerTick;
        }

        // takeInput is false for skip feedback: the overlay shows, but the controller keeps
        // seeking and pausing instead of driving the overlay's buttons
        private void ShowControls(bool skipFocus = false, bool takeInput = true,
            bool clearSkipFlagAfterDelay = false)
        {
            try
            {
                if (!CanToggleControls("show"))
                {
                    return;
                }

                CustomControlsOverlay.Opacity = 1.0;
                InfoOverlay.Opacity = 1.0;

                Logger.LogDebug(
                    "Showing controls (skipFocus={SkipFocus}, takeInput={TakeInput}, clearSkipFlagAfterDelay={ClearSkipFlagAfterDelay})", skipFocus, takeInput, clearSkipFlagAfterDelay);
                ViewModel.AreControlsVisible = true;

                XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Enabled;

                if (takeInput)
                {
                    ViewModel.SetControlsTakeInput(true);
                }

                ResetControlVisibilityTimer();

                if (clearSkipFlagAfterDelay)
                {
                    var clearSkipFlagTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    clearSkipFlagTimer.Tick += (s, e) =>
                    {
                        clearSkipFlagTimer.Stop();
                        ViewModel.ClearSkipFlag();
                        Logger.LogDebug("Cleared skip flag after showing controls briefly");
                    };
                    clearSkipFlagTimer.Start();
                }

                if (!skipFocus)
                {
                    FocusPrimaryControlButton();
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("ShowControls", ErrorCategory.Media));
                ResetControlVisibilityTimer();
            }
        }

        private void HideControls()
        {
            try
            {
                if (!CanToggleControls("hide"))
                {
                    return;
                }

                Logger.LogDebug("Hiding controls");
                ViewModel.AreControlsVisible = false;

                // Disable XY focus navigation when controls are hidden to prevent analog stick navigation
                XYFocusKeyboardNavigation = XYFocusKeyboardNavigationMode.Disabled;

                ViewModel.SetControlsTakeInput(false);

                FocusOverlayOrPlayer();
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("HideControls", ErrorCategory.Media));
            }
        }

        private bool CheckControlVisibility(ControlVisibilityCheck check = ControlVisibilityCheck.AreVisible)
        {
            switch (check)
            {
                case ControlVisibilityCheck.AreVisible:
                    return ViewModel.AreControlsVisible;
                case ControlVisibilityCheck.AreFlyoutsOpen:
                    return SubtitlesFlyout.IsOpen || AudioFlyout.IsOpen;
                default:
                    return false;
            }
        }

        private enum ControlVisibilityCheck
        {
            AreVisible,
            AreFlyoutsOpen
        }

        private Control GetFirstFocusableButton()
        {
            if (CustomPlayPauseButton.Visibility == Visibility.Visible && CustomPlayPauseButton.IsEnabled)
            {
                return CustomPlayPauseButton;
            }

            if (CustomSkipBackButton.Visibility == Visibility.Visible && CustomSkipBackButton.IsEnabled)
            {
                return CustomSkipBackButton;
            }

            if (EpisodesButton.Visibility == Visibility.Visible && EpisodesButton.IsEnabled)
            {
                return EpisodesButton;
            }

            if (SubtitlesButton.Visibility == Visibility.Visible && SubtitlesButton.IsEnabled)
            {
                return SubtitlesButton;
            }

            if (AudioButton.Visibility == Visibility.Visible && AudioButton.IsEnabled)
            {
                return AudioButton;
            }

            if (StatsButton.Visibility == Visibility.Visible && StatsButton.IsEnabled)
            {
                return StatsButton;
            }

            if (FavoriteButton.Visibility == Visibility.Visible && FavoriteButton.IsEnabled)
            {
                return FavoriteButton;
            }

            return null;
        }

        private void FocusPrimaryControlButton()
        {
            if (ViewModel.IsPaused)
            {
                CustomPlayPauseButton.Focus(FocusState.Programmatic);
                Logger.LogDebug("Set focus to custom play/pause button");
                return;
            }

            if (ViewModel.AreControlsVisible)
            {
                var firstButton = GetFirstFocusableButton();
                if (firstButton != null)
                {
                    firstButton.Focus(FocusState.Programmatic);
                    Logger.LogDebug("Set focus to {FirstButtonName}", firstButton.Name);
                }
            }
        }

        private Button GetVisibleOverlayButton()
        {
            if (SkipIntroButtonOverlay.Visibility == Visibility.Visible)
            {
                return SkipIntroButtonOverlay;
            }

            if (SkipOutroButtonOverlay.Visibility == Visibility.Visible)
            {
                return SkipOutroButtonOverlay;
            }

            if (NextEpisodeButtonOverlay.Visibility == Visibility.Visible)
            {
                return NextEpisodeButtonOverlay;
            }

            return null;
        }

        private void FocusOverlayOrPlayer()
        {
            var visibleOverlayButton = GetVisibleOverlayButton();
            if (visibleOverlayButton != null)
            {
                var focusResult = visibleOverlayButton.Focus(FocusState.Programmatic);
                Logger.LogDebug(
                    "Controls hidden, set focus to overlay button {VisibleOverlayButtonName}: {FocusResult}", visibleOverlayButton.Name, focusResult);
                return;
            }

            var mediaFocusResult = MediaPlayer.Focus(FocusState.Programmatic);
            Logger.LogDebug("Controls hidden, MediaPlayer focus result: {MediaFocusResult}", mediaFocusResult);
        }

        private bool CanToggleControls(string action)
        {
            if (_isDisposing == 1)
            {
                Logger.LogWarning("Cannot {Action} controls - page is being disposed", action);
                return false;
            }

            return true;
        }

        private void ResetControlVisibilityTimer()
        {
            Interlocked.Exchange(ref _controlVisibilityCounter, 0);
            _controlVisibilityTimer.Stop();
            _controlVisibilityTimer.Start();
        }

        private async void OnControlVisibilityTimerTick(object sender, object e)
        {
            try
            {
                if (CheckControlVisibility() &&
                    MediaPlayer.MediaPlayer.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing)
                {
                    Interlocked.Increment(ref _controlVisibilityCounter);

                    var preferences = await PreferencesService.GetAppPreferencesAsync();

                    // The timer ticks every 100 ms
                    var hideDelaySeconds = preferences.ControlsHideDelay;
                    var hideDelayTicks = hideDelaySeconds * 10;

                    if (_controlVisibilityCounter >= hideDelayTicks)
                    {
                        if (!CheckControlVisibility(ControlVisibilityCheck.AreFlyoutsOpen))
                        {
                            Logger.LogDebug(
                                "Auto-hiding controls after {HideDelaySeconds} seconds of inactivity", hideDelaySeconds);
                            HideControls();
                            Interlocked.Exchange(ref _controlVisibilityCounter, 0);
                        }
                        else
                        {
                            Interlocked.Exchange(ref _controlVisibilityCounter, 0);
                        }
                    }
                }
                else
                {
                    Interlocked.Exchange(ref _controlVisibilityCounter, 0);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnControlVisibilityTimerTick", ErrorCategory.Media), false);
            }
        }

        private void ConfigureControlButtonsFocusNavigation()
        {
            // Prevent focus from moving down from any control button to MediaPlayer
            FindAndConfigureButtons(ControlButtonsGrid);
        }

        private void FindAndConfigureButtons(DependencyObject parent)
        {
            var childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);

                if (child is Button button)
                {
                    button.XYFocusDown = button;
                    Logger.LogDebug(
                        "Set XYFocusDown on {ButtonName} to prevent downward navigation", button.Name ?? "unnamed button");
                }

                FindAndConfigureButtons(child);
            }
        }

        private void OnToggleControlsRequested(object sender, EventArgs e)
        {
            if (ViewModel.LastActionWasSkip)
            {
                Logger.LogDebug("Skip action detected - showing controls briefly");
                ShowControls(true, false, true);
            }
            else
            {
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
    }
}
