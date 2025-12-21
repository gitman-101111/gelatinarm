using System;
using System.Collections.Generic;
using System.Linq;
using Gelatinarm.Models;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Services
{
    internal sealed class PlaybackSourceResolver
    {
        private readonly ILogger _logger;
        private readonly JellyfinApiClient _apiClient;
        private readonly IAuthenticationService _authService;
        private readonly IUnifiedDeviceService _deviceService;

        public PlaybackSourceResolver(
            ILogger logger,
            JellyfinApiClient apiClient,
            IAuthenticationService authService,
            IUnifiedDeviceService deviceService)
        {
            _logger = logger;
            _apiClient = apiClient;
            _authService = authService;
            _deviceService = deviceService;
        }

        public MediaSourceInfo SelectBestMediaSource(IReadOnlyList<MediaSourceInfo> sources)
        {
            if (sources == null || sources.Count == 0)
            {
                _logger.LogError($"SelectBestMediaSource: No media sources available (count: {sources?.Count ?? 0})");
                return null;
            }

            _logger.LogInformation($"SelectBestMediaSource: Evaluating {sources.Count} media sources");

            // Log details of each source
            foreach (var source in sources)
            {
                _logger.LogInformation($"  Source: Id={source.Id}, DirectPlay={source.SupportsDirectPlay}, DirectStream={source.SupportsDirectStream}, Transcoding={source.SupportsTranscoding}");
                _logger.LogInformation($"    TranscodingUrl: {source.TranscodingUrl}");
                _logger.LogInformation($"    Path: {source.Path}");
                _logger.LogInformation($"    Container: {source.Container}");
                _logger.LogInformation($"    Bitrate: {source.Bitrate}");
            }

            // Prefer direct play sources
            var directPlaySource = sources.FirstOrDefault(s => s.SupportsDirectPlay == true);
            if (directPlaySource != null)
            {
                _logger.LogInformation($"Selected DirectPlay source: {directPlaySource.Id}");
                return directPlaySource;
            }

            // Then prefer direct stream
            var directStreamSource = sources.FirstOrDefault(s => s.SupportsDirectStream == true);
            if (directStreamSource != null)
            {
                _logger.LogInformation($"Selected DirectStream source: {directStreamSource.Id}");
                return directStreamSource;
            }

            // Finally, use transcoding
            var transcodingSource = sources.FirstOrDefault();
            if (transcodingSource != null)
            {
                _logger.LogInformation($"Selected Transcoding source: {transcodingSource.Id}");
            }
            return transcodingSource;
        }

        public string BuildStreamUrl(
            MediaSourceInfo mediaSource,
            BaseItemDto currentItem,
            MediaPlaybackParams playbackParams,
            string playSessionId)
        {
            string url;

            // If we have a transcoding URL from the server, use it
            if (!string.IsNullOrEmpty(mediaSource.TranscodingUrl))
            {
                // Transcoding URL is relative, need to prepend server URL
                var baseUrl = _authService.ServerUrl?.TrimEnd('/') ?? "";
                url = $"{baseUrl}{mediaSource.TranscodingUrl}";

                // Server-provided transcoding URLs are complete - do not modify them
                // The server already includes all necessary parameters
                // IMPORTANT: We don't modify URLs here to maintain SDK compatibility
            }
            else if (!string.IsNullOrEmpty(GetDirectStreamUrl(mediaSource)))
            {
                // Direct stream URL from server
                var baseUrl = _authService.ServerUrl?.TrimEnd('/') ?? "";
                url = $"{baseUrl}{GetDirectStreamUrl(mediaSource)}";
            }
            else
            {
                // Use SDK to build direct play URL
                if (!currentItem?.Id.HasValue ?? true)
                {
                    _logger.LogError("Cannot build direct play URL - current item has no ID");
                    throw new InvalidOperationException("Current item has no ID");
                }

                // Build URL using SDK for direct streaming
                var requestInfo = _apiClient.Videos[currentItem.Id.Value].Stream.ToGetRequestInformation(config =>
                {
                    config.QueryParameters.Static = true;
                    config.QueryParameters.MediaSourceId = mediaSource.Id;
                    config.QueryParameters.PlaySessionId = playSessionId;
                    config.QueryParameters.DeviceId = _deviceService.GetDeviceId();

                    // Add stream indices if present
                    if (playbackParams?.AudioStreamIndex >= 0)
                    {
                        config.QueryParameters.AudioStreamIndex = playbackParams.AudioStreamIndex.Value;
                    }

                    if (playbackParams?.SubtitleStreamIndex >= 0)
                    {
                        config.QueryParameters.SubtitleStreamIndex = playbackParams.SubtitleStreamIndex.Value;
                    }
                });

                var builtUri = _apiClient.BuildUri(requestInfo);
                url = builtUri.ToString();
            }

            // For transcoding URLs, we may need to add stream indices if not already present
            if (!string.IsNullOrEmpty(mediaSource.TranscodingUrl))
            {
                // Add AudioStreamIndex if present and not already in URL
                if (playbackParams?.AudioStreamIndex.HasValue == true &&
                    playbackParams.AudioStreamIndex.Value >= 0 &&
                    !url.Contains("AudioStreamIndex="))
                {
                    var separator = url.Contains('?') ? "&" : "?";
                    url += $"{separator}AudioStreamIndex={playbackParams.AudioStreamIndex.Value}";
                    _logger.LogInformation($"Added AudioStreamIndex={playbackParams.AudioStreamIndex.Value} to transcoding URL");
                }

                // Add SubtitleStreamIndex if present and not already in URL
                if (playbackParams?.SubtitleStreamIndex.HasValue == true &&
                    playbackParams.SubtitleStreamIndex.Value >= 0 &&
                    !url.Contains("SubtitleStreamIndex="))
                {
                    var separator = url.Contains('?') ? "&" : "?";
                    url += $"{separator}SubtitleStreamIndex={playbackParams.SubtitleStreamIndex.Value}&SubtitleMethod=Encode";
                    _logger.LogInformation($"Added SubtitleStreamIndex={playbackParams.SubtitleStreamIndex.Value} to transcoding URL");
                }
            }

            // Server-provided URLs already contain authentication (ApiKey parameter)
            // SDK-generated URLs use HTTP headers for authentication
            // Do not add duplicate API keys

            // Log resume handling approach
            if (playbackParams?.StartPositionTicks > 0)
            {
                var resumeTime = TimeSpan.FromTicks(playbackParams.StartPositionTicks.Value);

                // Tiered approach for resume:
                // 1. StartTimeTicks was already sent in PlaybackInfoDto (always sent first)
                // 2. Client-side seek will be applied after playback starts
                // 3. For HLS, we'll track manifest offset if server creates new manifest

                _logger.LogInformation($"[RESUME-STRATEGY] StartTimeTicks sent to server: {playbackParams.StartPositionTicks.Value} ({resumeTime:hh\\:mm\\:ss})");

                if (mediaSource.TranscodingUrl?.Contains(".m3u8") == true)
                {
                    _logger.LogInformation("[RESUME-STRATEGY] HLS stream detected - will use client-side seek with offset tracking if server doesn't honor StartTimeTicks");
                }
                else
                {
                    _logger.LogInformation("[RESUME-STRATEGY] Non-HLS stream - will use client-side seek if server doesn't honor StartTimeTicks");
                }
            }

            // Validate the final URL
            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                _logger.LogError($"Invalid URL generated: {url}");
                throw new InvalidOperationException($"Generated URL is invalid: {url}");
            }

            _logger.LogInformation($"Stream URL generated: {url}");
            return url;
        }

        public static string GetDirectStreamUrl(MediaSourceInfo source)
        {
            // DirectStreamUrl is not a direct property on MediaSourceInfo in the SDK
            // It needs to be constructed or accessed differently
            return source?.Path;
        }
    }
}
