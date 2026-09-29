using System;
using Windows.UI.Xaml.Media.Imaging;
using Gelatinarm.Shared.Server;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;
using Microsoft.Extensions.Logging;

namespace Gelatinarm.Shared.Images
{
    /// <summary>
    ///     An item's image for XAML tiles; the parameter names the kind (Primary by default)
    /// </summary>
    public class ImageConverter : OneWayConverter
    {
        private const int PosterWidth = 400;
        private const int BackdropWidth = 600;

        public enum ImageType
        {
            Primary,
            Backdrop
        }

        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is not BaseItemDto item)
                {
                    return null;
                }

                var imageType = ImageType.Primary;
                if (parameter is string paramStr && Enum.TryParse<ImageType>(paramStr, true, out var parsedType))
                {
                    imageType = parsedType;
                }

                var url = GetImageUrl(item, imageType);
                if (!string.IsNullOrEmpty(url))
                {
                    return new BitmapImage { UriSource = new Uri(url) };
                }
            }
            catch (Exception ex)
            {
                ServiceLocator.GetService<ILogger<ImageConverter>>()?.LogDebug(ex, "ImageConverter error");
            }

            return null;
        }

        private static string GetImageUrl(BaseItemDto item, ImageType imageType)
        {
            return imageType == ImageType.Backdrop ? GetBackdropImageUrl(item) : GetPrimaryImageUrl(item);
        }

        private static string GetPrimaryImageUrl(BaseItemDto item)
        {
            // An episode's poster is its series' poster
            if (item.Type == BaseItemDto_Type.Episode && !string.IsNullOrEmpty(item.SeriesPrimaryImageTag) &&
                item.SeriesId.HasValue)
            {
                return ImageHelper.BuildImageUrl(item.SeriesId.Value, ImageConstants.ImageTypePrimary, PosterWidth,
                    tag: item.SeriesPrimaryImageTag);
            }

            return GetOwnPrimaryImageUrl(item, PosterWidth);
        }

        private static string GetBackdropImageUrl(BaseItemDto item)
        {
            if (item.Type == BaseItemDto_Type.Movie && item.Id.HasValue)
            {
                return ImageHelper.BuildImageUrl(item.Id.Value, ImageConstants.ImageTypeBackdrop, BackdropWidth,
                    tag: ImageHelper.GetImageTag(item, ImageConstants.ImageTypeBackdrop),
                    quality: ImageConstants.BackdropQuality);
            }

            // Everything else uses its own primary image at backdrop width (an episode's
            // still frame, not its series' poster)
            return GetOwnPrimaryImageUrl(item, BackdropWidth);
        }

        private static string GetOwnPrimaryImageUrl(BaseItemDto item, int width)
        {
            return item.Id.HasValue
                ? ImageHelper.BuildImageUrl(item.Id.Value, ImageConstants.ImageTypePrimary, width,
                    tag: ImageHelper.GetImageTag(item, ImageConstants.ImageTypePrimary))
                : null;
        }
    }
}
