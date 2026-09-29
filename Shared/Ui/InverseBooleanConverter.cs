using System;

namespace Gelatinarm.Shared.Ui
{
    public class InverseBooleanConverter : OneWayConverter
    {
        public override object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is false;
        }
    }
}
