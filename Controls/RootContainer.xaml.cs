using System;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Gelatinarm.Controls
{
    public sealed partial class RootContainer : BaseControl
    {
        private DispatcherTimer _rightTriggerHoldTimer;
        private bool _isRightTriggerDown;
        private const int TRIGGER_HOLD_DELAY_MS = 500;

        public RootContainer()
        {
            try
            {
                InitializeComponent();

                if (ContentFrame == null)
                {
                    throw new InvalidOperationException("ContentFrame is null after InitializeComponent");
                }


                // Setup right trigger hold detection
                InitializeTriggerHoldDetection();
            }
            catch (Exception)
            {
                throw;
            }

        }

        private void InitializeTriggerHoldDetection()
        {
            // Subscribe to key events
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;

            // Initialize the hold timer
            _rightTriggerHoldTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(TRIGGER_HOLD_DELAY_MS)
            };
            _rightTriggerHoldTimer.Tick += OnRightTriggerHoldTimerTick;
        }

        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            // Check for right trigger press
            if (e.Key == VirtualKey.GamepadRightTrigger && !_isRightTriggerDown)
            {
                _isRightTriggerDown = true;
                _rightTriggerHoldTimer.Start();
            }
            // Cancel hold detection if any other key is pressed
            else if (_isRightTriggerDown && e.Key != VirtualKey.GamepadRightTrigger)
            {
                CancelRightTriggerHold();
            }
        }

        private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
        {
            // Check for right trigger release
            if (e.Key == VirtualKey.GamepadRightTrigger && _isRightTriggerDown)
            {
                // If released before timer fires, it's a normal press - don't handle it
                if (_rightTriggerHoldTimer.IsEnabled)
                {
                }
                CancelRightTriggerHold();
            }
        }

        private void OnRightTriggerHoldTimerTick(object sender, object e)
        {
            _rightTriggerHoldTimer.Stop();

            // Jump focus to MusicPlayer
            if (MusicPlayer != null && MusicPlayer.Visibility == Visibility.Visible)
            {
                MusicPlayer.FocusPlayPauseButton();
            }
            else
            {
            }
        }

        private void CancelRightTriggerHold()
        {
            _isRightTriggerDown = false;
            _rightTriggerHoldTimer.Stop();
        }

        public Frame MainFrame
        {
            get
            {
                if (ContentFrame == null)
                {
                }

                return ContentFrame;
            }
        }

        // Navigation should be done through INavigationService, not directly on the Frame
        // These methods have been removed to enforce proper navigation patterns
    }
}
