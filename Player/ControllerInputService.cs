using System;
using Windows.System;
using Gelatinarm.Shared.Base;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public interface IControllerInputService : IDisposable
    {
        /// <summary>
        ///     Synchronous so the page can set KeyRoutedEventArgs.Handled before the event moves on
        /// </summary>
        bool HandleKeyDown(VirtualKey key);

        event EventHandler<MediaAction> ActionTriggered;

        event EventHandler<(MediaAction action, object parameter)> ActionWithParameterTriggered;

        /// <summary>
        ///     Whether the player controls, rather than playback, receive the controller's buttons.
        ///     Not the same as the controls being visible: skip feedback shows them without input
        /// </summary>
        void SetControlsTakeInput(bool takeInput);
    }

    public class ControllerInputService : BaseService, IControllerInputService
    {
        private bool _controlsTakeInput;
        private DateTimeOffset _lastSkipInputUtc = DateTimeOffset.MinValue;

        public ControllerInputService(ILogger<ControllerInputService> logger) : base(logger)
        {
        }

        public event EventHandler<MediaAction> ActionTriggered;
        public event EventHandler<(MediaAction action, object parameter)> ActionWithParameterTriggered;

        public void SetControlsTakeInput(bool takeInput)
        {
            _controlsTakeInput = takeInput;
            Logger.LogDebug("ControllerInputService: controls take input: {TakeInput}", takeInput);
        }

        public bool HandleKeyDown(VirtualKey key)
        {
            // The triggers skip whether or not the controls take input
            if (key == VirtualKey.GamepadLeftTrigger || key == VirtualKey.GamepadRightTrigger)
            {
                _lastSkipInputUtc = DateTimeOffset.UtcNow;
                var action = key == VirtualKey.GamepadLeftTrigger ? MediaAction.Rewind : MediaAction.FastForward;
                ActionWithParameterTriggered?.Invoke(this, (action, PlayerConstants.TriggerSkipSeconds));
                return true;
            }

            var mapped = GetActionForKey(key);
            if (mapped == null)
            {
                return false;
            }

            var isSkip = mapped == MediaAction.Rewind || mapped == MediaAction.FastForward;
            var now = DateTimeOffset.UtcNow;

            // While the controls take input, the keys the focused control needs go to it: A, Space
            // and Down (which opens a flyout). Stats only overlays; Up hides the controls; a skip
            // may continue for a second after the last one, so a run of presses is not cut short.
            if (_controlsTakeInput)
            {
                var recentSkip = now - _lastSkipInputUtc <= TimeSpan.FromSeconds(1);
                var passes = mapped == MediaAction.ShowStats ||
                             (mapped == MediaAction.ShowInfo && (key == VirtualKey.GamepadDPadUp || key == VirtualKey.Up)) ||
                             (isSkip && recentSkip);
                if (!passes)
                {
                    Logger.LogDebug("Blocking {Key} - controls are visible, UI will handle it", key);
                    return false;
                }
            }

            if (isSkip)
            {
                _lastSkipInputUtc = now;
            }

            ActionTriggered?.Invoke(this, mapped.Value);
            return true;
        }

        private static MediaAction? GetActionForKey(VirtualKey key)
        {
            return key switch
            {
                VirtualKey.GamepadA => MediaAction.PlayPause,
                VirtualKey.Space => MediaAction.PlayPause,
                VirtualKey.GamepadY => MediaAction.ShowStats,
                VirtualKey.GamepadDPadUp => MediaAction.ShowInfo,
                VirtualKey.Up => MediaAction.ShowInfo,
                VirtualKey.GamepadDPadDown => MediaAction.ShowInfo,
                VirtualKey.Down => MediaAction.ShowInfo,
                VirtualKey.GamepadDPadLeft => MediaAction.Rewind,
                VirtualKey.Left => MediaAction.Rewind,
                VirtualKey.GamepadDPadRight => MediaAction.FastForward,
                VirtualKey.Right => MediaAction.FastForward,
                _ => null
            };
        }
    }
}
