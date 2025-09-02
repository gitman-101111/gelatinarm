using System;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;

namespace Gelatinarm.Services
{
    public enum RepeatMode
    {
        None,
        One,
        All
    }

    public interface IMediaControlService
    {
        MediaPlayer MediaPlayer { get; }
        bool IsPlaying { get; }
        BaseItemDto CurrentItem { get; }
        RepeatMode RepeatMode { get; }
        TimeSpan Position { get; }
        TimeSpan Duration { get; }

        event EventHandler<BaseItemDto> NowPlayingChanged;
        event EventHandler<MediaPlaybackState> PlaybackStateChanged;
        event EventHandler<MediaPlayerFailedEventArgs> MediaFailed;
        event EventHandler MediaEnded;
        event EventHandler MediaOpened;

        Task InitializeAsync(MediaPlayer mediaPlayer);
        void Play();
        void Pause();
        void Stop();
        void SeekTo(TimeSpan position);
        void SeekForward(int seconds);
        void SeekBackward(int seconds);
        void SetRepeatMode(RepeatMode mode);
        RepeatMode CycleRepeatMode();
        void SetPlaybackRate(double rate);
        Task SetMediaSource(MediaPlaybackItem source, BaseItemDto item);
        void ClearMediaSource();
    }

    public class MediaControlService : BaseService, IMediaControlService, IDisposable
    {
        private readonly IPreferencesService _preferencesService;

        public MediaControlService(ILogger<MediaControlService> logger, IPreferencesService preferencesService = null) : base(logger)
        {
            // Don't create MediaPlayer here - wait for InitializeAsync
            _preferencesService = preferencesService;
        }

        public MediaPlayer MediaPlayer { get; private set; }

        public bool IsPlaying => MediaPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing;
        public BaseItemDto CurrentItem { get; private set; }

        public RepeatMode RepeatMode { get; private set; } = RepeatMode.None;

        public TimeSpan Position => MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;
        public TimeSpan Duration => MediaPlayer?.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;

        public event EventHandler<BaseItemDto> NowPlayingChanged;
        public event EventHandler<MediaPlaybackState> PlaybackStateChanged;
        public event EventHandler<MediaPlayerFailedEventArgs> MediaFailed;
        public event EventHandler MediaEnded;
        public event EventHandler MediaOpened;

        public async Task InitializeAsync(MediaPlayer mediaPlayer)
        {
            if (mediaPlayer == null)
            {
                throw new ArgumentNullException(nameof(mediaPlayer));
            }

            // Clean up any existing MediaPlayer
            if (MediaPlayer != null)
            {
                UnsubscribeFromMediaPlayerEvents();
            }

            MediaPlayer = mediaPlayer;
            SubscribeToMediaPlayerEvents();

            Logger.LogInformation("MediaControlService initialized/re-initialized with external MediaPlayer");
            await Task.CompletedTask;
        }

        public void Play()
        {
            try
            {
                MediaPlayer?.Play();
                Logger.LogInformation("Play command executed");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error executing play command");
            }
        }

        public void Pause()
        {
            try
            {
                MediaPlayer?.Pause();
                Logger.LogInformation("Pause command executed");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error executing pause command");
            }
        }

        public void Stop()
        {
            var context = CreateErrorContext("Stop", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    MediaPlayer?.Pause();
                    ClearMediaSource();
                    CurrentItem = null;

                    NowPlayingChanged?.Invoke(this, null);
                    Logger.LogInformation("Stop command executed");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SeekTo(TimeSpan position)
        {
            var context = CreateErrorContext("SeekTo", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    if (MediaPlayer?.PlaybackSession != null)
                    {
                        var currentPosition = MediaPlayer.PlaybackSession.Position;
                        MediaPlayer.PlaybackSession.Position = position;
                        Logger.LogInformation($"Seeked to position: {position} (from {currentPosition})");
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public void SeekForward(int seconds)
        {
            try
            {
                if (MediaPlayer?.PlaybackSession != null)
                {
                    var currentPosition = MediaPlayer.PlaybackSession.Position;
                    var naturalDuration = MediaPlayer.PlaybackSession.NaturalDuration;

                    Logger.LogInformation($"Attempting to seek forward {seconds} seconds from {currentPosition:mm\\:ss}");
                    Logger.LogInformation($"NaturalDuration before seek: {naturalDuration:mm\\:ss}");

                    var newPosition = currentPosition + TimeSpan.FromSeconds(seconds);
                    var newPositionTicks = newPosition.Ticks;
                    var newPositionSeconds = newPositionTicks / 10000000.0;

                    if (newPosition < naturalDuration)
                    {
                        Logger.LogInformation($"Setting new position: {newPosition:mm\\:ss} ({newPositionSeconds:F1}s), Ticks: {newPositionTicks}");
                        MediaPlayer.PlaybackSession.Position = newPosition;
                        Logger.LogInformation($"Seeked forward {seconds} seconds to {newPosition:mm\\:ss}");
                    }
                    else
                    {
                        Logger.LogWarning($"Cannot seek forward {seconds} seconds - would exceed duration {naturalDuration}");
                    }
                }
                else
                {
                    Logger.LogWarning("Cannot seek - MediaPlayer or PlaybackSession is null");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error seeking forward {seconds} seconds");
            }
        }

        public void SeekBackward(int seconds)
        {
            try
            {
                if (MediaPlayer?.PlaybackSession != null)
                {
                    var currentPosition = MediaPlayer.PlaybackSession.Position;
                    Logger.LogInformation($"Attempting to seek backward {seconds} seconds from {currentPosition:mm\\:ss}");

                    var newPosition = currentPosition - TimeSpan.FromSeconds(seconds);
                    var newPositionTicks = newPosition.Ticks;
                    var newPositionSeconds = newPositionTicks / 10000000.0;

                    if (newPosition >= TimeSpan.Zero)
                    {
                        Logger.LogInformation($"Setting new position: {newPosition:mm\\:ss} ({newPositionSeconds:F1}s), Ticks: {newPositionTicks}");
                        MediaPlayer.PlaybackSession.Position = newPosition;
                    }
                    else
                    {
                        Logger.LogInformation("Setting position to beginning (00:00)");
                        MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
                    }

                    Logger.LogInformation($"Seeked backward {seconds} seconds to {MediaPlayer.PlaybackSession.Position:mm\\:ss}");
                }
                else
                {
                    Logger.LogWarning("Cannot seek - MediaPlayer or PlaybackSession is null");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error seeking backward {seconds} seconds");
            }
        }

        public void SetRepeatMode(RepeatMode mode)
        {
            var context = CreateErrorContext("SetRepeatMode", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    RepeatMode = mode;
                    Logger.LogInformation($"Repeat mode set to: {mode}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public RepeatMode CycleRepeatMode()
        {
            var context = CreateErrorContext("CycleRepeatMode", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    // Cycle through: None -> All -> One -> None
                    RepeatMode = RepeatMode switch
                    {
                        RepeatMode.None => RepeatMode.All,
                        RepeatMode.All => RepeatMode.One,
                        RepeatMode.One => RepeatMode.None,
                        _ => RepeatMode.None
                    };

                    Logger.LogInformation($"Repeat mode cycled to: {RepeatMode}");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });

            return RepeatMode;
        }

        public void SetPlaybackRate(double rate)
        {
            var context = CreateErrorContext("SetPlaybackRate", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    if (MediaPlayer?.PlaybackSession != null)
                    {
                        MediaPlayer.PlaybackSession.PlaybackRate = rate;
                        Logger.LogInformation($"Playback rate set to: {rate}x");
                    }

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        public async Task SetMediaSource(MediaPlaybackItem source, BaseItemDto item)
        {
            var context = CreateErrorContext("SetMediaSource", ErrorCategory.Media);
            try
            {
                if (source == null || item == null)
                {
                    Logger.LogError("Cannot set media source - source or item is null");
                    return;
                }

                CurrentItem = item;

                Logger.LogInformation($"Setting media source for: {item.Name}");

                // Clear current source with delay
                ClearMediaSource();
                await Task.Delay(100).ConfigureAwait(false);

                // Set new source
                MediaPlayer.Source = source;

                // Notify listeners
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    NowPlayingChanged?.Invoke(this, item);
                }, logger: Logger);

                Logger.LogInformation("Media source set successfully");
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public void ClearMediaSource()
        {
            var context = CreateErrorContext("ClearMediaSource", ErrorCategory.Media);
            AsyncHelper.FireAndForget(async () =>
            {
                try
                {
                    if (MediaPlayer != null)
                    {
                        MediaPlayer.Source = null;
                        Logger.LogDebug("Media source cleared");
                    }
                    else
                    {
                        Logger.LogDebug("MediaPlayer is null, skipping source clear");
                    }
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    await ErrorHandler.HandleErrorAsync(ex, context, false);
                }
            });
        }

        private void SubscribeToMediaPlayerEvents()
        {
            if (MediaPlayer == null) return;

            MediaPlayer.MediaFailed += OnMediaFailed;
            MediaPlayer.MediaEnded += OnMediaEnded;
            MediaPlayer.CurrentStateChanged += OnCurrentStateChanged;
            MediaPlayer.MediaOpened += OnMediaOpened;
        }

        private void UnsubscribeFromMediaPlayerEvents()
        {
            if (MediaPlayer == null) return;

            MediaPlayer.MediaFailed -= OnMediaFailed;
            MediaPlayer.MediaEnded -= OnMediaEnded;
            MediaPlayer.CurrentStateChanged -= OnCurrentStateChanged;
            MediaPlayer.MediaOpened -= OnMediaOpened;
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            Logger.LogError($"Media playback failed: {args.Error} - {args.ErrorMessage}");
            MediaFailed?.Invoke(sender, args);
        }

        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            Logger.LogInformation($"Media playback ended - RepeatMode: {RepeatMode}");
            MediaEnded?.Invoke(sender, EventArgs.Empty);
        }

        private void OnCurrentStateChanged(MediaPlayer sender, object args)
        {
            var playbackState = sender?.PlaybackSession?.PlaybackState ?? MediaPlaybackState.None;
            Logger.LogDebug($"Playback state changed to: {playbackState}");
            PlaybackStateChanged?.Invoke(this, playbackState);
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            Logger.LogInformation("MediaPlayer opened successfully");

            var duration = sender.PlaybackSession?.NaturalDuration;
            if (duration.HasValue && duration.Value < TimeSpan.FromDays(1000))
            {
                Logger.LogInformation($"Duration: {duration}");
            }

            Logger.LogInformation($"PlaybackState: {sender.PlaybackSession?.PlaybackState}");
            Logger.LogInformation($"CanPause: {sender.PlaybackSession?.CanPause}");
            Logger.LogInformation($"CanSeek: {sender.PlaybackSession?.CanSeek}");


            MediaOpened?.Invoke(sender, EventArgs.Empty);
        }

        public void Dispose()
        {
            if (MediaPlayer != null)
            {
                UnsubscribeFromMediaPlayerEvents();
                // Don't dispose MediaPlayer - we don't own it, MediaPlayerElement does
                MediaPlayer = null;
            }
        }
    }
}
