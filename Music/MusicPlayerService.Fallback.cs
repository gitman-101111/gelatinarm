using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Server;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Music
{
    public partial class MusicPlayerService
    {
        private void OnMediaFailed(object sender, MediaPlayerFailedEventArgs args)
        {
            FireAndForget(() => HandleMediaFailedAsync(args), "HandleMediaFailed");
        }

        private async Task HandleMediaFailedAsync(MediaPlayerFailedEventArgs args)
        {
            try
            {
                var session = _mediaControlService.MediaPlayer.PlaybackSession;
                Logger.LogError("=== Media playback failed: {Error} - {ErrorMessage} (state {State}, at {Position}, {SinceStart:F0} ms after start) ===",
                    args.Error, args.ErrorMessage, session?.PlaybackState, session?.Position,
                    (DateTime.UtcNow - _lastPlaybackStartTime).TotalMilliseconds);

                var currentItem = _mediaControlService.CurrentItem;
                if (currentItem != null)
                {
                    var sourceUri = (_mediaControlService.MediaPlayer.Source as MediaPlaybackItem)?.Source?.Uri;
                    Logger.LogError("  Failed item: {ItemName} ({ItemType}, {ItemId}, {Container}) from {SourceUri}",
                        currentItem.Name, currentItem.Type, currentItem.Id, currentItem.Container,
                        UrlHelper.RedactApiKey(sourceUri?.ToString()));

                    // The console rejects some files it cannot open directly (typically embedded
                    // artwork over MaxDirectPlayEmbeddedArtworkPixels); the server can stream them
                    if (args.Error == MediaPlayerError.SourceNotSupported &&
                        currentItem.Type == BaseItemDto_Type.Audio && !_isInFallbackMode)
                    {
                        Logger.LogInformation("Attempting automatic transcoding fallback for audio playback");
                        await PlayItemWithTranscodingFallbackAsync(currentItem, _currentMediaSource).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("HandleMediaFailed", ErrorCategory.Media), false);
            }
        }

        private async Task PlayFallbackUrlAsync(BaseItemDto item, string mediaUrl)
        {
            var source = MediaSource.CreateFromUri(new Uri(mediaUrl));
            var playbackItem = new MediaPlaybackItem(source);

            _smtcSuppressStoppedUntilUtc = DateTime.UtcNow.AddSeconds(3);
            _mediaControlService.ClearMediaSource();
            await Task.Delay(MusicConstants.MediaSourceClearDelayMs).ConfigureAwait(false);

            await _mediaControlService.SetMediaSourceAsync(playbackItem, item).ConfigureAwait(false);
            _lastPlaybackStartTime = DateTime.UtcNow;
            _mediaControlService.Play();

            await StartPlaybackReportingAsync().ConfigureAwait(false);
        }

        private static bool HasOversizedEmbeddedArtwork(MediaSourceInfo mediaSource)
        {
            const int MaxEdge = MusicConstants.MaxDirectPlayEmbeddedArtworkPixels;
            return mediaSource?.MediaStreams?.Any(s => s.Type == MediaStream_Type.EmbeddedImage &&
                                                       (s.Width > MaxEdge || s.Height > MaxEdge)) == true;
        }

        private static bool IsLosslessSource(MediaSourceInfo mediaSource)
        {
            var codec = mediaSource?.MediaStreams?.FirstOrDefault(s => s.Type == MediaStream_Type.Audio)?.Codec
                        ?? mediaSource?.Container;
            return codec != null &&
                   (codec.Equals("flac", StringComparison.OrdinalIgnoreCase) ||
                    codec.Equals("alac", StringComparison.OrdinalIgnoreCase) ||
                    codec.StartsWith("pcm", StringComparison.OrdinalIgnoreCase) ||
                    codec.Equals("wav", StringComparison.OrdinalIgnoreCase));
        }

        private string BuildDirectAudioStreamUrl(BaseItemDto item, MediaSourceInfo mediaSource)
        {
            var request = _apiClient.Audio[item.Id.Value].Stream.ToGetRequestInformation(config =>
            {
                config.QueryParameters.Static = true;
                config.QueryParameters.DeviceId = _deviceService.GetDeviceId();
                config.QueryParameters.PlaySessionId = _currentPlaySessionId;
                if (!string.IsNullOrEmpty(mediaSource?.Id))
                {
                    config.QueryParameters.MediaSourceId = mediaSource.Id;
                }
            });
            return _apiClient.BuildUri(request).ToString();
        }

        /// <summary>
        ///     Streams <paramref name="mediaSource" /> re-encoded by the server, for tracks the
        ///     console cannot open directly (typically oversized embedded artwork). The caller has
        ///     already chosen the source. Runs once per track, so a failing stream does not loop.
        /// </summary>
        private async Task PlayItemWithTranscodingFallbackAsync(BaseItemDto item, MediaSourceInfo mediaSource)
        {
            try
            {
                Logger.LogDebug("=== Attempting transcoding fallback for: {ItemName} ===", item?.Name);

                if (item?.Id == null || mediaSource == null)
                {
                    Logger.LogError("Cannot play fallback - item ID or media source missing");
                    return;
                }

                if (_isInFallbackMode)
                {
                    Logger.LogError("Already in fallback mode - stopping to prevent loop");
                    return;
                }

                _isInFallbackMode = true;

                var accessToken = _authService.AccessToken;
                if (string.IsNullOrEmpty(_authService.ServerUrl) || string.IsNullOrEmpty(accessToken))
                {
                    Logger.LogError("Server URL or access token is not available");
                    _isInFallbackMode = false;
                    return;
                }

                // Lossless sources are re-encoded to FLAC, which keeps the audio
                // bit-perfect; the server's ffmpeg drops the picture either way (-vn).
                // Lossy sources keep the MP3 path below.
                if (IsLosslessSource(mediaSource))
                {
                    var flacRequest = _apiClient.Audio[item.Id.Value].Stream.ToGetRequestInformation(config =>
                    {
                        config.QueryParameters.Static = false;
                        config.QueryParameters.Container = "flac";
                        config.QueryParameters.AudioCodec = "flac";
                        // Keep surround: without a limit the server downmixes to stereo (-ac 2)
                        config.QueryParameters.MaxAudioChannels = 8;
                        config.QueryParameters.DeviceId = _deviceService.GetDeviceId();
                        config.QueryParameters.PlaySessionId = _currentPlaySessionId;
                        if (!string.IsNullOrEmpty(mediaSource.Id))
                        {
                            config.QueryParameters.MediaSourceId = mediaSource.Id;
                        }
                    });

                    var flacUrl = UrlHelper.AppendApiKey(_apiClient.BuildUri(flacRequest).ToString(), accessToken);
                    Logger.LogInformation("Lossless fallback (FLAC, artwork stripped): {MediaUrl}",
                        UrlHelper.RedactApiKey(flacUrl));
                    await PlayFallbackUrlAsync(item, flacUrl).ConfigureAwait(false);
                    return;
                }

                var requestInfo = _apiClient.Audio[item.Id.Value].Universal.ToGetRequestInformation(config =>
                {
                    config.QueryParameters.UserId = _userProfileService.GetCurrentUserGuid();
                    config.QueryParameters.DeviceId = _deviceService.GetDeviceId();

                    if (!string.IsNullOrEmpty(mediaSource.Id))
                    {
                        config.QueryParameters.MediaSourceId = mediaSource.Id;
                    }

                    // Force transcoding to MP3 to strip embedded artwork
                    // This ensures the server transcodes even if the source format is supported
                    config.QueryParameters.Container = Mp3Container;
                    config.QueryParameters.AudioCodec = "mp3";
                    config.QueryParameters.MaxStreamingBitrate = 320000;
                });

                // MediaSource.CreateFromUri sends no SDK headers, so the key goes in the URL
                var mediaUrl = UrlHelper.AppendApiKey(_apiClient.BuildUri(requestInfo).ToString(), accessToken);

                Logger.LogInformation("Lossy fallback (MP3, artwork stripped): {MediaUrl}", UrlHelper.RedactApiKey(mediaUrl));

                await PlayFallbackUrlAsync(item, mediaUrl).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("PlayItemWithTranscodingFallbackAsync", ErrorCategory.Media), false);
                _isInFallbackMode = false;
            }
        }
    }
}
