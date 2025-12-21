using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Gelatinarm.Models;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.Core;
using Windows.Gaming.Input;
using Windows.Media.Playback;
using Windows.System;
using Windows.UI.Core;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Service for managing media controller input
    /// </summary>
    public class MediaControllerService : BaseService, IMediaControllerService
    {
        // Default button mappings
        private static readonly Dictionary<ControllerButton, MediaAction> DefaultMapping = new()
        {
            { ControllerButton.A, MediaAction.PlayPause },           // Play/Pause
            { ControllerButton.B, MediaAction.NavigateBack },        // Back/Stop
            { ControllerButton.Y, MediaAction.ShowStats },           // Show stats overlay
            { ControllerButton.DPadLeft, MediaAction.Rewind },       // Skip back 10 seconds
            { ControllerButton.DPadRight, MediaAction.FastForward }, // Skip forward 30 seconds
            { ControllerButton.DPadUp, MediaAction.ShowInfo },       // Show controls/OSD
            { ControllerButton.DPadDown, MediaAction.ShowInfo },     // Show controls/OSD
            // X button not mapped - not used during playback
            // Triggers handled separately in HandleKeyDownAsync for 10 minute skips
            // Shoulder buttons not mapped - audio/subtitle selection via UI only
            // Menu and View buttons not used
        };

        private readonly CoreDispatcher _dispatcher;
        private bool _areControlsVisible = false;
        private DateTimeOffset _lastSkipInputUtc = DateTimeOffset.MinValue;
        private Dictionary<ControllerButton, MediaAction> _buttonMapping;
        private Gamepad _currentGamepad;
        private MediaPlayer _mediaPlayer;

        public MediaControllerService(ILogger<MediaControllerService> logger) : base(logger)
        {
            _dispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;
            _buttonMapping = new Dictionary<ControllerButton, MediaAction>(DefaultMapping);
            IsEnabled = true;

            // Subscribe to gamepad events
            Gamepad.GamepadAdded += Gamepad_GamepadAdded;
            Gamepad.GamepadRemoved += Gamepad_GamepadRemoved;

            // Check for already connected gamepads
            if (Gamepad.Gamepads.Count > 0)
            {
                _currentGamepad = Gamepad.Gamepads[0];
                Logger.LogInformation($"Found connected gamepad: {_currentGamepad}");
            }
            else
            {
                Logger.LogWarning("No gamepad detected at initialization");
            }
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

            // Check again for gamepads if we didn't find one in constructor
            if (_currentGamepad == null && Gamepad.Gamepads.Count > 0)
            {
                _currentGamepad = Gamepad.Gamepads[0];
                Logger.LogInformation($"Found gamepad during initialization: {_currentGamepad}");
            }

            // Event-based input will be handled by subscribing to Page's KeyDown events
            Logger.LogInformation("MediaControllerService initialized - ready for event-based input");
            return Task.CompletedTask;
        }

        public Task<bool> HandleButtonPressAsync(ControllerButton button)
        {
            if (!IsEnabled || _buttonMapping == null)
            {
                return Task.FromResult(false);
            }

            // Check if button has a mapping
            if (_buttonMapping.TryGetValue(button, out var action))
            {
                if (action == MediaAction.Rewind || action == MediaAction.FastForward)
                {
                    _lastSkipInputUtc = DateTimeOffset.UtcNow;
                }

                // Some actions should work regardless of control visibility
                // ShowInfo toggles controls, ShowStats just overlays stats without taking focus
                if (action == MediaAction.ShowInfo ||
                    action == MediaAction.ShowStats)
                {
                    Logger.LogInformation(
                        $"Controller button {button} mapped to action {action} - allowing regardless of control visibility");
                    ActionTriggered?.Invoke(this, action);
                    return Task.FromResult(true);
                }

                // When controls are visible, ignore other controller input to allow UI navigation
                // This includes A button (PlayPause) so it can activate focused buttons
                if (_areControlsVisible)
                {
                    Logger.LogDebug($"Ignoring {button} - controls are visible, allowing UI navigation");
                    return Task.FromResult(false);
                }

                Logger.LogInformation($"Controller button {button} mapped to action {action}");
                if (ActionTriggered != null)
                {
                    Logger.LogInformation($"Invoking ActionTriggered event for action {action}");
                    ActionTriggered.Invoke(this, action);
                }
                else
                {
                    Logger.LogWarning($"ActionTriggered event is null - cannot invoke action {action}");
                }
                return Task.FromResult(true);
            }

            Logger.LogDebug($"No mapping found for controller button {button}");
            return Task.FromResult(false);
        }


        public void SetButtonMapping(Dictionary<ControllerButton, MediaAction> mapping)
        {
            _buttonMapping = mapping ?? new Dictionary<ControllerButton, MediaAction>(DefaultMapping);
            Logger.LogInformation("Button mapping updated");
        }

        public Dictionary<ControllerButton, MediaAction> GetButtonMapping()
        {
            return new Dictionary<ControllerButton, MediaAction>(_buttonMapping);
        }

        public void SetEnabled(bool enabled)
        {
            IsEnabled = enabled;
            Logger.LogInformation($"Controller input {(enabled ? "enabled" : "disabled")}");
        }

        public void SetControlsVisible(bool visible)
        {
            _areControlsVisible = visible;
            Logger.LogInformation($"MediaControllerService: Controls visibility updated to {visible}");
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
            ControllerButton? button = key switch
            {
                VirtualKey.GamepadA => ControllerButton.A,
                VirtualKey.GamepadX => ControllerButton.X,
                VirtualKey.GamepadY => ControllerButton.Y,
                VirtualKey.GamepadDPadUp => ControllerButton.DPadUp,
                VirtualKey.GamepadDPadDown => ControllerButton.DPadDown,
                VirtualKey.GamepadDPadLeft => ControllerButton.DPadLeft,
                VirtualKey.GamepadDPadRight => ControllerButton.DPadRight,
                VirtualKey.GamepadLeftShoulder => ControllerButton.LeftShoulder,
                VirtualKey.GamepadRightShoulder => ControllerButton.RightShoulder,
                VirtualKey.GamepadView => ControllerButton.View,
                VirtualKey.GamepadLeftThumbstickButton => ControllerButton.LeftThumbstick,
                VirtualKey.GamepadRightThumbstickButton => ControllerButton.RightThumbstick,
                VirtualKey.Space => ControllerButton.A, // Space acts like A button
                // Map arrow keys to D-pad (accepting that analog stick will also trigger these)
                VirtualKey.Up => ControllerButton.DPadUp,
                VirtualKey.Down => ControllerButton.DPadDown,
                VirtualKey.Left => ControllerButton.DPadLeft,
                VirtualKey.Right => ControllerButton.DPadRight,
                _ => null
            };

            if (button.HasValue)
            {
                return await HandleButtonPressAsync(button.Value);
            }

            return false;
        }

        public void Dispose()
        {
            // Disable the service
            IsEnabled = false;

            // Unsubscribe from events
            Gamepad.GamepadAdded -= Gamepad_GamepadAdded;
            Gamepad.GamepadRemoved -= Gamepad_GamepadRemoved;

            _currentGamepad = null;
            _mediaPlayer = null;
            _buttonMapping = null;
        }

        private void Gamepad_GamepadAdded(object sender, Gamepad e)
        {
            Logger.LogInformation($"Gamepad connected: {e}");
            _currentGamepad = e;
        }

        private void Gamepad_GamepadRemoved(object sender, Gamepad e)
        {
            Logger.LogInformation("Gamepad disconnected");
            if (_currentGamepad == e)
            {
                _currentGamepad = null;
            }
        }
    }
}
