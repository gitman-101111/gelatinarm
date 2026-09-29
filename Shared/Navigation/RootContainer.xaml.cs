using System;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Gelatinarm.Music;

namespace Gelatinarm.Shared.Navigation
{
    // The window's content: the page frame, with the mini player under it on every page
    public sealed partial class RootContainer : UserControl
    {
        private const int TriggerHoldDelayMs = 500;
        private readonly DispatcherTimer _rightTriggerHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(TriggerHoldDelayMs) };
        private bool _isRightTriggerDown;

        public RootContainer()
        {
            InitializeComponent();
            PreviewKeyDown += OnPreviewKeyDown;
            PreviewKeyUp += OnPreviewKeyUp;
            _rightTriggerHoldTimer.Tick += OnRightTriggerHoldTimerTick;
        }

        private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.GamepadRightTrigger && !_isRightTriggerDown)
            {
                _isRightTriggerDown = true;
                _rightTriggerHoldTimer.Start();
            }
            else if (_isRightTriggerDown && e.Key != VirtualKey.GamepadRightTrigger)
            {
                CancelRightTriggerHold();
            }
        }

        private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.GamepadRightTrigger && _isRightTriggerDown)
            {
                CancelRightTriggerHold();
            }
        }

        private void OnRightTriggerHoldTimerTick(object sender, object e)
        {
            _rightTriggerHoldTimer.Stop();

            // Holding the right trigger jumps to the mini player, when one is showing
            MusicPlayer.FocusPlayPauseButton();
        }

        private void CancelRightTriggerHold()
        {
            _isRightTriggerDown = false;
            _rightTriggerHoldTimer.Stop();
        }

        public Frame MainFrame => ContentFrame;
    }
}
