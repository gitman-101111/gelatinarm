using System;

namespace Gelatinarm.Helpers
{
    /// <summary>
    /// Helper methods for URL auth parameters.
    /// </summary>
    public static class UrlHelper
    {
        public static string AppendApiKey(string url, string accessToken)
        {
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(accessToken))
            {
                return url;
            }

            if (HasApiKey(url))
            {
                return url;
            }

            var separator = url.Contains("?") ? "&" : "?";
            return $"{url}{separator}ApiKey={Uri.EscapeDataString(accessToken)}";
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
