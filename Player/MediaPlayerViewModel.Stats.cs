using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.UI.Xaml.Controls;
using CommunityToolkit.Mvvm.Input;
using Gelatinarm.Details;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Server;
using Gelatinarm.SignIn;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
{
    public partial class MediaPlayerViewModel
    {
        [RelayCommand]
        private void ToggleStats()
        {
            IsStatsVisible = !IsStatsVisible;
            if (IsStatsVisible)
            {
                _statsUpdateTimer.Start();
                UpdatePlaybackStats();
            }
            else
            {
                _statsUpdateTimer.Stop();
            }
        }

        private void OnStatsUpdateTimerTick(object sender, object e)
        {
            if (_isDisposed)
            {
                Logger.LogDebug("[STATS-TIMER] Timer fired after disposal, ignoring");
                return;
            }

            try
            {
                if (IsStatsVisible)
                {
                    UpdatePlaybackStats();
                }
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("OnStatsUpdateTimerTick", ErrorCategory.Media));
            }
        }

        private void UpdatePlaybackStats()
        {
            if (MediaPlayerElement?.MediaPlayer?.PlaybackSession == null)
            {
                return;
            }

            try
            {
                // Track changes and buffering recovery restart playback with a new source
                SyncCurrentStream();

                var session = MediaPlayerElement.MediaPlayer.PlaybackSession;
                CurrentStats = new PlaybackStats
                {
                    Player = "MediaPlayerElement",
                    PlayMethod = GetPlayMethod(),
                    Protocol = GetProtocol(),
                    StreamType = session.NaturalVideoHeight > 0 ? "Video" : "Audio",
                    Container = GetContainerFormat(),
                    VideoCodec = GetVideoCodecInfo(),
                    VideoRangeType = GetVideoRangeType(),
                    AudioCodec = GetAudioCodecInfo(),
                    AudioChannels = GetAudioChannels(),
                    AudioSampleRate = GetAudioSampleRate(),
                    // What the player decodes, which a transcode may have scaled
                    Resolution = session.NaturalVideoHeight > 0
                        ? $"{session.NaturalVideoWidth}x{session.NaturalVideoHeight}"
                        : "--",
                    FrameRate = GetFrameRate() ?? "--",
                    TranscodeReasons = GetTranscodeReasons() ?? "--"
                };
            }
            catch (Exception ex)
            {
                ErrorHandler.HandleError(ex, CreateErrorContext("UpdatePlaybackStats", ErrorCategory.Media));
            }
        }

        private string GetVideoCodecInfo()
        {
            var videoStream = StatsVideoStream;
            return videoStream?.Codec?.ToUpper();
        }

        private string GetContainerFormat()
        {
            return _statsMediaSource?.Container?.ToUpper();
        }

        private string GetVideoRangeType()
        {
            var videoStream = StatsVideoStream;
            if (videoStream?.VideoRangeType != null)
            {
                return videoStream.VideoRangeType.ToString();
            }

            if (videoStream?.ColorTransfer != null)
            {
                var transfer = videoStream.ColorTransfer.ToLower();
                if (transfer.Contains("smpte2084") || transfer.Contains("st2084"))
                {
                    return "HDR10";
                }

                if (transfer.Contains("arib-std-b67") || transfer.Contains("hlg"))
                {
                    return "HLG";
                }
            }

            if (!string.IsNullOrEmpty(videoStream?.CodecTag) && videoStream.CodecTag.Contains("dovi"))
            {
                return "Dolby Vision";
            }

            return "SDR";
        }

        private string GetAudioCodecInfo()
        {
            var audioStream = StatsAudioStream;

            return audioStream?.Codec?.ToUpper();
        }

        private string GetAudioChannels()
        {
            var audioStream = StatsAudioStream;

            if (audioStream?.Channels is int channels)
            {
                var layout = MediaLabels.ChannelLayout(channels);
                return layout != null ? $"{channels} ({layout})" : channels.ToString();
            }

            if (!string.IsNullOrEmpty(audioStream?.ChannelLayout))
            {
                return audioStream.ChannelLayout;
            }

            return null;
        }

        private string GetAudioSampleRate()
        {
            var audioStream = StatsAudioStream;

            return audioStream?.SampleRate != null ? $"{audioStream.SampleRate} Hz" : null;
        }

        private string GetPlayMethod()
        {
            if (_statsMediaSource == null)
            {
                return "Unknown";
            }

            // The direct-play path clears TranscodingUrl, so its absence means the player is
            // reading the file itself. Anything else is the server's stream; only the server
            // knows whether it copied or re-encoded each track, so ask it.
            if (string.IsNullOrEmpty(_statsMediaSource.TranscodingUrl))
            {
                return "Direct playing";
            }

            var info = _serverTranscodingInfo;
            if (info == null)
            {
                return "Server stream (remux or transcode; details not reported)";
            }

            var video = info.IsVideoDirect ? "video copied" : $"video to {info.VideoCodec ?? "?"}";
            var audio = info.IsAudioDirect ? "audio copied" : $"audio to {info.AudioCodec ?? "?"}";
            var kind = info.IsVideoDirect && info.IsAudioDirect ? "Remuxing" : "Transcoding";
            return $"{kind} ({video}, {audio})";
        }

        private string GetTranscodeReasons()
        {
            return UrlHelper.GetQueryParameter(_statsMediaSource?.TranscodingUrl, "TranscodeReasons")
                ?.Replace(",", ", ");
        }

        /// <summary>
        ///     Takes on the stream actually playing: the HLS handling and the stats overlay follow
        ///     it. Every playback start or restart creates a new source, so a different reference
        ///     means a new stream.
        /// </summary>
        private void SyncCurrentStream()
        {
            var current = _playbackControlService.GetCurrentMediaSource();
            if (current != null && !ReferenceEquals(current, _statsMediaSource))
            {
                _statsMediaSource = current;
                _sessionState.IsHlsStream = UrlHelper.IsHls(current.TranscodingUrl);
                StartServerTranscodingInfoPoll();
            }
        }

        /// <summary>
        ///     Asks the server what it is doing to the current stream (this device's session
        ///     TranscodingInfo). The server only reports it while its ffmpeg job runs, so this
        ///     starts with the stream, polls briefly, and keeps the first answer.
        /// </summary>
        private void StartServerTranscodingInfoPoll()
        {
            _serverTranscodingInfo = null;
            if (string.IsNullOrEmpty(_statsMediaSource?.TranscodingUrl))
            {
                return;
            }

            var mediaSource = _statsMediaSource;
            var itemId = CurrentItem?.Id;
            FireAndForget(async () =>
            {
                if (itemId == null)
                {
                    return;
                }

                for (var attempt = 0; attempt < PlayerConstants.StatsTranscodingInfoPollAttempts; attempt++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(PlayerConstants.StatsTranscodingInfoPollSeconds))
                        .ConfigureAwait(false);
                    if (!ReferenceEquals(mediaSource, _statsMediaSource))
                    {
                        return; // the stream changed; its own poll takes over
                    }

                    var info = await FindServerStreamDetailsAsync(itemId.Value).ConfigureAwait(false);
                    if (info != null)
                    {
                        _serverTranscodingInfo = info;
                        Logger.LogInformation(
                            "Server transcoding info: video direct={IsVideoDirect} ({VideoCodec}), audio direct={IsAudioDirect} ({AudioCodec})",
                            info.IsVideoDirect, info.VideoCodec, info.IsAudioDirect, info.AudioCodec);
                        return;
                    }
                }

                Logger.LogInformation("Server did not report transcoding info for this stream");
            });
        }

        /// <summary>
        ///     Reads the server's session list as raw JSON rather than through the SDK: the
        ///     SDK's TranscodingInfo model fails to deserialize this server's reply (a field it
        ///     types as a single enum arrives as an array), which loses the whole response.
        ///     The session is matched by what it is playing, not by device ID: the server keys
        ///     sessions to the device recorded at sign-in.
        /// </summary>
        private static async Task<ServerStreamDetails> FindServerStreamDetailsAsync(Guid itemId)
        {
            var auth = GetRequiredService<IAuthenticationService>();
            var httpClientFactory = GetRequiredService<IHttpClientFactory>();
            if (string.IsNullOrEmpty(auth.ServerUrl))
            {
                return null;
            }

            var url = UrlHelper.AppendApiKey($"{auth.ServerUrl.TrimEnd('/')}/Sessions", auth.AccessToken);
            var client = httpClientFactory.CreateClient(SystemConstants.JellyfinHttpClientName);
            var json = await client.GetStringAsync(url).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var session in document.RootElement.EnumerateArray())
            {
                if (!session.TryGetProperty("NowPlayingItem", out var nowPlaying) ||
                    !nowPlaying.TryGetProperty("Id", out var idElement) ||
                    !Guid.TryParse(idElement.GetString(), out var playingId) ||
                    playingId != itemId ||
                    !session.TryGetProperty("TranscodingInfo", out var transcoding) ||
                    transcoding.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                return new ServerStreamDetails(
                    ReadBool(transcoding, "IsVideoDirect"),
                    ReadBool(transcoding, "IsAudioDirect"),
                    ReadString(transcoding, "VideoCodec"),
                    ReadString(transcoding, "AudioCodec"));
            }

            return null;
        }

        private static bool ReadBool(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        }

        private static string ReadString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private sealed class ServerStreamDetails
        {
            public ServerStreamDetails(bool isVideoDirect, bool isAudioDirect, string videoCodec, string audioCodec)
            {
                IsVideoDirect = isVideoDirect;
                IsAudioDirect = isAudioDirect;
                VideoCodec = videoCodec;
                AudioCodec = audioCodec;
            }

            public bool IsVideoDirect { get; }
            public bool IsAudioDirect { get; }
            public string VideoCodec { get; }
            public string AudioCodec { get; }
        }

        // A server stream is HLS or a progressive HTTP transcode; a direct play reads the file over
        // the server address's own scheme
        private string GetProtocol()
        {
            if (!string.IsNullOrEmpty(_statsMediaSource?.TranscodingUrl))
            {
                return UrlHelper.IsHls(_statsMediaSource.TranscodingUrl) ? "HLS" : "HTTP";
            }

            var serverUrl = GetRequiredService<IAuthenticationService>().ServerUrl;
            return serverUrl?.StartsWith("https", StringComparison.OrdinalIgnoreCase) == true ? "HTTPS" : "HTTP";
        }

        private string GetFrameRate()
        {
            var videoStream = StatsVideoStream;
            if (videoStream?.RealFrameRate != null)
            {
                return $"{videoStream.RealFrameRate:F2} fps";
            }

            if (videoStream?.AverageFrameRate != null)
            {
                return $"{videoStream.AverageFrameRate:F2} fps";
            }

            return null;
        }
    }
}
