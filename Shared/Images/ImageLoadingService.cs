using System;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Gelatinarm.Shared.Base;
using Gelatinarm.Shared.Errors;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Images
{
    /// <summary>
    ///     Images set from code, loaded as the XAML tiles load theirs
    /// </summary>
    public interface IImageLoadingService
    {
        /// <summary>
        ///     The item's image of that type; null when no URL can be built for it
        /// </summary>
        Task<ImageSource> LoadImageAsync(BaseItemDto item, string imageType, int? width, int? height);

        /// <summary>
        ///     Loads the item's image and hands it to <paramref name="setImageAction" /> on the UI thread
        /// </summary>
        Task LoadImageIntoTargetAsync(BaseItemDto item, string imageType, Action<ImageSource> setImageAction,
            int? width, int? height = null);
    }

    /// <summary>
    ///     Images set from code (details pages, the season page, the mini player) load the way the
    ///     XAML tiles do: a BitmapImage on the item's image URL, fetched and cached by the platform
    ///     (the URL carries the image tag, which the server caches against).
    /// </summary>
    public class ImageLoadingService : BaseService, IImageLoadingService
    {
        public ImageLoadingService(ILogger<ImageLoadingService> logger) : base(logger)
        {
        }

        public async Task<ImageSource> LoadImageAsync(BaseItemDto item, string imageType, int? width, int? height)
        {
            if (item?.Id == null)
            {
                return null;
            }

            var url = ImageHelper.BuildImageUrl(item.Id.Value, imageType, width,
                ImageHelper.GetImageTag(item, imageType), height);
            if (string.IsNullOrEmpty(url))
            {
                return null;
            }

            // BitmapImage is a UI object: it has to be created on the UI thread
            ImageSource image = null;
            await UiHelper.RunOnUIThreadAsync(() => image = new BitmapImage(new Uri(url)), Logger).ConfigureAwait(false);
            return image;
        }

        public async Task LoadImageIntoTargetAsync(
            BaseItemDto item,
            string imageType,
            Action<ImageSource> setImageAction,
            int? width,
            int? height = null)
        {
            try
            {
                var imageSource = await LoadImageAsync(item, imageType, width, height).ConfigureAwait(false);

                if (imageSource != null)
                {
                    await UiHelper.RunOnUIThreadAsync(() => setImageAction(imageSource), Logger);
                }
            }
            catch (Exception ex)
            {
                await ErrorHandler.HandleErrorAsync(ex, CreateErrorContext("LoadImageIntoTarget", ErrorCategory.Media), false);
            }
        }
    }
}
