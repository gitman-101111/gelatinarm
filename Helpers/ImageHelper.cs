using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Gelatinarm.Services;
using Jellyfin.Sdk;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Gelatinarm.Helpers
{
    /// <summary>
    ///     Consolidated helper class for all image-related operations including URL building, caching, and image source
    ///     retrieval
    /// </summary>
    public static class ImageHelper
    {
        private const string IMAGE_CACHE_PROVIDER = "ImageCache";
        private static ICacheProvider _imageCacheProvider;
        private static ICacheManagerService _cacheManager;
        private static ILogger _logger;

        static ImageHelper()
        {
            AsyncHelper.FireAndForget(() => InitializeCacheAsync(), _logger, typeof(ImageHelper));
        }

        private static async Task InitializeCacheAsync()
        {
            try
            {
                _logger = App.Current.Services?.GetService<ILogger<BaseService>>();
                _cacheManager = App.Current.Services?.GetService<ICacheManagerService>();

                // Initialize and register the file-based cache provider for images
                var imageCacheProvider = new FileCacheProvider(
                    App.Current.Services?.GetService<ILogger<FileCacheProvider>>(),
                    "ImageCache"
                );
                await imageCacheProvider.InitializeAsync();

                _imageCacheProvider = imageCacheProvider;
                _cacheManager?.RegisterCacheProvider(IMAGE_CACHE_PROVIDER, imageCacheProvider);

                _logger?.LogInformation("Image cache provider initialized and registered");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize image cache provider");
            }
        }

        #region Image Source Methods (formerly ImageServiceHelper)

        /// <summary>
        ///     Get an ImageSource for a media item with caching support
        /// </summary>
        public static async Task<ImageSource> GetImageSourceAsync(BaseItemDto item, string imageType, int? width = null,
            int? height = null)
        {
            if (item?.Id == null)
            {
                return null;
            }

            // Get the image tag for cache busting
            var imageTag = GetImageTag(item, imageType);
            var cacheKey = GenerateCacheKey(item.Id.ToString(), imageType, width, height, 90, imageTag);

            // Try to get from cache first
            var cachedImage = await GetCachedImageAsync(cacheKey);
            if (cachedImage != null)
            {
                return cachedImage;
            }

            // Build URL and download
            var imageUrl = BuildImageUrl(item.Id.Value, imageType, width, height, imageTag);
            if (string.IsNullOrEmpty(imageUrl))
            {
                return null;
            }

            return await DownloadAndCacheImageAsync(imageUrl, cacheKey);
        }

        /// <summary>
        ///     Get an image by URL with caching support
        /// </summary>
        public static async Task<BitmapImage> GetImageByUrlAsync(string imageUrl)
        {
            if (string.IsNullOrEmpty(imageUrl))
            {
                return null;
            }

            var cacheKey = GenerateCacheKeyFromUrl(imageUrl);

            // Try to get from cache first
            var cachedImage = await GetCachedImageAsync(cacheKey);
            if (cachedImage != null)
            {
                return cachedImage;
            }

            // Download and cache the image
            return await DownloadAndCacheImageAsync(imageUrl, cacheKey);
        }

        /// <summary>
        ///     Clear the image cache
        /// </summary>
        public static async Task ClearCacheAsync()
        {
            if (_imageCacheProvider == null)
            {
                return;
            }

            try
            {
                await _imageCacheProvider.ClearAsync();
                _logger?.LogInformation("Image cache cleared");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to clear image cache");
            }
        }


        #endregion

        #region URL Building Methods (formerly ImageUrlHelper)

        /// <summary>
        ///     Build image URL for a media item
        /// </summary>
        public static string BuildImageUrl(Guid itemId, string imageType, int? width = null, int? height = null,
            string tag = null)
        {
            try
            {
                var apiClient = App.Current.Services.GetService<JellyfinApiClient>();
                var authService = App.Current.Services.GetService<IAuthenticationService>();

                if (apiClient == null || authService == null)
                {
                    return null;
                }

                // Use SDK to build the URL properly
                var requestInfo = apiClient.Items[itemId]
                    .Images[imageType]
                    .ToGetRequestInformation(config =>
                    {
                        config.QueryParameters.Quality = 90;

                        if (width.HasValue)
                            config.QueryParameters.MaxWidth = width.Value;
                        if (height.HasValue)
                            config.QueryParameters.MaxHeight = height.Value;
                        if (!string.IsNullOrEmpty(tag))
                            config.QueryParameters.Tag = tag;
                    });

                var url = requestInfo.URI.ToString();

                // Add API key for authentication if available since BitmapImage doesn't use SDK headers
                var accessToken = authService.AccessToken;
                if (!string.IsNullOrEmpty(accessToken))
                {
                    var separator = url.Contains("?") ? "&" : "?";
                    url = $"{url}{separator}api_key={Uri.EscapeDataString(accessToken)}";
                }

                return url;
            }
            catch (Exception)
            {
                return null; // Gracefully return null on error
            }
        }


        /// <summary>
        ///     Simple helper to get image URL by string ID (for MusicPlayer)
        /// </summary>
        public static string GetImageUrl(string itemId, string imageType, int? width = null, int? height = null)
        {
            if (Guid.TryParse(itemId, out var guid))
            {
                return BuildImageUrl(guid, imageType, width, height);
            }

            return null;
        }

        #endregion

        #region Image Metadata Methods

        /// <summary>
        ///     Check if item has specific image type
        /// </summary>
        public static bool HasImageType(BaseItemDto item, string imageType)
        {
            if (item?.ImageTags?.AdditionalData != null &&
                item.ImageTags.AdditionalData.ContainsKey(imageType))
            {
                return true;
            }

            if (imageType == "Backdrop" && item?.BackdropImageTags?.Any() == true)
            {
                return true;
            }

            if (imageType == "Primary" && item?.Type == BaseItemDto_Type.Series)
            {
                // Series items always have primary images
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Get image tag for cache busting
        /// </summary>
        public static string GetImageTag(BaseItemDto item, string imageType)
        {
            if (imageType == "Backdrop" && item?.BackdropImageTags?.Any() == true)
            {
                return item.BackdropImageTags.FirstOrDefault();
            }

            if (item?.ImageTags?.AdditionalData?.ContainsKey(imageType) == true)
            {
                return item.ImageTags.AdditionalData[imageType]?.ToString();
            }

            return null;
        }

        #endregion

        #region Private Cache Methods

        private static string GenerateCacheKey(string itemId, string imageType, int? width, int? height, int? quality,
            string tag = null)
        {
            var key = $"{itemId}_{imageType}_{width ?? 0}_{height ?? 0}_{quality ?? 90}";
            if (!string.IsNullOrEmpty(tag))
            {
                key += $"_{tag}";
            }

            return key;
        }

        private static string GenerateCacheKeyFromUrl(string url)
        {
            // Simple hash of URL for cache key
            return url.GetHashCode().ToString();
        }

        private static async Task<BitmapImage> GetCachedImageAsync(string cacheKey)
        {
            if (_imageCacheProvider == null)
            {
                return null;
            }

            try
            {
                var imageData = await _imageCacheProvider.GetAsync(cacheKey);
                if (imageData != null)
                {
                    using (var stream = new MemoryStream(imageData))
                    {
                        var bitmap = new BitmapImage();
                        await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                        return bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, $"Failed to load cached image: {cacheKey}");
            }

            return null;
        }

        private static async Task<BitmapImage> DownloadAndCacheImageAsync(string imageUrl, string cacheKey)
        {
            try
            {
                // Get HttpClient from factory to avoid socket exhaustion
                var httpClientFactory = App.Current.Services?.GetService<IHttpClientFactory>();
                if (httpClientFactory == null)
                {
                    _logger?.LogError("HttpClientFactory not available");
                    return null;
                }

                var httpClient = httpClientFactory.CreateClient("JellyfinClient");
                var response = await httpClient.GetAsync(imageUrl);
                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning($"Failed to download image. Status: {response.StatusCode}");
                    return null;
                }

                var imageData = await response.Content.ReadAsByteArrayAsync();

                // Save to cache using the cache provider
                if (_imageCacheProvider != null)
                {
                    try
                    {
                        // Cache for 30 days by default for images
                        await _imageCacheProvider.SetAsync(cacheKey, imageData, TimeSpan.FromDays(30));
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Failed to save image to cache");
                    }
                }

                // Create bitmap from downloaded data
                using (var stream = new MemoryStream(imageData))
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                    return bitmap;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, $"Failed to download image from: {imageUrl}");
                return null;
            }
        }

        #endregion
    }
}
