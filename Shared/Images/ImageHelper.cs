using System;
using System.Linq;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Server;
using Gelatinarm.SignIn;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;

namespace Gelatinarm.Shared.Images
{
    /// <summary>
    ///     Image URLs and tags. Images load from these URLs through BitmapImage (the XAML
    ///     converters and ImageLoadingService alike), cached by the platform.
    /// </summary>
    public static class ImageHelper
    {
        // Resolved when first needed: the helper is static and runs before and after sign-in
        private static ILogger Logger => ServiceLocator.GetRequiredService<ILogger<BaseService>>();

        public static string BuildImageUrl(Guid itemId, string imageType, int? width, string tag, int? height = null,
            int quality = ImageConstants.ImageQuality)
        {
            return BuildAuthenticatedUrl(apiClient => apiClient.Items[itemId]
                .Images[imageType]
                .ToGetRequestInformation(config =>
                {
                    config.QueryParameters.Quality = quality;

                    if (width.HasValue)
                    {
                        config.QueryParameters.MaxWidth = width.Value;
                    }

                    if (height.HasValue)
                    {
                        config.QueryParameters.MaxHeight = height.Value;
                    }

                    if (!string.IsNullOrEmpty(tag))
                    {
                        config.QueryParameters.Tag = tag;
                    }
                }), "image");
        }

        /// <summary>
        ///     The /UserImage endpoint (userId as a query parameter, not the path).
        ///     The endpoint serves the image as stored - no server-side resizing.
        /// </summary>
        public static string BuildUserImageUrl(Guid userId, string tag)
        {
            return BuildAuthenticatedUrl(apiClient => apiClient.UserImage.ToGetRequestInformation(config =>
            {
                config.QueryParameters.UserId = userId;

                if (!string.IsNullOrEmpty(tag))
                {
                    config.QueryParameters.Tag = tag;
                }
            }), "user image");
        }

        private static string BuildAuthenticatedUrl(Func<JellyfinApiClient, RequestInformation> buildRequest,
            string what)
        {
            try
            {
                var apiClient = ServiceLocator.GetRequiredService<JellyfinApiClient>();
                var authService = ServiceLocator.GetRequiredService<IAuthenticationService>();

                var serverUrl = authService.ServerUrl;
                if (string.IsNullOrEmpty(serverUrl) || serverUrl == "about:blank")
                {
                    Logger.LogWarning("ImageHelper: Server URL not yet configured, cannot build {What} URL", what);
                    return null;
                }

                // BuildUri applies the configured server; BitmapImage cannot send the SDK's auth
                // header, so the token goes in the query instead
                var url = apiClient.BuildUri(buildRequest(apiClient)).ToString();
                return UrlHelper.AppendApiKey(url, authService.AccessToken);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        ///     A playing track's artwork: its album's image when the server has one for the album,
        ///     else the track's own; null when neither has one.
        /// </summary>
        public static string GetTrackArtworkUrl(BaseItemDto track, int? size = null)
        {
            if (track?.AlbumId != null && !string.IsNullOrEmpty(track.AlbumPrimaryImageTag))
            {
                return BuildImageUrl(track.AlbumId.Value, ImageConstants.ImageTypePrimary, size,
                    track.AlbumPrimaryImageTag, size);
            }

            var ownTag = GetImageTag(track, ImageConstants.ImageTypePrimary);
            return track?.Id != null && ownTag != null
                ? BuildImageUrl(track.Id.Value, ImageConstants.ImageTypePrimary, size, ownTag, size)
                : null;
        }

        public static string GetImageTag(BaseItemDto item, string imageType)
        {
            if (imageType == ImageConstants.ImageTypeBackdrop && item?.BackdropImageTags is { Count: > 0 })
            {
                return item.BackdropImageTags.FirstOrDefault();
            }

            if (item?.ImageTags?.AdditionalData?.ContainsKey(imageType) == true)
            {
                return item.ImageTags.AdditionalData[imageType]?.ToString();
            }

            return null;
        }
    }
}
