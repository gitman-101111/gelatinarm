using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Gelatinarm.Helpers;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;
using Windows.UI.Core;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Gelatinarm.Services
{
    public class ImageLoadingService : BaseService, IImageLoadingService
    {
        private readonly ILogger<ImageLoadingService> _logger;

        public ImageLoadingService(ILogger<ImageLoadingService> logger) : base(logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        ///     Load an image with retry logic
        /// </summary>
        public async Task<ImageSource> LoadImageAsync(BaseItemDto item, string imageType, int? width = null,
            int? height = null, CancellationToken cancellationToken = default)
        {
            if (item?.Id == null)
            {
                _logger.LogDebug("LoadImageAsync called with null item or ID");
                return null;
            }

            try
            {
                // Use RetryHelper for consistent retry logic
                return await RetryHelper.ExecuteWithRetryAsync(
                    async () => await ImageHelper.GetImageSourceAsync(item, imageType, width, height)
                        .ConfigureAwait(false),
                    _logger,
                    3,
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken,
                    shouldRetry: ex => IsImageLoadingError(ex)
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug($"Image loading cancelled for {item.Name} ({imageType})");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to load {imageType} image for {item.Name} after retries");
                return null;
            }
        }

        /// <summary>
        ///     Load an image by URL with retry logic
        /// </summary>
        public async Task<BitmapImage> LoadImageByUrlAsync(string imageUrl,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(imageUrl))
            {
                _logger.LogDebug("LoadImageByUrlAsync called with null or empty URL");
                return null;
            }

            try
            {
                // Use RetryHelper for consistent retry logic
                return await RetryHelper.ExecuteWithRetryAsync(
                    async () => await ImageHelper.GetImageByUrlAsync(imageUrl).ConfigureAwait(false),
                    _logger,
                    3,
                    TimeSpan.FromMilliseconds(500),
                    cancellationToken,
                    shouldRetry: ex => IsImageLoadingError(ex)
                ).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug($"Image loading cancelled for URL: {imageUrl}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to load image from URL after retries: {imageUrl}");
                return null;
            }
        }

        /// <summary>
        ///     Load an image and set it on the UI thread
        /// </summary>
        public async Task LoadImageIntoTargetAsync(
            BaseItemDto item,
            string imageType,
            Action<ImageSource> setImageAction,
            CoreDispatcher dispatcher,
            int? width = null,
            int? height = null,
            CancellationToken cancellationToken = default)
        {
            if (item?.Id == null || setImageAction == null || dispatcher == null)
            {
                _logger.LogDebug("LoadImageIntoTargetAsync called with invalid parameters");
                return;
            }

            try
            {
                var imageSource = await LoadImageAsync(item, imageType, width, height, cancellationToken)
                    .ConfigureAwait(false);

                if (imageSource != null && !cancellationToken.IsCancellationRequested)
                {
                    await UIHelper.RunOnUIThreadAsync(() =>
                    {
                        try
                        {
                            setImageAction(imageSource);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error setting image on UI thread");
                        }
                    }, dispatcher, _logger);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug($"Image loading cancelled for {item.Name} ({imageType})");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading {imageType} image for {item.Name}");
            }
        }

        /// <summary>
        ///     Load an image by URL and set it on the UI thread
        /// </summary>
        public async Task LoadImageByUrlIntoTargetAsync(
            string imageUrl,
            Action<BitmapImage> setImageAction,
            CoreDispatcher dispatcher,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(imageUrl) || setImageAction == null || dispatcher == null)
            {
                _logger.LogDebug("LoadImageByUrlIntoTargetAsync called with invalid parameters");
                return;
            }

            try
            {
                var bitmapImage = await LoadImageByUrlAsync(imageUrl, cancellationToken).ConfigureAwait(false);

                if (bitmapImage != null && !cancellationToken.IsCancellationRequested)
                {
                    await UIHelper.RunOnUIThreadAsync(() =>
                    {
                        try
                        {
                            setImageAction(bitmapImage);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error setting image on UI thread");
                        }
                    }, dispatcher, _logger);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug($"Image loading cancelled for URL: {imageUrl}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading image from URL: {imageUrl}");
            }
        }

        public async Task<ImageSource> GetImageAsync(Guid itemId, ImageType imageType, string imageTag,
            int? width = null, int? height = null, CancellationToken cancellationToken = default)
        {
            try
            {
                var imageUrl = ImageHelper.GetImageUrl(
                    itemId.ToString(),
                    imageType.ToString(),
                    width,
                    height);

                return await LoadImageByUrlAsync(imageUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, $"Error loading image for item {itemId}, type {imageType}");
                return null;
            }
        }

        /// <summary>
        ///     Determine if an exception is a transient image loading error that should be retried
        /// </summary>
        private bool IsImageLoadingError(Exception ex)
        {
            return ex switch
            {
                TaskCanceledException => false, // Don't retry cancellations
                OperationCanceledException => false, // Don't retry cancellations
                HttpRequestException => true, // Network errors
                IOException => true, // File system errors
                UnauthorizedAccessException => false, // Don't retry auth errors
                _ => false // Don't retry unknown errors
            };
        }
    }
}
