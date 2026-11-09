using System;
using System.Globalization;
using System.Windows.Data;

namespace CoreShift.Converters
{
    /// <summary>
    /// Converts a boolean to a tooltip string for the volume lock button.
    /// </summary>
    public sealed class BoolToTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool enabled)
            {
                return enabled ? "Unlock volume limit (allow full volume)" : "Lock volume limit (enforce maximum)";
            }
            return "Volume limit";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}