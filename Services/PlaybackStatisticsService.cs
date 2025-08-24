using System;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Microsoft.Extensions.Logging;
using Windows.ApplicationModel.Core;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Gelatinarm.Services
{
    /// <summary>
    ///     Service for managing playback statistics display
    /// </summary>
    public class PlaybackStatisticsService : BaseService, IPlaybackStatisticsService
    {
        private readonly CoreDispatcher _dispatcher;
        private PlaybackStats _currentStats;
        private MediaPlayer _mediaPlayer;
        private DispatcherTimer _updateTimer;

        public PlaybackStatisticsService(ILogger<PlaybackStatisticsService> logger) : base(logger)
        {
            _dispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;
            _currentStats = new PlaybackStats();
        }

        public bool IsVisible { get; private set; }

        public event EventHandler<PlaybackStats> StatsUpdated;

        public Task InitializeAsync(MediaPlayer mediaPlayer)
        {
            _mediaPlayer = mediaPlayer ?? throw new ArgumentNullException(nameof(mediaPlayer)); _updateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250) // Update 4 times per second
            };
            _updateTimer.Tick += UpdateTimer_Tick;

            Logger.LogInformation("PlaybackStatisticsService initialized");
            return Task.CompletedTask;
        }

        public PlaybackStats GetCurrentStats()
        {
            UpdateStatistics();
            return _currentStats;
        }

        public void StartUpdating()
        {
            if (!_updateTimer.IsEnabled)
            {
                _updateTimer.Start();
                Logger.LogInformation("Started statistics updates");
            }
        }

        public void StopUpdating()
        {
            // Ensure we're on the UI thread when accessing DispatcherTimer
            if (_dispatcher.HasThreadAccess)
            {
                if (_updateTimer?.IsEnabled == true)
                {
                    _updateTimer.Stop();
                    Logger.LogInformation("Stopped statistics updates");
                }
            }
            else
            {
                // Queue the stop operation on the UI thread
                _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (_updateTimer?.IsEnabled == true)
                    {
                        _updateTimer.Stop();
                        Logger.LogInformation("Stopped statistics updates from UI thread");
                    }
                });
            }
        }

        public void ToggleVisibility()
        {
            IsVisible = !IsVisible;

            if (IsVisible)
            {
                StartUpdating();
            }
            else
            {
                StopUpdating();
            }

            Logger.LogInformation($"Statistics visibility toggled to: {IsVisible}");
        }

        public void Dispose()
        {
            // Ensure timer is stopped properly on UI thread
            if (_updateTimer != null)
            {
                if (_dispatcher.HasThreadAccess)
                {
                    _updateTimer.Stop();
                    _updateTimer = null;
                }
                else
                {
                    // Queue the disposal on the UI thread
                    _ = _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        _updateTimer?.Stop();
                        _updateTimer = null;
                    });
                }
            }

            _mediaPlayer = null;
            _currentStats = null;
        }

        private void UpdateTimer_Tick(object sender, object e)
        {
            UpdateStatistics();
        }

        private void UpdateStatistics()
        {
            try
            {
                if (_mediaPlayer?.PlaybackSession == null)
                {
                    return;
                }

                var session = _mediaPlayer.PlaybackSession;
                var stats = new PlaybackStats
                {
                    LastUpdated = DateTime.Now,                 // Populate new detailed fields
                    Player = "MPV Video Player",
                    PlayMethod = GetPlayMethod(),
                    Protocol = "https",
                    StreamType = session.NaturalVideoHeight > 0 ? "Video" : "Audio",
                    Container = GetContainerFormat(),
                    Size = null, // Not available from MediaPlayer - will be hidden
                    Bitrate = null, // Not available from MediaPlayer - will be hidden
                    VideoCodec = GetVideoCodecInfo(),
                    VideoBitrate = null, // Not available from MediaPlayer - will be hidden
                    VideoRangeType = GetVideoRangeType(),
                    AudioCodec = GetAudioCodecInfo(),
                    AudioBitrate = null, // Not available from MediaPlayer - will be hidden
                    AudioChannels = GetAudioChannels(),
                    AudioSampleRate = GetAudioSampleRate()
                };

                // Video Information
                if (session.NaturalVideoHeight > 0 && session.NaturalVideoWidth > 0)
                {
                    stats.Resolution = $"{session.NaturalVideoWidth}x{session.NaturalVideoHeight}";
                    stats.VideoInfo = $"Resolution: {stats.Resolution}";

                    // Add codec info if available
                    var videoCodec = GetVideoCodecInfo();
                    if (!string.IsNullOrEmpty(videoCodec))
                    {
                        stats.VideoInfo += $"\nCodec: {videoCodec}";
                    }

                    // Add frame rate if available
                    if (_mediaPlayer.PlaybackSession != null && session.PlaybackRate != 1.0)
                    {
                        stats.VideoInfo += $"\nPlayback Rate: {session.PlaybackRate:F1}x";
                    }
                }
                else
                {
                    stats.VideoInfo = "Type: Audio Only";
                    stats.Resolution = "--";
                }

                // Playback Information
                var position = session.Position;
                var duration = session.NaturalDuration;

                // Basic playback info
                stats.PlaybackInfo = "Player: MediaPlayerElement";

                // Determine play method
                var playMethod = GetPlayMethod();
                stats.PlaybackInfo += $"\nPlay method: {playMethod}";

                // Protocol
                var protocol = GetProtocol();
                if (!string.IsNullOrEmpty(protocol))
                {
                    stats.PlaybackInfo += $"\nProtocol: {protocol}";
                }

                // Position info
                stats.PlaybackInfo += $"\nPosition: {TimeFormattingHelper.FormatTime(position)} / {TimeFormattingHelper.FormatTime(duration)}";

                if (duration.TotalSeconds > 0)
                {
                    var percentage = position.TotalSeconds / duration.TotalSeconds * 100;
                    stats.PlaybackInfo += $" ({percentage:F1}%)";
                }

                // Buffer Information
                var bufferingProgress = GetSafeBufferingProgress(session);
                var downloadProgress = GetSafeDownloadProgress(session);

                if (bufferingProgress >= 0)
                {
                    stats.BufferInfo = $"Buffer: {bufferingProgress:F0}%";

                    if (downloadProgress >= 0 && downloadProgress != bufferingProgress)
                    {
                        stats.BufferInfo += $" | Download: {downloadProgress:F0}%";
                    }
                }

                // Playback State
                var state = session.PlaybackState;
                var playbackState = state switch
                {
                    MediaPlaybackState.Playing => "Playing",
                    MediaPlaybackState.Paused => "Paused",
                    MediaPlaybackState.Buffering => "Buffering",
                    MediaPlaybackState.Opening => "Opening",
                    _ => state.ToString()
                };

                stats.NetworkInfo = $"State: {playbackState}";

                // Bitrate Information (if available from media source)
                if (_mediaPlayer.Source is MediaPlaybackItem playbackItem)
                {
                    try
                    {
                        // Try to get bitrate from source properties
                        if (playbackItem.Source?.CustomProperties?.ContainsKey("Bitrate") == true)
                        {
                            var bitrate = playbackItem.Source.CustomProperties["Bitrate"] as string;
                            if (!string.IsNullOrEmpty(bitrate))
                            {
                                stats.BitrateInfo = $"Bitrate: {bitrate}";
                            }
                        }
                    }
                    catch
                    {
                        // Ignore errors getting bitrate
                    }
                }

                stats.SubtitleInfo = "Subtitles: N/A";

                _currentStats = stats;
                StatsUpdated?.Invoke(this, stats);
            }
            catch (Exception ex)
            {
                Logger.LogDebug($"UpdateStatistics exception: {ex.GetType().Name}");
            }
        }

        private double GetSafeBufferingProgress(MediaPlaybackSession session)
        {
            // Don't even try to access BufferingProgress for certain states or stream types
            // This avoids the InvalidCastException entirely
            if (session == null ||
                session.PlaybackState == MediaPlaybackState.None ||
                session.PlaybackState == MediaPlaybackState.Opening ||
                !IsBufferingSupported())
            {
                return -1;
            }

            try
            {
                return session.BufferingProgress * 100;
            }
            catch
            {
                // If it still fails, mark as unsupported for this session
                _isBufferingSupported = false;
                return -1;
            }
        }

        private double GetSafeDownloadProgress(MediaPlaybackSession session)
        {
            // Don't even try to access DownloadProgress for certain states or stream types
            // This avoids the InvalidCastException entirely
            if (session == null ||
                session.PlaybackState == MediaPlaybackState.None ||
                session.PlaybackState == MediaPlaybackState.Opening ||
                !IsDownloadSupported())
            {
                return -1;
            }

            try
            {
                return session.DownloadProgress * 100;
            }
            catch
            {
                // If it still fails, mark as unsupported for this session
                _isDownloadSupported = false;
                return -1;
            }
        }

        private bool _isBufferingSupported = true;
        private bool _isDownloadSupported = true;

        private bool IsBufferingSupported()
        {
            // Reset support flags when media changes
            if (_mediaPlayer?.Source != _lastMediaSource)
            {
                _lastMediaSource = _mediaPlayer?.Source;
                _isBufferingSupported = true;
                _isDownloadSupported = true;
            }
            return _isBufferingSupported;
        }

        private bool IsDownloadSupported()
        {
            return _isDownloadSupported;
        }

        private Windows.Media.Playback.IMediaPlaybackSource _lastMediaSource;


        private string GetVideoCodecInfo()
        {
            // Video codec info would need to be passed from MediaSourceInfo
            return "HEVC";
        }

        private string GetContainerFormat()
        {
            // Container format would need to be passed from MediaSourceInfo
            return "mkv";
        }

        private string GetVideoRangeType()
        {
            // HDR info would need to be passed from MediaSourceInfo
            return "SDR";
        }

        private string GetAudioCodecInfo()
        {
            // Audio codec info would need to be passed from MediaSourceInfo
            return "AC3";
        }

        private string GetAudioChannels()
        {
            // Audio channel info would need to be passed from MediaSourceInfo
            return "6";
        }

        private string GetAudioSampleRate()
        {
            // Sample rate would need to be passed from MediaSourceInfo
            return "48000 Hz";
        }

        private string GetPlayMethod()
        {
            // This would need MediaSourceInfo to determine
            return "Direct playing";
        }

        private string GetProtocol()
        {
            // This would need the stream URL to determine
            return "https";
        }
    }
}
