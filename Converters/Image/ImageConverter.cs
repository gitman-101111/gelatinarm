using System;
using System.Linq;
using Gelatinarm.Constants;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
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
                var authService = App.Current.Services?.GetService<IAuthenticationService>();
                var serverUrl = authService?.ServerUrl;
                var accessToken = authService?.AccessToken;

                if (string.IsNullOrEmpty(serverUrl))
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
                var logger = App.Current.Services?.GetService<ILogger<ImageConverter>>();
                logger?.LogDebug($"ImageConverter error: {ex.Message}");
            }
#else
            catch
            {
            }
#endif

            return null;
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
            var apiClient = App.Current.Services?.GetService<JellyfinApiClient>();
            var authService = App.Current.Services?.GetService<IAuthenticationService>();

            if (apiClient == null || authService == null)
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
                    return requestInfo.URI.ToString();
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
                    return requestInfo.URI.ToString();
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
                    return requestInfo.URI.ToString();
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
            var apiClient = App.Current.Services?.GetService<JellyfinApiClient>();
            var authService = App.Current.Services?.GetService<IAuthenticationService>();

            if (apiClient == null || authService == null)
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
                        url = requestInfo.URI.ToString();
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
                        url = requestInfo.URI.ToString();
                    }
                }
                // For movies, prefer backdrop
                else if (item.Type == BaseItemDto_Type.Movie)
                {
                    if (item.BackdropImageTags?.Any() == true)
                    {
                        var backdropTag = item.BackdropImageTags.First();
                        var requestInfo = apiClient.Items[item.Id.Value]
                            .Images["Backdrop/0"]
                            .ToGetRequestInformation(config =>
                            {
                                config.QueryParameters.Tag = backdropTag;
                                config.QueryParameters.Quality = MediaConstants.BACKDROP_QUALITY;
                                config.QueryParameters.MaxWidth = 600;
                            });
                        url = requestInfo.URI.ToString();
                    }
                    // Fall back to primary if no backdrop
                    else
                    {
                        url = GetPrimaryImageUrl(item, serverUrl);
                        // Adjust width for backdrop usage
                        if (!string.IsNullOrEmpty(url))
                        {
                            url = url.Replace("maxWidth=400", "maxWidth=600");
                        }
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
