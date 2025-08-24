using System;
using Gelatinarm.Helpers;
using Jellyfin.Sdk.Generated.Models;
using Windows.UI.Xaml.Data;

namespace Gelatinarm.Converters.Image
{
    /// <summary>
    ///     Converter to generate image URLs for BaseItemPerson objects
    /// </summary>
    public class PersonImageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                if (value is not BaseItemPerson person || person?.Id == null)
                {
                    return null;
                }

                // Use ImageHelper to build the URL
                var imageUrl = ImageHelper.BuildImageUrl(person.Id.Value, "Primary", 200, 200, person.PrimaryImageTag);

                return imageUrl;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
