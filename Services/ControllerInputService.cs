using System;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.System;
using Gelatinarm.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Service for managing media controller input
    /// </summary>
    public class ControllerInputService : BaseService, IControllerInputService
    {
        private bool _areControlsVisible;
        private DateTimeOffset _lastSkipInputUtc = DateTimeOffset.MinValue;
        private MediaPlayer _mediaPlayer;

        public ControllerInputService(ILogger<ControllerInputService> logger) : base(logger)
        {
            IsEnabled = true;
        }

        public bool IsEnabled { get; private set; }

        public event EventHandler<MediaAction> ActionTriggered;
        public event EventHandler<(MediaAction action, object parameter)> ActionWithParameterTriggered;

        public Task InitializeAsync(MediaPlayer mediaPlayer)
        {
            _mediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer));

            // Reset control visibility state for new playback session
            _areControlsVisible = false;
            Logger.LogInformation("Reset controls visibility to false for new playback session");

            // Event-based input will be handled by subscribing to Page's KeyDown events
            Logger.LogInformation("ControllerInputService initialized - ready for event-based input");
            return Task.CompletedTask;
        }

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            Logger.LogInformation($"Controller input {(enabled ? "enabled" : "disabled")}");
        }

        public void SetControlsVisible(bool visible)
        {
            _areControlsVisible = visible;
            Logger.LogInformation($"ControllerInputService: Controls visibility updated to {visible}");
        }

        /// <summary>
        ///     Handles KeyDown events from the MediaPlayerPage
        /// </summary>
        public async Task<bool> HandleKeyDownAsync(VirtualKey key)
        {
            if (!IsEnabled)
            {
                return false;
            }

            // Special handling for B button - always goes back
            if (key == VirtualKey.GamepadB)
            {
                ActionTriggered?.Invoke(this, MediaAction.NavigateBack);
                return true;
            }

            // When controls are visible, only certain buttons should still work
            if (_areControlsVisible)
            {
                var now = DateTimeOffset.UtcNow;
                var allowSkipWhileVisible = now - _lastSkipInputUtc <= TimeSpan.FromSeconds(1);

                // Special case: triggers should work even with controls visible for quick skips
                if (key == VirtualKey.GamepadLeftTrigger)
                {
                    _lastSkipInputUtc = now;
                    ActionWithParameterTriggered?.Invoke(this, (MediaAction.Rewind, 600));
                    return true;
                }

                if (key == VirtualKey.GamepadRightTrigger)
                {
                    _lastSkipInputUtc = now;
                    ActionWithParameterTriggered?.Invoke(this, (MediaAction.FastForward, 600));
                    return true;
                }

                // Allow D-pad left/right to continue skipping briefly after a recent skip
                if (allowSkipWhileVisible &&
                    (key == VirtualKey.GamepadDPadLeft || key == VirtualKey.Left))
                {
                    _lastSkipInputUtc = now;
                    ActionTriggered?.Invoke(this, MediaAction.Rewind);
                    return true;
                }

                if (allowSkipWhileVisible &&
                    (key == VirtualKey.GamepadDPadRight || key == VirtualKey.Right))
                {
                    _lastSkipInputUtc = now;
                    ActionTriggered?.Invoke(this, MediaAction.FastForward);
                    return true;
                }

                // Y button (stats) should work as it doesn't take focus, just overlays stats
                if (key == VirtualKey.GamepadY)
                {
                    ActionTriggered?.Invoke(this, MediaAction.ShowStats);
                    return true;
                }

                // D-pad Up or regular Up key should hide controls when they're visible
                if (key == VirtualKey.GamepadDPadUp || key == VirtualKey.Up)
                {
                    ActionTriggered?.Invoke(this, MediaAction.ShowInfo);
                    return true;
                }

                // Block A button and other inputs to allow UI interaction with focused controls
                // D-pad Down is intentionally blocked here to allow flyout navigation
                Logger.LogDebug($"Blocking {key} - controls are visible, UI will handle it");
                return false;
            }

            // Controls are hidden - handle trigger inputs
            if (key == VirtualKey.GamepadLeftTrigger)
            {
                _lastSkipInputUtc = DateTimeOffset.UtcNow;
                ActionWithParameterTriggered?.Invoke(this, (MediaAction.Rewind, 600));
                return true;
            }

            if (key == VirtualKey.GamepadRightTrigger)
            {
                _lastSkipInputUtc = DateTimeOffset.UtcNow;
                ActionWithParameterTriggered?.Invoke(this, (MediaAction.FastForward, 600));
                return true;
            }

            // Map VirtualKey to ControllerButton for other keys
            var action = GetActionForKey(key);
            if (action.HasValue)
            {
                if (action == MediaAction.Rewind || action == MediaAction.FastForward)
                {
                    _lastSkipInputUtc = DateTimeOffset.UtcNow;
                }

                ActionTriggered?.Invoke(this, action.Value);
                return true;
            }

            return false;
        }

        public void Dispose()
        {
            // Disable the service
            IsEnabled = false;
            _mediaPlayer = null;
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
