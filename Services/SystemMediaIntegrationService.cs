using System;
using System.Linq;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace Gelatinarm.Services
{
    public interface ISystemMediaIntegrationService
    {
        event EventHandler<SystemMediaTransportControlsButton> ButtonPressed;
        event EventHandler<bool> ShuffleEnabledChangeRequested;
        event EventHandler<MediaPlaybackAutoRepeatMode> RepeatModeChangeRequested;

        void Initialize(MediaPlayer mediaPlayer);
        Task UpdateDisplay(BaseItemDto item);
        void UpdatePlaybackStatus(MediaPlaybackState state);
        void UpdateButtonStates(bool canGoNext, bool canGoPrevious);
        void SetShuffleEnabled(bool enabled);
        void SetRepeatMode(RepeatMode mode);
        void ClearDisplay();
        void Dispose();
    }

    public class SystemMediaIntegrationService : BaseService, ISystemMediaIntegrationService
    {
        private MediaPlayer _mediaPlayer;
        private SystemMediaTransportControls _systemMediaTransportControls;

        public SystemMediaIntegrationService(ILogger<SystemMediaIntegrationService> logger) : base(logger)
        {
        }

        public event EventHandler<SystemMediaTransportControlsButton> ButtonPressed;
        public event EventHandler<bool> ShuffleEnabledChangeRequested;
        public event EventHandler<MediaPlaybackAutoRepeatMode> RepeatModeChangeRequested;

        public void Initialize(MediaPlayer mediaPlayer)
        {
            var context = CreateErrorContext("Initialize");
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    _mediaPlayer = mediaPlayer;
                    InitializeSystemMediaTransportControls();
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public async Task UpdateDisplay(BaseItemDto item)
        {
            if (_systemMediaTransportControls == null || item == null)
            {
                return;
            }

            var context = CreateErrorContext("UpdateDisplay", ErrorCategory.Media);
            try
            {
                var updater = _systemMediaTransportControls.DisplayUpdater;
                if (updater == null)
                {
                    Logger.LogWarning("SystemMediaTransportControls.DisplayUpdater returned null");
                    return;
                }
                updater.Type = MediaPlaybackType.Music;

                // Set music properties
                updater.MusicProperties.Title = item.Name ?? "Unknown Title";
                updater.MusicProperties.Artist = item.AlbumArtist ?? item.Artists?.FirstOrDefault() ?? "Unknown Artist";
                updater.MusicProperties.AlbumTitle = item.Album ?? "";

                // Set album artwork
                await SetAlbumArtwork(updater, item);

                // Update the display
                updater.Update();

                Logger.LogInformation($"Updated System Media Transport Controls: {item.Name}");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public void UpdatePlaybackStatus(MediaPlaybackState state)
        {
            if (_systemMediaTransportControls == null)
            {
                return;
            }

            var context = CreateErrorContext("UpdatePlaybackStatus", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    switch (state)
                    {
                        case MediaPlaybackState.Playing:
                            _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Playing;
                            break;
                        case MediaPlaybackState.Paused:
                            _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Paused;
                            break;
                        case MediaPlaybackState.None:
                            _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Stopped;
                            break;
                        case MediaPlaybackState.Opening:
                        case MediaPlaybackState.Buffering:
                            _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Changing;
                            break;
                    }

                    Logger.LogDebug($"Updated SMTC playback status to: {_systemMediaTransportControls.PlaybackStatus}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void UpdateButtonStates(bool canGoNext, bool canGoPrevious)
        {
            if (_systemMediaTransportControls == null)
            {
                return;
            }

            var context = CreateErrorContext("UpdateButtonStates", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    // Ensure SMTC is enabled before setting button states
                    if (!_systemMediaTransportControls.IsEnabled)
                    {
                        Logger.LogWarning("SMTC is not enabled, enabling it now");
                        _systemMediaTransportControls.IsEnabled = true;
                    }

                    _systemMediaTransportControls.IsNextEnabled = canGoNext;
                    _systemMediaTransportControls.IsPreviousEnabled = canGoPrevious;

                    // Force update the display
                    _systemMediaTransportControls.DisplayUpdater.Update();

                    Logger.LogInformation(
                        $"Updated transport controls buttons: Next={canGoNext}, Previous={canGoPrevious}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SetShuffleEnabled(bool enabled)
        {
            if (_systemMediaTransportControls == null)
            {
                return;
            }

            var context = CreateErrorContext("SetShuffleEnabled", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    _systemMediaTransportControls.ShuffleEnabled = enabled;
                    Logger.LogInformation($"SMTC shuffle set to: {enabled}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SetRepeatMode(RepeatMode mode)
        {
            if (_systemMediaTransportControls == null)
            {
                return;
            }

            var context = CreateErrorContext("SetRepeatMode", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    _systemMediaTransportControls.AutoRepeatMode = mode switch
                    {
                        RepeatMode.None => MediaPlaybackAutoRepeatMode.None,
                        RepeatMode.One => MediaPlaybackAutoRepeatMode.Track,
                        RepeatMode.All => MediaPlaybackAutoRepeatMode.List,
                        _ => MediaPlaybackAutoRepeatMode.None
                    };

                    Logger.LogInformation($"SMTC repeat mode set to: {_systemMediaTransportControls.AutoRepeatMode}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void ClearDisplay()
        {
            if (_systemMediaTransportControls == null)
            {
                return;
            }

            var context = CreateErrorContext("ClearDisplay", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    _systemMediaTransportControls.DisplayUpdater.ClearAll();
                    _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Closed;
                    _systemMediaTransportControls.DisplayUpdater.Update();
                    Logger.LogInformation("Cleared System Media Transport Controls display");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void Dispose()
        {
            if (_systemMediaTransportControls != null)
            {
                // Unsubscribe from events
                _systemMediaTransportControls.ButtonPressed -= OnSystemMediaTransportControlsButtonPressed;
                _systemMediaTransportControls.ShuffleEnabledChangeRequested -= OnShuffleEnabledChangeRequested;
                _systemMediaTransportControls.AutoRepeatModeChangeRequested -= OnAutoRepeatModeChangeRequested;

                // Clear the display and set to closed state before disabling
                _systemMediaTransportControls.DisplayUpdater.ClearAll();
                _systemMediaTransportControls.PlaybackStatus = MediaPlaybackStatus.Closed;
                _systemMediaTransportControls.DisplayUpdater.Update();

                // Now disable and null out
                _systemMediaTransportControls.IsEnabled = false;
                _systemMediaTransportControls = null;
            }

            _mediaPlayer = null;
        }

        private void InitializeSystemMediaTransportControls()
        {
            try
            {
                if (_mediaPlayer == null)
                {
                    Logger.LogWarning("Cannot initialize SystemMediaTransportControls - MediaPlayer is null");
                    return;
                }

                _systemMediaTransportControls = _mediaPlayer.SystemMediaTransportControls;
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

                Logger.LogInformation("System Media Transport Controls initialized");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to initialize System Media Transport Controls");
            }
        }

        private void OnSystemMediaTransportControlsButtonPressed(SystemMediaTransportControls sender,
            SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            Logger.LogInformation($"SMTC Button pressed: {args.Button}");
            ButtonPressed?.Invoke(this, args.Button);
        }

        private void OnShuffleEnabledChangeRequested(SystemMediaTransportControls sender,
            ShuffleEnabledChangeRequestedEventArgs args)
        {
            Logger.LogInformation("SMTC Shuffle change requested");
            ShuffleEnabledChangeRequested?.Invoke(this, !sender.ShuffleEnabled);
        }

        private void OnAutoRepeatModeChangeRequested(SystemMediaTransportControls sender,
            AutoRepeatModeChangeRequestedEventArgs args)
        {
            Logger.LogInformation($"SMTC Repeat mode change requested: {args.RequestedAutoRepeatMode}");
            _systemMediaTransportControls.AutoRepeatMode = args.RequestedAutoRepeatMode;
            RepeatModeChangeRequested?.Invoke(this, args.RequestedAutoRepeatMode);
        }

        private async Task SetAlbumArtwork(SystemMediaTransportControlsDisplayUpdater updater, BaseItemDto item)
        {
            try
            {
                var hasPrimaryImage = item.ImageTags?.AdditionalData != null &&
                                      item.ImageTags.AdditionalData.ContainsKey("Primary");

                if (hasPrimaryImage || item.AlbumId != null || item.AlbumPrimaryImageTag != null)
                {
                    string imageUrl = null;

                    // Use album image if available, otherwise use track image
                    if (item.AlbumId.HasValue && item.AlbumPrimaryImageTag != null)
                    {
                        imageUrl = ImageHelper.GetImageUrl(item.AlbumId.Value.ToString(), "Primary");
                        Logger.LogInformation($"Using album image URL: {imageUrl}");
                    }
                    else if (item.Id.HasValue && hasPrimaryImage)
                    {
                        imageUrl = ImageHelper.GetImageUrl(item.Id.Value.ToString(), "Primary");
                        Logger.LogInformation($"Using track image URL: {imageUrl}");
                    }
                    else if (item.AlbumId.HasValue)
                    {
                        // Try album image even without tag
                        imageUrl = ImageHelper.GetImageUrl(item.AlbumId.Value.ToString(), "Primary");
                        Logger.LogInformation($"Trying album image URL without tag: {imageUrl}");
                    }

                    if (!string.IsNullOrEmpty(imageUrl))
                    {
                        updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(imageUrl));
                        Logger.LogInformation("Set SMTC thumbnail successfully");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to set album artwork for System Media Transport Controls");
            }
        }
    }
}
