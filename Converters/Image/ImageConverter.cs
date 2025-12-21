using System;
using System.Linq;
using Gelatinarm.Constants;
using Gelatinarm.Helpers;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Imaging;

namespace Gelatinarm.Converters.Image
{
    /// <summary>
    ///     Unified converter for displaying various image types for Jellyfin items
    /// </summary>
    public class ImageConverter : IValueConverter
    {
        public enum ImageType
        {
            Primary,
            Backdrop,
            Auto // Automatically determine based on context
        }

        private static T GetService<T>() where T : class
        {
            return ServiceLocator.GetService<T>();
        }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is not BaseItemDto item || item == null)
                {
                    return null;
                }

                // Parse image type from parameter
                var imageType = ImageType.Primary;
                if (parameter is string paramStr)
                {
                    if (Enum.TryParse<ImageType>(paramStr, true, out var parsedType))
                    {
                        imageType = parsedType;
                    }
                }
                if (!TryGetAuthContext(out var authService, out var serverUrl, out var accessToken))
                {
                    return null;
                }

                var url = GetImageUrl(item, imageType, serverUrl);

                // Create bitmap if we have a URL
                if (!string.IsNullOrEmpty(url))
                {
                    var bitmap = new BitmapImage { UriSource = new Uri(url) };

                    // Add auth token if available
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        bitmap.CreateOptions = BitmapCreateOptions.None;
                        // Using ApiKey query parameter for image URLs where we can't set HTTP headers
                        bitmap.UriSource = new Uri($"{url}&ApiKey={accessToken}");
                    }

                    return bitmap;
                }
            }
#if DEBUG
            catch (Exception ex)
            {
                GetLogger()?.LogDebug($"ImageConverter error: {ex.Message}");
            }
#else
            catch
            {
            }
#endif

            return null;
        }

        private static bool TryGetAuthContext(out IAuthenticationService authService, out string serverUrl, out string accessToken)
        {
            authService = GetService<IAuthenticationService>();
            serverUrl = authService?.ServerUrl;
            accessToken = authService?.AccessToken;
            return !string.IsNullOrEmpty(serverUrl);
        }

        private static ILogger<ImageConverter> GetLogger()
        {
            return GetService<ILogger<ImageConverter>>();
        }

        private static bool TryGetApiContext(out JellyfinApiClient apiClient, out IAuthenticationService authService)
        {
            apiClient = GetService<JellyfinApiClient>();
            authService = GetService<IAuthenticationService>();
            return apiClient != null && authService != null;
        }

        private string GetImageUrl(BaseItemDto item, ImageType imageType, string serverUrl)
        {
            switch (imageType)
            {
                case ImageType.Primary:
                    return GetPrimaryImageUrl(item, serverUrl);

                case ImageType.Backdrop:
                    return GetBackdropImageUrl(item, serverUrl);

                case ImageType.Auto:
                    // For Continue Watching scenarios, use backdrop for movies
                    if (item.Type == BaseItemDto_Type.Movie)
                    {
                        return GetBackdropImageUrl(item, serverUrl) ?? GetPrimaryImageUrl(item, serverUrl);
                    }

                    return GetPrimaryImageUrl(item, serverUrl);

                default:
                    return GetPrimaryImageUrl(item, serverUrl);
            }
        }

        private string GetPrimaryImageUrl(BaseItemDto item, string serverUrl)
        {
            if (!TryGetApiContext(out var apiClient, out var authService))
            {
                return null;
            }

            try
            {
                // Special handling for episodes - use series image
                if (item.Type == BaseItemDto_Type.Episode && !string.IsNullOrEmpty(item.SeriesPrimaryImageTag) &&
                    item.SeriesId.HasValue)
                {
                    var requestInfo = apiClient.Items[item.SeriesId.Value]
                        .Images["Primary"]
                        .ToGetRequestInformation(config =>
                        {
                            config.QueryParameters.Tag = item.SeriesPrimaryImageTag;
                            config.QueryParameters.Quality = MediaConstants.IMAGE_QUALITY;
                            config.QueryParameters.MaxWidth = 400;
                        });
                    return apiClient.BuildUri(requestInfo).ToString();
                }

                // Standard primary image handling
                if (item.ImageTags?.AdditionalData != null && item.ImageTags.AdditionalData.ContainsKey("Primary"))
                {
                    var imageTag = item.ImageTags.AdditionalData["Primary"]?.ToString();
                    var requestInfo = apiClient.Items[item.Id.Value]
                        .Images["Primary"]
                        .ToGetRequestInformation(config =>
                        {
                            if (!string.IsNullOrEmpty(imageTag))
                                config.QueryParameters.Tag = imageTag;
                            config.QueryParameters.Quality = MediaConstants.IMAGE_QUALITY;
                            config.QueryParameters.MaxWidth = 400;
                        });
                    return apiClient.BuildUri(requestInfo).ToString();
                }

                // Fallback to primary without tag
                if (item.Id != null)
                {
                    var requestInfo = apiClient.Items[item.Id.Value]
                        .Images["Primary"]
                        .ToGetRequestInformation(config =>
                        {
                            config.QueryParameters.Quality = MediaConstants.IMAGE_QUALITY;
                            config.QueryParameters.MaxWidth = 400;
                        });
                    return apiClient.BuildUri(requestInfo).ToString();
                }
            }
            catch (Exception)
            {
                // Fallback to null on SDK error
                return null;
            }

            return null;
        }

        private string GetBackdropImageUrl(BaseItemDto item, string serverUrl)
        {
            if (!TryGetApiContext(out var apiClient, out var authService))
            {
                return null;
            }

            string url = null;

            try
            {
                // For episodes and series, use primary image with larger width
                if (item.Type == BaseItemDto_Type.Episode || item.Type == BaseItemDto_Type.Series)
                {
                    if (item.ImageTags?.AdditionalData != null && item.ImageTags.AdditionalData.ContainsKey("Primary"))
                    {
                        var imageTag = item.ImageTags.AdditionalData["Primary"]?.ToString();
                        var requestInfo = apiClient.Items[item.Id.Value]
                            .Images["Primary"]
                            .ToGetRequestInformation(config =>
                            {
                                if (!string.IsNullOrEmpty(imageTag))
                                    config.QueryParameters.Tag = imageTag;
                                config.QueryParameters.Quality = MediaConstants.IMAGE_QUALITY;
                                config.QueryParameters.MaxWidth = 600;
                            });
                        url = apiClient.BuildUri(requestInfo).ToString();
                    }
                    else if (item.Id != null)
                    {
                        var requestInfo = apiClient.Items[item.Id.Value]
                            .Images["Primary"]
                            .ToGetRequestInformation(config =>
                            {
                                config.QueryParameters.Quality = MediaConstants.IMAGE_QUALITY;
                                config.QueryParameters.MaxWidth = 600;
                            });
                        url = apiClient.BuildUri(requestInfo).ToString();
                    }
                }
                // For movies, always try to get backdrop
                else if (item.Type == BaseItemDto_Type.Movie)
                {
                    // Try to get backdrop with tag if available
                    if (item.BackdropImageTags?.Any() == true)
                    {
                        var backdropTag = item.BackdropImageTags.FirstOrDefault();
                        if (!string.IsNullOrEmpty(backdropTag))
                        {
                            var requestInfo = apiClient.Items[item.Id.Value]
                                .Images["Backdrop"][0]
                                .ToGetRequestInformation(config =>
                                {
                                    config.QueryParameters.Tag = backdropTag;
                                    config.QueryParameters.Quality = MediaConstants.BACKDROP_QUALITY;
                                    config.QueryParameters.MaxWidth = 600;
                                });
                            url = apiClient.BuildUri(requestInfo).ToString();
                        }
                    }

                    // If no tag or tag failed, try to get backdrop without tag
                    // The server should still return the backdrop if it exists
                    if (string.IsNullOrEmpty(url) && item.Id != null)
                    {
                        var requestInfo = apiClient.Items[item.Id.Value]
                            .Images["Backdrop"][0]
                            .ToGetRequestInformation(config =>
                            {
                                config.QueryParameters.Quality = MediaConstants.BACKDROP_QUALITY;
                                config.QueryParameters.MaxWidth = 600;
                            });
                        url = apiClient.BuildUri(requestInfo).ToString();
                    }
                }
                // Other types use primary with backdrop dimensions
                else
                {
                    url = GetPrimaryImageUrl(item, serverUrl);
                    if (!string.IsNullOrEmpty(url))
                    {
                        url = url.Replace("maxWidth=400", "maxWidth=600");
                    }
                }
            }
            catch (Exception)
            {
                // Fallback to null on SDK error
                return null;
            }

            return url;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
