using System;
using System.Collections.Generic;
using System.Linq;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Gelatinarm.Details;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Navigation;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Music
{
    public sealed partial class MusicPlayer : BaseControl
    {
        private IMusicPlayerService _musicPlayerService;
        private IPlaybackQueueService _queueService;
        private INavigationService _navigationService;
        private readonly DispatcherTimer _progressTimer = new() { Interval = TimeSpan.FromMilliseconds(MusicConstants.MiniPlayerUpdateIntervalMs) };

        public MusicPlayer()
        {
            InitializeComponent();
            _progressTimer.Tick += ProgressTimer_Tick;
            Loaded += MusicPlayer_Loaded;
            Unloaded += MusicPlayer_Unloaded;
        }

        protected override void OnServicesInitialized()
        {
            _musicPlayerService = GetRequiredService<IMusicPlayerService>();
            _queueService = GetRequiredService<IPlaybackQueueService>();
            _navigationService = GetRequiredService<INavigationService>();
        }

        private void MusicPlayer_Loaded(object sender, RoutedEventArgs e)
        {
            var context = CreateErrorContext("MusicPlayerLoad");
            try
            {
                _musicPlayerService.NowPlayingChanged += OnNowPlayingChanged;
                _musicPlayerService.PlaybackStateChanged += OnPlaybackStateChanged;
                _musicPlayerService.ShuffleStateChanged += OnShuffleStateChanged;
                _musicPlayerService.RepeatModeChanged += OnRepeatModeChanged;

                _queueService.QueueChanged += OnQueueChanged;

                if (_musicPlayerService.CurrentItem != null)
                {
                    UpdateNowPlayingInfo(_musicPlayerService.CurrentItem);
                    UpdatePlayPauseButton();
                    if (_musicPlayerService.IsPlaying)
                    {
                        _progressTimer.Start();
                    }

                    UpdateShuffleButton();
                    UpdateRepeatButton();
                    UpdateNavigationButtons();
                }
                else
                {
                    Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, context);
            }
        }

        private void MusicPlayer_Unloaded(object sender, RoutedEventArgs e)
        {
            _progressTimer.Stop();

            _musicPlayerService.NowPlayingChanged -= OnNowPlayingChanged;
            _musicPlayerService.PlaybackStateChanged -= OnPlaybackStateChanged;
            _musicPlayerService.ShuffleStateChanged -= OnShuffleStateChanged;
            _musicPlayerService.RepeatModeChanged -= OnRepeatModeChanged;

            _queueService.QueueChanged -= OnQueueChanged;
        }

        private void OnNowPlayingChanged(object sender, BaseItemDto item)
        {
            UpdateOnUIThread("NowPlayingChanged", () =>
            {
                UpdateNowPlayingInfo(item);
                UpdateNavigationButtons();
            });
        }

        private void OnPlaybackStateChanged(object sender, MediaPlaybackState state)
        {
            UpdateOnUIThread("PlaybackStateChanged", () =>
            {
                UpdatePlayPauseButton();

                if (state == MediaPlaybackState.Playing)
                {
                    _progressTimer.Start();
                }
                else
                {
                    _progressTimer.Stop();
                }
            });
        }

        private void OnShuffleStateChanged(object sender, bool isShuffled)
        {
            UpdateOnUIThread("ShuffleStateChanged", () =>
            {
                UpdateShuffleButton();
                UpdateNavigationButtons();
            });
        }

        private void OnRepeatModeChanged(object sender, RepeatMode repeatMode)
        {
            UpdateOnUIThread("RepeatModeChanged", () =>
            {
                UpdateRepeatButton();
                UpdateNavigationButtons();
            });
        }

        private void OnQueueChanged(object sender, List<BaseItemDto> queue)
        {
            UpdateOnUIThread("QueueChanged", UpdateNavigationButtons);
        }

        // The player service raises its events off the UI thread. RunOnUIThreadAsync never throws.
        private void UpdateOnUIThread(string operation, Action update)
        {
            _ = UiHelper.RunOnUIThreadAsync(() => ErrorHandler.Run(CreateErrorContext(operation), update),
                Logger, Dispatcher);
        }

        private void UpdateNowPlayingInfo(BaseItemDto item)
        {
            if (item == null)
            {
                Visibility = Visibility.Collapsed;
                return;
            }

            Visibility = Visibility.Visible;
            TrackName.Text = MediaLabels.TrackTitle(item);
            ArtistName.Text = MediaLabels.TrackArtist(item);

            // A track without a length must not keep the previous track's
            TotalTimeText.Text = item.RunTimeTicks > 0
                ? TimeFormattingHelper.FormatTime(TimeSpan.FromTicks(item.RunTimeTicks.Value))
                : string.Empty;

            // A track without artwork must not keep the previous track's
            var artworkUrl = ImageHelper.GetTrackArtworkUrl(item, 200);
            AlbumArt.Source = artworkUrl != null ? new BitmapImage(new Uri(artworkUrl)) : null;
            ProgressBar.Width = 0;
            CurrentTimeText.Text = "0:00";
        }

        private void ProgressTimer_Tick(object sender, object e)
        {
            var playbackSession = _musicPlayerService.MediaPlayer?.PlaybackSession;
            if (playbackSession != null)
            {
                try
                {
                    var position = playbackSession.Position;

                    // The item's own length, as the total shown beside it
                    var currentItem = _musicPlayerService.CurrentItem;
                    if (currentItem?.RunTimeTicks > 0)
                    {
                        var duration = TimeSpan.FromTicks(currentItem.RunTimeTicks.Value);

                        var progressPercentage = position.TotalSeconds / duration.TotalSeconds;
                        if (ProgressBar.Parent is Grid progressBarContainer)
                        {
                            ProgressBar.Width = progressBarContainer.ActualWidth * progressPercentage;
                        }

                        CurrentTimeText.Text = TimeFormattingHelper.FormatTime(position);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "MusicPlayer: progress tick failed");
                }
            }
        }

        private void UpdatePlayPauseButton()
        {
            var isPlaying = _musicPlayerService.IsPlaying;
            PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768"; // Pause : Play
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_musicPlayerService.IsPlaying)
            {
                _musicPlayerService.Pause();
            }
            else
            {
                _musicPlayerService.Play();
            }
        }

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            _musicPlayerService.SkipPrevious();
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            _musicPlayerService.SkipNext();
        }

        private void GoToArtist_Click(object sender, RoutedEventArgs e)
        {
            var currentItem = _musicPlayerService.CurrentItem;
            var artistItem = currentItem?.ArtistItems?.FirstOrDefault() ?? currentItem?.AlbumArtists?.FirstOrDefault();
            if (artistItem?.Id is Guid artistId)
            {
                _navigationService.Navigate(typeof(ArtistDetailsPage), artistId.ToString());
            }
        }

        private void GoToAlbum_Click(object sender, RoutedEventArgs e)
        {
            if (_musicPlayerService.CurrentItem?.AlbumId is Guid albumId)
            {
                _navigationService.Navigate(typeof(AlbumDetailsPage), albumId.ToString());
            }
        }

        private async void StartInstantMix_Click(object sender, RoutedEventArgs e)
        {
            var currentItem = _musicPlayerService.CurrentItem;
            if (currentItem != null)
            {
                await _musicPlayerService.PlayInstantMixAsync(currentItem);
            }
        }

        private void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            _queueService.ClearQueue();
            Logger.LogInformation("Queue cleared");
        }

        private void ClosePlayer_Click(object sender, RoutedEventArgs e)
        {
            _musicPlayerService.Stop();
        }

        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            // The service's ShuffleStateChanged brings the button's look in line
            _musicPlayerService.SetShuffle(ShuffleButton.IsChecked == true);
        }

        private void RepeatButton_Click(object sender, RoutedEventArgs e)
        {
            _musicPlayerService.CycleRepeatMode();
        }

        private void UpdateShuffleButton()
        {
            ShuffleButton.IsChecked = _queueService.IsShuffleMode;
            ShuffleButton.Opacity = _queueService.IsShuffleMode ? 1.0 : 0.6;
        }

        private void UpdateRepeatButton()
        {
            switch (_musicPlayerService.RepeatMode)
            {
                case RepeatMode.None:
                    RepeatIcon.Glyph = "\uE8EE"; // Repeat all icon (subdued)
                    RepeatButton.Opacity = 0.6;
                    break;
                case RepeatMode.All:
                    RepeatIcon.Glyph = "\uE8EE"; // Repeat all icon (active)
                    RepeatButton.Opacity = 1.0;
                    break;
                case RepeatMode.One:
                    RepeatIcon.Glyph = "\uE8ED"; // Repeat one icon (active)
                    RepeatButton.Opacity = 1.0;
                    break;
            }
        }

        // As SkipNext and SkipPrevious go: Next follows the play order (shuffled or not) and wraps
        // under Repeat All; Previous always has somewhere to go, restarting the first track
        private void UpdateNavigationButtons()
        {
            PreviousButton.IsEnabled = _queueService.Queue.Count > 0;

            NextButton.IsEnabled = _queueService.HasNext(_musicPlayerService.RepeatMode == RepeatMode.All);
        }

        public void FocusPlayPauseButton()
        {
            if (Visibility == Visibility.Visible)
            {
                PlayPauseButton.Focus(FocusState.Programmatic);
            }
        }
    }
}
