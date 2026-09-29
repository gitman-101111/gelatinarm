using System;
using Gelatinarm.Shared.Images;
using Gelatinarm.Shared.Ui;
using Jellyfin.Sdk.Generated.Models;

namespace Gelatinarm.Details
{
    public class PersonImageConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is BaseItemPerson person && person.Id != null
                ? ImageHelper.BuildImageUrl(person.Id.Value, ImageConstants.ImageTypePrimary, 200, tag: person.PrimaryImageTag, height: 200)
                : null;
        }
    }
}
