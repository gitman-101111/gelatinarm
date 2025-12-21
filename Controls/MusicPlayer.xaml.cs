using System;
using System.Collections.Generic;
using System.Linq;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Gelatinarm.Services;
using Gelatinarm.Views;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using RepeatMode = Gelatinarm.Services.RepeatMode;

namespace Gelatinarm.Controls
{
    public sealed partial class MusicPlayer : BaseControl
    {
        private MediaPlayer _currentMediaPlayer;
        private bool _isUpdatingProgress = false;
        private IMusicPlayerService _musicPlayerService;
        private INavigationService _navigationService;
        private JellyfinApiClient _apiClient;
        private IUserProfileService _userProfileService;
        private IImageLoadingService _imageLoadingService;
        private DispatcherTimer _progressTimer;

        public MusicPlayer()
        {
            InitializeComponent(); Loaded += MusicPlayer_Loaded;
            Unloaded += MusicPlayer_Unloaded;
        }

        protected override void OnServicesInitialized(IServiceProvider services)
        {
            // Store service provider for later use
            _musicPlayerService = GetService<IMusicPlayerService>();
            _navigationService = GetService<INavigationService>();
            _apiClient = GetService<JellyfinApiClient>();
            _userProfileService = GetService<IUserProfileService>();
            _imageLoadingService = GetService<IImageLoadingService>();
        }

        private void MusicPlayer_Loaded(object sender, RoutedEventArgs e)
        {
            var context = CreateErrorContext("MusicPlayerLoad");
            try
            {
                // Wire up click handlers programmatically to avoid XAML parsing issues on Xbox
                if (PreviousButton != null)
                {
                    PreviousButton.Click += PreviousButton_Click;
                }

                if (RewindButton != null)
                {
                    RewindButton.Click += RewindButton_Click;
                }

                if (PlayPauseButton != null)
                {
                    PlayPauseButton.Click += PlayPauseButton_Click;
                }

                if (FastForwardButton != null)
                {
                    FastForwardButton.Click += FastForwardButton_Click;
                }

                if (NextButton != null)
                {
                    NextButton.Click += NextButton_Click;
                }

                if (MenuButton != null)
                {
                    MenuButton.Click += MenuButton_Click;
                }

                if (ShuffleButton != null)
                {
                    ShuffleButton.Click += ShuffleButton_Click;
                }

                // Subscribe to service events
                if (_musicPlayerService != null)
                {
                    _musicPlayerService.NowPlayingChanged += OnNowPlayingChanged;
                    _musicPlayerService.PlaybackStateChanged += OnPlaybackStateChanged;
                    _musicPlayerService.ShuffleStateChanged += OnShuffleStateChanged;
                    _musicPlayerService.RepeatModeChanged += OnRepeatModeChanged;
                    _musicPlayerService.QueueChanged += OnQueueChanged;

                    // Subscribe to MediaPlayer events for duration updates
                    SubscribeToMediaPlayer();
                }
                _progressTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(UIConstants.MINI_PLAYER_UPDATE_INTERVAL_MS)
                };
                _progressTimer.Tick += ProgressTimer_Tick;


                // Check if there's already something playing
                if (_musicPlayerService?.CurrentItem != null)
                {
                    UpdateNowPlayingInfo(_musicPlayerService.CurrentItem);
                    UpdatePlayPauseButton();
                    if (_musicPlayerService.IsPlaying)
                    {
                        _progressTimer.Start();
                    }

                    // Update shuffle and repeat button states
                    UpdateShuffleButton();
                    UpdateRepeatButton();
                    UpdateNavigationButtons();
                }
                else
                {
                    // No content playing, ensure we're hidden
                    Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }
            }
        }

        private void MusicPlayer_Unloaded(object sender, RoutedEventArgs e)
        {
            var context = CreateErrorContext("MusicPlayerUnload", ErrorCategory.User, ErrorSeverity.Warning);
            try
            {
                // Unwire click handlers
                if (PreviousButton != null)
                {
                    PreviousButton.Click -= PreviousButton_Click;
                }

                if (RewindButton != null)
                {
                    RewindButton.Click -= RewindButton_Click;
                }

                if (PlayPauseButton != null)
                {
                    PlayPauseButton.Click -= PlayPauseButton_Click;
                }

                if (FastForwardButton != null)
                {
                    FastForwardButton.Click -= FastForwardButton_Click;
                }

                if (NextButton != null)
                {
                    NextButton.Click -= NextButton_Click;
                }

                if (MenuButton != null)
                {
                    MenuButton.Click -= MenuButton_Click;
                }

                if (ShuffleButton != null)
                {
                    ShuffleButton.Click -= ShuffleButton_Click;
                }

                // Stop and dispose timers
                if (_progressTimer != null)
                {
                    _progressTimer.Stop();
                    _progressTimer.Tick -= ProgressTimer_Tick;
                    _progressTimer = null;
                }


                // Unsubscribe from events
                if (_musicPlayerService != null)
                {
                    _musicPlayerService.NowPlayingChanged -= OnNowPlayingChanged;
                    _musicPlayerService.PlaybackStateChanged -= OnPlaybackStateChanged;
                    _musicPlayerService.ShuffleStateChanged -= OnShuffleStateChanged;
                    _musicPlayerService.RepeatModeChanged -= OnRepeatModeChanged;
                    _musicPlayerService.QueueChanged -= OnQueueChanged;
                }

                // Unsubscribe from MediaPlayer events
                UnsubscribeFromMediaPlayer();

                Logger?.LogInformation("MusicPlayer unloaded and cleaned up");
            }
            catch (Exception ex)
            {
                if (ErrorHandler is ErrorHandlingService errorService)
                {
                    errorService.HandleError(ex, context);
                }
                else
                {
                    AsyncHelper.FireAndForget(async () => await ErrorHandler.HandleErrorAsync(ex, context, false));
                }
            }
        }

        private void OnNowPlayingChanged(object sender, BaseItemDto item)
        {
            Logger?.LogInformation($"MusicPlayer received NowPlayingChanged event for: {item?.Name}");
#if DEBUG
            Logger?.LogDebug($"MusicPlayer: NowPlayingChanged event received for: {item?.Name}");
#endif
            AsyncHelper.FireAndForget(async () =>
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    var context = CreateErrorContext("NowPlayingChanged");
                    try
                    {
#if DEBUG
                        Logger?.LogDebug($"MusicPlayer: Updating UI for: {item?.Name}");
#endif
                        UpdateNowPlayingInfo(item);
                        UpdateNavigationButtons();

                        // Re-subscribe to MediaPlayer in case it changed
                        SubscribeToMediaPlayer();
                    }
                    catch (Exception ex)
                    {
                        if (ErrorHandler is ErrorHandlingService errorService)
                        {
                            errorService.HandleError(ex, context);
                        }
                        else
                        {
                            AsyncHelper.FireAndForget(async () =>
                                await ErrorHandler.HandleErrorAsync(ex, context, false));
                        }
                    }
                }, Dispatcher, Logger);
            }, Logger, typeof(MusicPlayer));
        }

        private void OnPlaybackStateChanged(object sender, MediaPlaybackState state)
        {
            AsyncHelper.FireAndForget(async () =>
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    try
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
                    }
                    catch (Exception ex)
                    {
                        var context = CreateErrorContext("PlaybackStateChanged");
                        if (ErrorHandler is ErrorHandlingService errorService)
                        {
                            errorService.HandleError(ex, context);
                        }
                        else
                        {
                            AsyncHelper.FireAndForget(async () =>
                                await ErrorHandler.HandleErrorAsync(ex, context, false));
                        }
                    }
                }, Dispatcher, Logger);
            }, Logger, typeof(MusicPlayer));
        }

        private void OnShuffleStateChanged(object sender, bool isShuffled)
        {
            Logger?.LogInformation($"MusicPlayer received ShuffleStateChanged event: {isShuffled}");
            AsyncHelper.FireAndForget(async () =>
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    try
                    {
                        UpdateShuffleButton();
                    }
                    catch (Exception ex)
                    {
                        var context = CreateErrorContext("ShuffleStateChanged");
                        if (ErrorHandler is ErrorHandlingService errorService)
                        {
                            errorService.HandleError(ex, context);
                        }
                        else
                        {
                            AsyncHelper.FireAndForget(async () =>
                                await ErrorHandler.HandleErrorAsync(ex, context, false));
                        }
                    }
                }, Dispatcher, Logger);
            }, Logger, typeof(MusicPlayer));
        }

        private void OnRepeatModeChanged(object sender, RepeatMode repeatMode)
        {
            Logger?.LogInformation($"MusicPlayer received RepeatModeChanged event: {repeatMode}");
            AsyncHelper.FireAndForget(async () =>
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    try
                    {
                        UpdateRepeatButton();
                    }
                    catch (Exception ex)
                    {
                        var context = CreateErrorContext("RepeatModeChanged");
                        if (ErrorHandler is ErrorHandlingService errorService)
                        {
                            errorService.HandleError(ex, context);
                        }
                        else
                        {
                            AsyncHelper.FireAndForget(async () =>
                                await ErrorHandler.HandleErrorAsync(ex, context, false));
                        }
                    }
                }, Dispatcher, Logger);
            }, Logger, typeof(MusicPlayer));
        }

        private void OnQueueChanged(object sender, List<BaseItemDto> queue)
        {
            Logger?.LogInformation($"MusicPlayer received QueueChanged event: {queue?.Count ?? 0} items");
            AsyncHelper.FireAndForget(async () =>
            {
                await UIHelper.RunOnUIThreadAsync(() =>
                {
                    try
                    {
                        UpdateNavigationButtons();
                    }
                    catch (Exception ex)
                    {
                        var context = CreateErrorContext("QueueChanged");
                        if (ErrorHandler is ErrorHandlingService errorService)
                        {
                            errorService.HandleError(ex, context);
                        }
                        else
                        {
                            AsyncHelper.FireAndForget(async () =>
                                await ErrorHandler.HandleErrorAsync(ex, context, false));
                        }
                    }
                }, Dispatcher, Logger);
            }, Logger, typeof(MusicPlayer));
        }

        private void UpdateNowPlayingInfo(BaseItemDto item)
        {
#if DEBUG
            Logger?.LogDebug($"MusicPlayer: UpdateNowPlayingInfo called for: {item?.Name}");
#endif
            if (item == null)
            {
#if DEBUG
                Logger?.LogDebug("MusicPlayer: Item is null, hiding MusicPlayer");
#endif
                Visibility = Visibility.Collapsed;
                return;
            }

            // Show the MusicPlayer when we have content
#if DEBUG
            Logger?.LogDebug("MusicPlayer: Setting visibility to Visible");
#endif
            Visibility = Visibility.Visible;

            // Update UI with null checks
            if (TrackName != null)
            {
                TrackName.Text = item.Name ?? "Unknown Track";
            }

            if (ArtistName != null)
            {
                ArtistName.Text = item.AlbumArtist ?? item.Artists?.FirstOrDefault() ?? "Unknown Artist";
            }


            // Update duration from metadata if available
            if (TotalTimeText != null && item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0)
            {
                var duration = TimeSpan.FromTicks(item.RunTimeTicks.Value);
                TotalTimeText.Text = TimeFormattingHelper.FormatTime(duration);
                Logger?.LogDebug($"MusicPlayer: Set duration from metadata: {TimeFormattingHelper.FormatTime(duration)}");
            }

            // Load album art
            if (AlbumArt != null)
            {
                // Load album art asynchronously
                AsyncHelper.FireAndForget(async () =>
                {
                    if (_imageLoadingService != null)
                    {
                        BaseItemDto imageItem = null;

                        // Determine which item to use for the image
                        if (item.AlbumId.HasValue)
                        {
                            // Create a temporary item for the album
                            imageItem = new BaseItemDto { Id = item.AlbumId };
                        }
                        else if (ImageHelper.HasImageType(item, "Primary"))
                        {
                            imageItem = item;
                        }

                        if (imageItem != null)
                        {
                            await _imageLoadingService.LoadImageIntoTargetAsync(
                                imageItem,
                                "Primary",
                                imageSource => AlbumArt.Source = imageSource,
                                Dispatcher,
                                200,
                                200
                            ).ConfigureAwait(false);
                        }
                    }
                }, Logger, typeof(MusicPlayer));
            }

            // Reset progress with null checks
            if (ProgressBar != null)
            {
                ProgressBar.Width = 0;
            }

            if (CurrentTimeText != null)
            {
                CurrentTimeText.Text = "0:00";
            }
            // Don't reset TotalTimeText here - it's already set from metadata above

            Visibility = Visibility.Visible;
        }

        private void ProgressTimer_Tick(object sender, object e)
        {
            if (!_isUpdatingProgress && _musicPlayerService?.MediaPlayer?.PlaybackSession != null)
            {
                _isUpdatingProgress = true;
                try
                {
                    var playbackSession = _musicPlayerService.MediaPlayer.PlaybackSession;
                    if (playbackSession == null)
                    {
                        return;
                    }

                    var position = playbackSession.Position;

                    // Always use metadata duration for consistency
                    var currentItem = _musicPlayerService?.CurrentItem;
                    if (currentItem?.RunTimeTicks.HasValue == true && currentItem.RunTimeTicks.Value > 0)
                    {
                        var duration = TimeSpan.FromTicks(currentItem.RunTimeTicks.Value);

                        // Update progress bar width as percentage
                        var progressPercentage = position.TotalSeconds / duration.TotalSeconds;
                        var progressBarContainer = ProgressBar?.Parent as Grid;
                        if (progressBarContainer != null && ProgressBar != null)
                        {
                            ProgressBar.Width = progressBarContainer.ActualWidth * progressPercentage;
                        }

                        // Update time displays
                        if (CurrentTimeText != null)
                        {
                            CurrentTimeText.Text = TimeFormattingHelper.FormatTime(position);
                        }
                        // Duration text is already set from metadata in UpdateNowPlayingInfo
                    }
                }
#if DEBUG
                catch (Exception ex)
                {
                    Logger?.LogDebug($"MusicPlayer: Error in ProgressTimer_Tick - {ex.Message}");
                }
#else
                catch
                {
                }
#endif
                finally
                {
                    _isUpdatingProgress = false;
                }
            }
        }


        private void UpdatePlayPauseButton()
        {
            if (PlayPauseIcon == null)
            {
                return;
            }

            var isPlaying = _musicPlayerService?.IsPlaying == true;
            PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768"; // Pause : Play
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_musicPlayerService?.IsPlaying == true)
                {
                    _musicPlayerService.Pause();
                }
                else
                {
                    _musicPlayerService?.Play();
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in PlayPauseButton_Click");
            }
        }

        private void PreviousButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.SkipPrevious();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in PreviousButton_Click");
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.SkipNext();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in NextButton_Click");
            }
        }

        private void RewindButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.SeekBackward(10);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in RewindButton_Click");
            }
        }

        private void FastForwardButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.SeekForward(30);
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in FastForwardButton_Click");
            }
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            // Flyout will open automatically
            // No direct service calls here that are likely to throw unhandled.
        }

        private void GoToArtist_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var currentItem = _musicPlayerService?.CurrentItem;
                if (currentItem?.AlbumArtists?.Any() == true || currentItem?.ArtistItems?.Any() == true)
                {
                    var artistItem = currentItem.ArtistItems?.FirstOrDefault() ??
                                     currentItem.AlbumArtists?.FirstOrDefault();
                    if (artistItem != null && _navigationService != null) // Added null check for navigationService
                    {
                        _navigationService.Navigate(typeof(ArtistDetailsPage), artistItem.Id.Value.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in GoToArtist_Click");
            }
        }

        private void GoToAlbum_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var currentItem = _musicPlayerService?.CurrentItem;
                if (currentItem?.AlbumId.HasValue == true)
                {
                    if (_navigationService != null) // Added null check for navigationService
                    {
                        _navigationService.Navigate(typeof(AlbumDetailsPage), currentItem.AlbumId.Value.ToString());
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in GoToAlbum_Click");
            }
        }

        private async void StartInstantMix_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var currentItem = _musicPlayerService?.CurrentItem;
                if (currentItem?.Id != null)
                {
                    Logger?.LogInformation($"Starting instant mix for '{currentItem.Name}'");

                    // Get the API client
                    if (_apiClient != null && _userProfileService != null)
                    {
                        var userId = _userProfileService.CurrentUserId;
                        if (!string.IsNullOrEmpty(userId) && Guid.TryParse(userId, out var userIdGuid))
                        {
                            // Get instant mix for the current track
                            var instantMix = await _apiClient.Items[currentItem.Id.Value].InstantMix.GetAsync(config =>
                            {
                                config.QueryParameters.UserId = userIdGuid;
                                config.QueryParameters.Limit = MediaConstants.MAX_DISCOVERY_QUERY_LIMIT;
                            });

                            if (instantMix?.Items?.Any() == true)
                            {
                                // Play the instant mix
                                await _musicPlayerService.PlayItems(instantMix.Items.ToList());
                                Logger?.LogInformation($"Started instant mix with {instantMix.Items.Count} tracks");
                            }
                            else
                            {
                                Logger?.LogWarning("No items returned for instant mix");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in StartInstantMix_Click");
            }
        }


        private void ClearQueue_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.ClearQueue();
                Logger?.LogInformation("Queue cleared");
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ClearQueue_Click");
            }
        }

        private void ClosePlayer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.Stop();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ClosePlayer_Click");
            }
        }


        private void ShuffleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ShuffleButton != null)
                {
                    var isShuffled = ShuffleButton.IsChecked ?? false;
                    _musicPlayerService?.SetShuffle(isShuffled);
                    Logger?.LogInformation($"Shuffle set to: {isShuffled}");
                    // Update opacity immediately
                    ShuffleButton.Opacity = isShuffled ? 1.0 : 0.6;
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in ShuffleButton_Click");
            }
        }

        private void RepeatButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _musicPlayerService?.CycleRepeatMode();
                UpdateRepeatButton();
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error in RepeatButton_Click");
            }
        }

        private void UpdateShuffleButton()
        {
            if (ShuffleButton != null && _musicPlayerService != null)
            {
                ShuffleButton.IsChecked = _musicPlayerService.IsShuffleEnabled;
                // Update opacity based on state
                ShuffleButton.Opacity = _musicPlayerService.IsShuffleEnabled ? 1.0 : 0.6;
            }
        }

        private void UpdateRepeatButton()
        {
            if (RepeatIcon != null && RepeatButton != null && _musicPlayerService != null)
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
        }

        private void UpdateNavigationButtons()
        {
            if (_musicPlayerService != null && _musicPlayerService.Queue != null)
            {
                var hasPrevious = _musicPlayerService.CurrentQueueIndex > 0;
                var hasNext = _musicPlayerService.CurrentQueueIndex < _musicPlayerService.Queue.Count - 1;

                if (PreviousButton != null)
                {
                    PreviousButton.IsEnabled = hasPrevious;
                }

                if (NextButton != null)
                {
                    NextButton.IsEnabled = hasNext;
                }
            }
        }

        /// <summary>
        /// Sets focus to the play/pause button, making the MusicPlayer the active control
        /// </summary>
        public void FocusPlayPauseButton()
        {
            if (PlayPauseButton != null && Visibility == Visibility.Visible)
            {
                PlayPauseButton.Focus(FocusState.Programmatic);
                Logger?.LogInformation("MusicPlayer: Focus set to PlayPauseButton via trigger hold");
            }
        }


        private void SubscribeToMediaPlayer()
        {
            try
            {
                // Unsubscribe from previous MediaPlayer if any
                UnsubscribeFromMediaPlayer();

                // Subscribe to new MediaPlayer
                if (_musicPlayerService?.MediaPlayer != null)
                {
                    _currentMediaPlayer = _musicPlayerService.MediaPlayer;
                    Logger?.LogInformation("Subscribed to MediaPlayer events");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error subscribing to MediaPlayer events");
            }
        }

        private void UnsubscribeFromMediaPlayer()
        {
            try
            {
                if (_currentMediaPlayer != null)
                {
                    _currentMediaPlayer = null;
                    Logger?.LogInformation("Unsubscribed from MediaPlayer events");
                }
            }
            catch (Exception ex)
            {
                Logger?.LogError(ex, "Error unsubscribing from MediaPlayer events");
            }
        }
    }
}
