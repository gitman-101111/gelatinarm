using System;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Gelatinarm.Details;
using Gelatinarm.Shared.Async;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Music
{
    public partial class MusicPlayerService
    {
        /// <summary>
        ///     Sets up the system media controls on the first track and shows <paramref name="item" />
        ///     there. Runs on NowPlayingChanged and again on MediaOpened: the MediaPlayer resets the
        ///     controls' display when a new source finishes opening.
        /// </summary>
        private void ShowOnTransportControls(BaseItemDto item)
        {
            if (!IsAudioItem(item))
            {
                return;
            }

            if (!_isSmtcInitialized && _mediaControlService.MediaPlayer != null)
            {
                Logger.LogDebug(
                    "Initializing System Media Transport Controls for music playback: {ItemName}", item.Name);
                InitializeSystemMediaTransportControls(_mediaControlService.MediaPlayer);
                _isSmtcInitialized = true;
            }

            FireAndForget(() => UpdateDisplayAsync(item), "UpdateSystemMediaDisplay");
            UpdateTransportControlsState();
        }

        private void OnSystemMediaTransportControlsButtonPressed(SystemMediaTransportControls sender,
            SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            Logger.LogInformation("SMTC Button pressed: {ArgsButton}", args.Button);
            switch (args.Button)
            {
                case SystemMediaTransportControlsButton.Play:
                    Play();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    Pause();
                    break;
                case SystemMediaTransportControlsButton.Stop:
                    Stop();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    SkipNext();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    SkipPrevious();
                    break;
            }
        }

        private void InitializeSystemMediaTransportControls(MediaPlayer mediaPlayer)
        {
            _systemMediaTransportControls = mediaPlayer.SystemMediaTransportControls;
            if (_systemMediaTransportControls == null)
            {
                Logger.LogWarning("MediaPlayer.SystemMediaTransportControls returned null");
                return;
            }

            _systemMediaTransportControls.IsEnabled = true;
            _systemMediaTransportControls.IsPauseEnabled = true;
            _systemMediaTransportControls.IsPlayEnabled = true;
            _systemMediaTransportControls.IsNextEnabled = true;
            _systemMediaTransportControls.IsPreviousEnabled = true;
            _systemMediaTransportControls.IsStopEnabled = true;

            _systemMediaTransportControls.ButtonPressed += OnSystemMediaTransportControlsButtonPressed;
            _systemMediaTransportControls.ShuffleEnabledChangeRequested += OnShuffleEnabledChangeRequested;
            _systemMediaTransportControls.AutoRepeatModeChangeRequested += OnAutoRepeatModeChangeRequested;

            Logger.LogDebug("System Media Transport Controls initialized");
            UpdateTransportControlsState();
        }

        private void UnwireSystemMediaTransportControls()
        {
            if (_systemMediaTransportControls == null)
            {
                return;
            }

            _systemMediaTransportControls.ButtonPressed -= OnSystemMediaTransportControlsButtonPressed;
            _systemMediaTransportControls.ShuffleEnabledChangeRequested -= OnShuffleEnabledChangeRequested;
            _systemMediaTransportControls.AutoRepeatModeChangeRequested -= OnAutoRepeatModeChangeRequested;
        }

        private void UpdatePlaybackStatus(MediaPlaybackState state)
        {
            var smtc = _systemMediaTransportControls;
            if (smtc == null)
            {
                return;
            }

            if ((state == MediaPlaybackState.None || state == MediaPlaybackState.Paused) &&
                DateTime.UtcNow < _smtcSuppressStoppedUntilUtc)
            {
                Logger.LogDebug("Suppressing SMTC stopped status during transition");
                return;
            }

            var token = AsyncHelper.Supersede(ref _playbackStatusUpdateCts).Token;

            var context = CreateErrorContext("UpdatePlaybackStatus", ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    await Task.Delay(50, token).ConfigureAwait(false);

                    smtc.PlaybackStatus = state switch
                    {
                        MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
                        MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
                        MediaPlaybackState.None => MediaPlaybackStatus.Stopped,
                        MediaPlaybackState.Opening => MediaPlaybackStatus.Changing,
                        MediaPlaybackState.Buffering => MediaPlaybackStatus.Changing,
                        _ => MediaPlaybackStatus.Closed
                    };

                    Logger.LogDebug("Updated SMTC playback status to: {SmtcPlaybackStatus}", smtc.PlaybackStatus);
                }
                catch (OperationCanceledException)
                {
                    // Debounced — a newer state superseded this one
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private async Task UpdateDisplayAsync(BaseItemDto item)
        {
            var smtc = _systemMediaTransportControls;
            if (smtc == null || item == null)
            {
                return;
            }

            var context = CreateErrorContext("UpdateDisplayAsync", ErrorCategory.Media);
            try
            {
                await UiHelper.RunOnUIThreadAsync(() =>
                {
                    var updater = smtc.DisplayUpdater;
                    if (updater == null)
                    {
                        Logger.LogWarning("SystemMediaTransportControls.DisplayUpdater returned null");
                        return;
                    }

                    updater.Type = MediaPlaybackType.Music;
                    updater.MusicProperties.Title = MediaLabels.TrackTitle(item);
                    updater.MusicProperties.Artist = MediaLabels.TrackArtist(item);
                    updater.MusicProperties.AlbumTitle = item.Album ?? string.Empty;

                    SetAlbumArtwork(updater, item);
                    updater.Update();

                    Logger.LogDebug("Updated System Media Transport Controls: {ItemName}", item.Name);
                }, logger: Logger).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        /// <summary>
        ///     The transport controls must be touched on the UI thread; callers are not.
        /// </summary>
        private void UpdateTransportControls(string operation, Action<SystemMediaTransportControls> update)
        {
            var smtc = _systemMediaTransportControls;
            if (smtc == null)
            {
                return;
            }

            var context = CreateErrorContext(operation, ErrorCategory.Media);
            FireAndForget(async () =>
            {
                try
                {
                    await UiHelper.RunOnUIThreadAsync(() => update(smtc), logger: Logger).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void ClearDisplay()
        {
            var smtc = _systemMediaTransportControls;
            if (smtc == null)
            {
                return;
            }

            var context = CreateErrorContext("ClearDisplay", ErrorCategory.Media);
            ErrorHandler.Run(context, () =>
            {
                smtc.DisplayUpdater.ClearAll();
                smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                smtc.DisplayUpdater.Update();
                Logger.LogDebug("Cleared System Media Transport Controls display");
            });
        }

        private void DisposeSystemMediaTransportControls()
        {
            if (_systemMediaTransportControls == null)
            {
                return;
            }

            UnwireSystemMediaTransportControls();
            ClearDisplay();
            _systemMediaTransportControls.IsEnabled = false;
            _systemMediaTransportControls = null;
        }

        private void OnShuffleEnabledChangeRequested(SystemMediaTransportControls sender,
            ShuffleEnabledChangeRequestedEventArgs args)
        {
            Logger.LogInformation("SMTC Shuffle change requested");
            SetShuffle(!sender.ShuffleEnabled);
        }

        private void OnAutoRepeatModeChangeRequested(SystemMediaTransportControls sender,
            AutoRepeatModeChangeRequestedEventArgs args)
        {
            Logger.LogInformation("SMTC Repeat mode change requested: {ArgsRequestedAutoRepeatMode}", args.RequestedAutoRepeatMode);
            ApplyRepeatMode(args.RequestedAutoRepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.Track => RepeatMode.One,
                MediaPlaybackAutoRepeatMode.List => RepeatMode.All,
                _ => RepeatMode.None
            });
        }

        private void SetAlbumArtwork(SystemMediaTransportControlsDisplayUpdater updater, BaseItemDto item)
        {
            try
            {
                var imageUrl = ImageHelper.GetTrackArtworkUrl(item);
                if (!string.IsNullOrEmpty(imageUrl))
                {
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(imageUrl));
                    Logger.LogDebug("Set SMTC thumbnail successfully");
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("SetAlbumArtwork", ErrorCategory.Media, ErrorSeverity.Warning));
            }
        }

        private void UpdateTransportControlsState()
        {
            var token = AsyncHelper.Supersede(ref _transportControlsUpdateCts).Token;

            FireAndForget(async () =>
            {
                try
                {
                    await Task.Delay(75, token).ConfigureAwait(false);

                    var queue = _queueService.Queue;
                    var currentIndex = _queueService.CurrentQueueIndex;
                    var isRepeatAll = _mediaControlService.RepeatMode == RepeatMode.All;
                    var isShuffleMode = _queueService.IsShuffleMode;

                    // As the mini player's buttons and SkipNext/SkipPrevious go: Next follows the play
                    // order (shuffled or not) and wraps under Repeat All; Previous always has somewhere
                    // to go, restarting the first track
                    var shouldEnableNext = _queueService.HasNext(isRepeatAll);
                    var shouldEnablePrevious = queue.Count > 0;
                    var repeatMode = _mediaControlService.RepeatMode;

                    UpdateTransportControls("UpdateTransportControlsState", smtc =>
                    {
                        smtc.IsEnabled = true;
                        smtc.IsNextEnabled = shouldEnableNext;
                        smtc.IsPreviousEnabled = shouldEnablePrevious;
                        smtc.ShuffleEnabled = isShuffleMode;
                        smtc.AutoRepeatMode = repeatMode switch
                        {
                            RepeatMode.One => MediaPlaybackAutoRepeatMode.Track,
                            RepeatMode.All => MediaPlaybackAutoRepeatMode.List,
                            _ => MediaPlaybackAutoRepeatMode.None
                        };
                    });

                    Logger.LogDebug(
                        "Updated transport controls: Next={ShouldEnableNext}, Previous={ShouldEnablePrevious}, Shuffle={IsShuffleMode}, Repeat={MediaControlServiceRepeatMode}, QueueIndex={CurrentIndex}/{QueueCount}", shouldEnableNext, shouldEnablePrevious, isShuffleMode, _mediaControlService.RepeatMode, currentIndex, queue.Count);
                }
                catch (OperationCanceledException)
                {
                    // Debounced — a newer update superseded this one
                }
                catch (Exception ex)
                {
                    ErrorHandler.HandleError(ex, CreateErrorContext("UpdateTransportControlsState", ErrorCategory.Media));
                }
            });
        }
    }
}
