using System;
using Gelatinarm.Shared.Errors;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public partial class MediaPlayerViewModel
    {
        public bool LastActionWasSkip { get; private set; }

        public event EventHandler ToggleControlsRequested;

        public void SetControlsTakeInput(bool takeInput)
        {
            _controllerInputService.SetControlsTakeInput(takeInput);
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
                Logger.LogDebug("Controller action with parameter: {ArgsAction}, parameter: {ArgsParameter}", args.action, args.parameter);

                switch (args.action)
                {
                    case MediaAction.FastForward:
                        if (args.parameter is int forwardSeconds)
                        {
                            await SkipForwardAsync(forwardSeconds);
                        }

                        break;
                    case MediaAction.Rewind:
                        if (args.parameter is int backwardSeconds)
                        {
                            await SkipBackwardAsync(backwardSeconds);
                        }

                        break;
                    default:
                        OnControllerActionTriggered(sender, args.action);
                        break;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnControllerActionWithParameterTriggered", ErrorCategory.Media), false);
            }
        }

        private async void OnControllerActionTriggered(object sender, MediaAction action)
        {
            try
            {
                Logger.LogDebug("OnControllerActionTriggered called with action: {Action}", action);
                switch (action)
                {
                    case MediaAction.PlayPause:
                        await PlayPauseAsync();
                        break;
                    case MediaAction.FastForward:
                        await SkipForwardAsync(null);
                        break;
                    case MediaAction.Rewind:
                        await SkipBackwardAsync(null);
                        break;
                    case MediaAction.ShowStats:
                        ToggleStats();
                        break;
                    case MediaAction.ShowInfo:
                        ToggleControlsRequested?.Invoke(this, EventArgs.Empty);
                        break;
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("OnControllerActionTriggered", ErrorCategory.Media), false);
            }
        }

        public IControllerInputService GetControllerInputService()
        {
            return _controllerInputService;
        }
    }
}
