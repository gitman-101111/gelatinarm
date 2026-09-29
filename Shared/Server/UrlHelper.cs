using System;
using System.Text.RegularExpressions;

namespace Gelatinarm.Shared.Server
{
    /// <summary>
    ///     URLs the server hands out: resolving them, their access token, and whether a stream is HLS.
    /// </summary>
    public static class UrlHelper
    {
        // A pathological URL must not stall the player on a regex
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

        /// <summary>
        ///     A URL the server handed back (a media source's Path or TranscodingUrl): used as is
        ///     when absolute, otherwise taken as relative to the server.
        /// </summary>
        public static string ResolveServerUrl(string serverUrl, string pathOrUrl)
        {
            return pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? pathOrUrl
                : $"{serverUrl.TrimEnd('/')}{pathOrUrl}";
        }

        public static string AppendApiKey(string url, string accessToken)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(accessToken) || HasApiKey(url))
            {
                return url;
            }

            var separator = url.Contains("?") ? "&" : "?";
            return $"{url}{separator}ApiKey={Uri.EscapeDataString(accessToken)}";
        }

        /// <summary>
        ///     The URL with any access token hidden, for logging. Stream and image URLs carry
        ///     the token as ApiKey because MediaPlayer and image sources cannot send headers.
        /// </summary>
        public static string RedactApiKey(string url)
        {
            return string.IsNullOrEmpty(url)
                ? url
                : Regex.Replace(url, "(?<=[?&](?:ApiKey|api_key)=)[^&]*", "REDACTED", RegexOptions.IgnoreCase, RegexTimeout);
        }

        /// <summary>
        ///     Whether a stream URL is HLS: Jellyfin serves an HLS transcode as master.m3u8, and
        ///     the universal audio endpoint asks for one with transcodingProtocol=hls.
        /// </summary>
        public static bool IsHls(string url)
        {
            return url != null && (url.Contains(".m3u8") || url.Contains("transcodingProtocol=hls"));
        }

        /// <summary>
        ///     A query parameter's value, unescaped; null when the URL does not carry it
        /// </summary>
        public static string GetQueryParameter(string url, string key)
        {
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            var match = Regex.Match(url, $"[?&]{Regex.Escape(key)}=([^&]*)", RegexOptions.IgnoreCase, RegexTimeout);
            return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
        }

        public static bool HasApiKey(string url)
        {
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            return url.Contains("ApiKey=", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("api_key=", StringComparison.OrdinalIgnoreCase);
        }
    }
}
