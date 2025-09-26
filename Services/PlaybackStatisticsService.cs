using System;
using System.Linq;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Gelatinarm.Models;
using Jellyfin.Sdk.Generated.Models;
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
        private MediaSourceInfo _currentMediaSource;

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

        public void SetMediaSourceInfo(MediaSourceInfo mediaSource)
        {
            _currentMediaSource = mediaSource;
            if (mediaSource != null)
            {
                Logger.LogInformation($"MediaSourceInfo set - DirectPlay: {mediaSource.SupportsDirectPlay}, DirectStream: {mediaSource.SupportsDirectStream}, Transcoding: {mediaSource.SupportsTranscoding}");
            }
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
                    LastUpdated = DateTime.Now,
                    Player = "MediaPlayerElement",
                    PlayMethod = GetPlayMethod(),
                    Protocol = GetProtocol(),
                    StreamType = session.NaturalVideoHeight > 0 ? "Video" : "Audio",
                    Container = GetContainerFormat(),
                    VideoCodec = GetVideoCodecInfo(),
                    VideoRangeType = GetVideoRangeType(),
                    AudioCodec = GetAudioCodecInfo(),
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

                    // Add actual frame rate from media stream
                    var frameRate = GetFrameRate();
                    if (!string.IsNullOrEmpty(frameRate))
                    {
                        stats.VideoInfo += $"\nFrame Rate: {frameRate}";
                    }

                    // Add playback speed if not normal
                    if (session.PlaybackRate != 1.0)
                    {
                        stats.VideoInfo += $"\nPlayback Speed: {session.PlaybackRate:F1}x";
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

                // Bitrate Information from MediaSourceInfo
                if (_currentMediaSource?.Bitrate != null)
                {
                    var bitrateMbps = _currentMediaSource.Bitrate.Value / 1_000_000.0;
                    stats.BitrateInfo = $"Bitrate: {bitrateMbps:F1} Mbps";
                }

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
            if (_currentMediaSource?.MediaStreams == null)
            {
                return null;
            }

            // Find the video stream
            var videoStream = _currentMediaSource.MediaStreams
                .FirstOrDefault(s => s.Type == MediaStream_Type.Video);

            return videoStream?.Codec?.ToUpper();
        }

        private string GetContainerFormat()
        {
            return _currentMediaSource?.Container?.ToUpper();
        }

        private string GetVideoRangeType()
        {
            if (_currentMediaSource?.MediaStreams == null)
            {
                return null;
            }

            // Find the video stream
            var videoStream = _currentMediaSource.MediaStreams
                .FirstOrDefault(s => s.Type == MediaStream_Type.Video);

            // Check for HDR metadata in video stream
            if (videoStream?.VideoRangeType != null)
            {
                // Convert enum to string representation
                return videoStream.VideoRangeType.ToString();
            }

            // Check color transfer characteristic for HDR detection
            if (videoStream?.ColorTransfer != null)
            {
                var transfer = videoStream.ColorTransfer.ToLower();
                if (transfer.Contains("smpte2084") || transfer.Contains("st2084"))
                    return "HDR10";
                if (transfer.Contains("arib-std-b67") || transfer.Contains("hlg"))
                    return "HLG";
            }

            // Check for Dolby Vision
            if (videoStream?.CodecTag != null && videoStream.CodecTag.Contains("dovi"))
            {
                return "Dolby Vision";
            }

            return "SDR";
        }

        private string GetAudioCodecInfo()
        {
            if (_currentMediaSource?.MediaStreams == null)
            {
                return null;
            }

            // Find the default or first audio stream
            var audioStream = _currentMediaSource.MediaStreams
                .FirstOrDefault(s => s.Type == MediaStream_Type.Audio && (s.IsDefault == true || s.Index == _currentMediaSource.DefaultAudioStreamIndex))
                ?? _currentMediaSource.MediaStreams.FirstOrDefault(s => s.Type == MediaStream_Type.Audio);

            return audioStream?.Codec?.ToUpper();
        }

        private string GetAudioChannels()
        {
            if (_currentMediaSource?.MediaStreams == null)
            {
                return null;
            }

            // Find the default or first audio stream
            var audioStream = _currentMediaSource.MediaStreams
                .FirstOrDefault(s => s.Type == MediaStream_Type.Audio && (s.IsDefault == true || s.Index == _currentMediaSource.DefaultAudioStreamIndex))
                ?? _currentMediaSource.MediaStreams.FirstOrDefault(s => s.Type == MediaStream_Type.Audio);

            if (audioStream?.Channels != null)
            {
                // Format channel count with description
                return audioStream.Channels switch
                {
                    1 => "1 (Mono)",
                    2 => "2 (Stereo)",
                    6 => "6 (5.1)",
                    8 => "8 (7.1)",
                    _ => audioStream.Channels.ToString()
                };
            }

            // Check channel layout if available
            if (!string.IsNullOrEmpty(audioStream?.ChannelLayout))
            {
                return audioStream.ChannelLayout;
            }

            return null;
        }

        private string GetAudioSampleRate()
        {
            if (_currentMediaSource?.MediaStreams == null)
            {
                return null;
            }

            // Find the default or first audio stream
            var audioStream = _currentMediaSource.MediaStreams
                .FirstOrDefault(s => s.Type == MediaStream_Type.Audio && (s.IsDefault == true || s.Index == _currentMediaSource.DefaultAudioStreamIndex))
                ?? _currentMediaSource.MediaStreams.FirstOrDefault(s => s.Type == MediaStream_Type.Audio);

            if (audioStream?.SampleRate != null)
            {
                return $"{audioStream.SampleRate} Hz";
            }

            return null;
        }

        private string GetPlayMethod()
        {
            if (_currentMediaSource == null)
            {
                return "Unknown";
            }

            // Check for Direct Play first
            if (_currentMediaSource.SupportsDirectPlay == true)
            {
                return "Direct playing";
            }

            // Check for Direct Stream
            if (_currentMediaSource.SupportsDirectStream == true)
            {
                return "Direct streaming";
            }

            // If transcoding, check if it's actually just remuxing
            if (_currentMediaSource.SupportsTranscoding == true)
            {
                // Check if the transcoding URL indicates remuxing (both video and audio are being copied)
                // When remuxing, the URL typically won't have video/audio transcoding parameters
                // or will have indicators that streams are being copied
                if (!string.IsNullOrEmpty(_currentMediaSource.TranscodingUrl))
                {
                    var url = _currentMediaSource.TranscodingUrl.ToLower();

                    // Check for common transcoding parameters that indicate actual transcoding
                    bool hasVideoTranscode = url.Contains("videocodec=") && !url.Contains("videocodec=copy");
                    bool hasAudioTranscode = url.Contains("audiocodec=") && !url.Contains("audiocodec=copy");

                    // Also check TranscodeReasons - if it's only for container or protocol reasons, it's remuxing
                    bool isOnlyContainerReason = url.Contains("transcodereasons=containernotsupported") ||
                                                  url.Contains("transcodereasons=directplayerror");

                    // If neither video nor audio is being transcoded, or only container reasons, it's remuxing
                    if ((!hasVideoTranscode && !hasAudioTranscode) || isOnlyContainerReason)
                    {
                        return "Remuxing";
                    }
                }

                return "Transcoding";
            }

            return "Unknown";
        }

        private string GetProtocol()
        {
            // Determine protocol based on transcoding URL or direct play
            if (!string.IsNullOrEmpty(_currentMediaSource?.TranscodingUrl))
            {
                if (_currentMediaSource.TranscodingUrl.Contains(".m3u8"))
                {
                    return "HLS";
                }
                else if (_currentMediaSource.TranscodingUrl.Contains(".mpd"))
                {
                    return "DASH";
                }
                return "HTTP";
            }

            // Direct play typically uses HTTPS
            if (_currentMediaSource?.SupportsDirectPlay == true)
            {
                return "HTTPS";
            }

            return null;
        }

        private string GetFrameRate()
        {
            if (_currentMediaSource?.MediaStreams == null)
            {
                return null;
            }

            // Find the video stream
            var videoStream = _currentMediaSource.MediaStreams
                .FirstOrDefault(s => s.Type == MediaStream_Type.Video);

            if (videoStream?.RealFrameRate != null)
            {
                return $"{videoStream.RealFrameRate:F2} fps";
            }
            else if (videoStream?.AverageFrameRate != null)
            {
                return $"{videoStream.AverageFrameRate:F2} fps";
            }

            return null;
        }
    }
}
