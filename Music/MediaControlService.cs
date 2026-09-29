using System;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Music
{
    public interface IMediaControlService
    {
        MediaPlayer MediaPlayer { get; }
        bool IsPlaying { get; }
        BaseItemDto CurrentItem { get; }
        RepeatMode RepeatMode { get; }
        TimeSpan Position { get; }

        event EventHandler<BaseItemDto> NowPlayingChanged;
        event EventHandler<MediaPlaybackState> PlaybackStateChanged;
        event EventHandler<MediaPlayerFailedEventArgs> MediaFailed;
        event EventHandler MediaEnded;
        event EventHandler MediaOpened;

        void Initialize(MediaPlayer mediaPlayer);
        void Play();
        void Pause();
        void Stop();
        void SetRepeatMode(RepeatMode mode);
        Task SetMediaSourceAsync(MediaPlaybackItem source, BaseItemDto item);
        void ClearMediaSource();
    }

    public enum RepeatMode
    {
        None,
        One,
        All
    }

    public class MediaControlService : BaseService, IMediaControlService
    {
        public MediaControlService(ILogger<MediaControlService> logger) :
            base(logger)
        {
        }

        public MediaPlayer MediaPlayer { get; private set; }

        public bool IsPlaying => MediaPlayer?.PlaybackSession?.PlaybackState == MediaPlaybackState.Playing;
        public BaseItemDto CurrentItem { get; private set; }

        public RepeatMode RepeatMode { get; private set; }

        public TimeSpan Position => MediaPlayer?.PlaybackSession?.Position ?? TimeSpan.Zero;

        public event EventHandler<BaseItemDto> NowPlayingChanged;
        public event EventHandler<MediaPlaybackState> PlaybackStateChanged;
        public event EventHandler<MediaPlayerFailedEventArgs> MediaFailed;
        public event EventHandler MediaEnded;
        public event EventHandler MediaOpened;

        public void Initialize(MediaPlayer mediaPlayer)
        {
            MediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer));
            SubscribeToMediaPlayerEvents();
        }

        public void Play()
        {
            ErrorHandler.Run(CreateErrorContext("Play", ErrorCategory.Media), () => MediaPlayer?.Play());
        }

        public void Pause()
        {
            ErrorHandler.Run(CreateErrorContext("Pause", ErrorCategory.Media), () => MediaPlayer?.Pause());
        }

        public void Stop()
        {
            ErrorHandler.Run(CreateErrorContext("Stop", ErrorCategory.Media), () =>
            {
                MediaPlayer?.Pause();
                ClearMediaSource();
                CurrentItem = null;
                NowPlayingChanged?.Invoke(this, null);
            });
        }

        public void SetRepeatMode(RepeatMode mode)
        {
            ErrorHandler.Run(CreateErrorContext("SetRepeatMode", ErrorCategory.Media), () =>
            {
                RepeatMode = mode;
                Logger.LogInformation("Repeat mode set to: {Mode}", mode);
            });
        }

        public async Task SetMediaSourceAsync(MediaPlaybackItem source, BaseItemDto item)
        {
            var context = CreateErrorContext("SetMediaSourceAsync", ErrorCategory.Media);
            try
            {
                if (source == null || item == null)
                {
                    Logger.LogError("Cannot set media source - source or item is null");
                    return;
                }

                CurrentItem = item;

                Logger.LogDebug("Setting media source for: {ItemName}", item.Name);

                // Set new source directly without clearing first for smooth transitions
                MediaPlayer.Source = source;

                await UiHelper.RunOnUIThreadAsync(() => NowPlayingChanged?.Invoke(this, item), logger: Logger);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, context, false);
            }
        }

        public void ClearMediaSource()
        {
            ErrorHandler.Run(CreateErrorContext("ClearMediaSource", ErrorCategory.Media), () =>
            {
                if (MediaPlayer != null)
                {
                    MediaPlayer.Source = null;
                }
            });
        }

        private void SubscribeToMediaPlayerEvents()
        {
            MediaPlayer.MediaFailed += OnMediaFailed;
            MediaPlayer.MediaEnded += OnMediaEnded;
            MediaPlayer.CurrentStateChanged += OnCurrentStateChanged;
            MediaPlayer.MediaOpened += OnMediaOpened;
        }

        private void UnsubscribeFromMediaPlayerEvents()
        {
            MediaPlayer.MediaFailed -= OnMediaFailed;
            MediaPlayer.MediaEnded -= OnMediaEnded;
            MediaPlayer.CurrentStateChanged -= OnCurrentStateChanged;
            MediaPlayer.MediaOpened -= OnMediaOpened;
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            Logger.LogError("Media playback failed: {ArgsError} - {ArgsErrorMessage}", args.Error, args.ErrorMessage);
            MediaFailed?.Invoke(sender, args);
        }

        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            Logger.LogInformation("Media playback ended - RepeatMode: {RepeatMode}", RepeatMode);
            MediaEnded?.Invoke(sender, EventArgs.Empty);
        }

        private void OnCurrentStateChanged(MediaPlayer sender, object args)
        {
            var playbackState = sender.PlaybackSession?.PlaybackState ?? MediaPlaybackState.None;
            Logger.LogDebug("Playback state changed to: {PlaybackState}", playbackState);
            PlaybackStateChanged?.Invoke(this, playbackState);
        }

        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            var session = sender.PlaybackSession;
            Logger.LogInformation(
                "MediaPlayer opened: Duration {Duration}, State {PlaybackState}, CanPause {CanPause}, CanSeek {CanSeek}",
                session?.NaturalDuration, session?.PlaybackState, session?.CanPause, session?.CanSeek);

            MediaOpened?.Invoke(sender, EventArgs.Empty);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && MediaPlayer != null)
            {
                UnsubscribeFromMediaPlayerEvents();
                // Not ours to dispose: the music player service created it and keeps it for the app's life
                MediaPlayer = null;
            }

            base.Dispose(disposing);
        }
    }
}
