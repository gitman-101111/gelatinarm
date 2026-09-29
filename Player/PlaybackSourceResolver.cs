using System;
using System.Collections.Generic;
using System.Linq;
using Gelatinarm.Shared.Device;
using Gelatinarm.Shared.Server;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Player
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
                _logger.LogError("SelectBestMediaSource: No media sources available (count: {SourcesCount})", sources?.Count ?? 0);
                return null;
            }

            _logger.LogDebug("SelectBestMediaSource: Evaluating {SourcesCount} media sources", sources.Count);

            foreach (var source in sources)
            {
                _logger.LogDebug(
                    "  Source {SourceId}: {Container}, {Bitrate} bps; DirectPlay {DirectPlay}, DirectStream {DirectStream}, " +
                    "Transcoding {Transcoding}; {Path}; {TranscodingUrl}", source.Id, source.Container, source.Bitrate,
                    source.SupportsDirectPlay, source.SupportsDirectStream, source.SupportsTranscoding, source.Path,
                    UrlHelper.RedactApiKey(source.TranscodingUrl));
            }

            var directPlaySource = sources.FirstOrDefault(s => s.SupportsDirectPlay == true);
            if (directPlaySource != null)
            {
                _logger.LogInformation("Selected DirectPlay source: {DirectPlaySourceId}", directPlaySource.Id);
                return directPlaySource;
            }

            var directStreamSource = sources.FirstOrDefault(s => s.SupportsDirectStream == true);
            if (directStreamSource != null)
            {
                _logger.LogInformation("Selected DirectStream source: {DirectStreamSourceId}", directStreamSource.Id);
                return directStreamSource;
            }

            var transcodingSource = sources[0];
            _logger.LogInformation("Selected Transcoding source: {TranscodingSourceId}", transcodingSource.Id);
            return transcodingSource;
        }

        public string BuildStreamUrl(
            MediaSourceInfo mediaSource,
            MediaPlaybackParams playbackParams,
            string playSessionId)
        {
            string url;

            if (!string.IsNullOrEmpty(mediaSource.TranscodingUrl))
            {
                // The server's transcoding URL carries every parameter; only the host is added
                url = UrlHelper.ResolveServerUrl(_authService.ServerUrl, mediaSource.TranscodingUrl);
            }
            else
            {
                // Direct play: the file itself, from the static stream endpoint
                var currentItem = playbackParams?.Item;
                if (currentItem?.Id.HasValue != true)
                {
                    throw new InvalidOperationException("Cannot build the direct play URL: the current item has no ID");
                }

                var requestInfo = _apiClient.Videos[currentItem.Id.Value].Stream.ToGetRequestInformation(config =>
                {
                    config.QueryParameters.Static = true;
                    config.QueryParameters.MediaSourceId = mediaSource.Id;
                    config.QueryParameters.PlaySessionId = playSessionId;
                    config.QueryParameters.DeviceId = _deviceService.GetDeviceId();

                    if (playbackParams?.AudioStreamIndex >= 0)
                    {
                        config.QueryParameters.AudioStreamIndex = playbackParams.AudioStreamIndex.Value;
                    }

                    if (playbackParams?.SubtitleStreamIndex >= 0)
                    {
                        config.QueryParameters.SubtitleStreamIndex = playbackParams.SubtitleStreamIndex.Value;
                    }
                });

                // MediaPlayer cannot send the SDK's auth header, so the token goes in the URL
                url = UrlHelper.AppendApiKey(_apiClient.BuildUri(requestInfo).ToString(), _authService.AccessToken);
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException($"Generated URL is invalid: {UrlHelper.RedactApiKey(url)}");
            }

            _logger.LogInformation("Stream URL generated: {Url}", UrlHelper.RedactApiKey(url));
            return url;
        }
    }
}
