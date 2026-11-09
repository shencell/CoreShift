using System;
using System.Globalization;
using System.Windows.Data;

namespace CoreShift.Converters
{
    /// <summary>
    /// Converts slider value, min, max, and container width to a fill width for the track.
    /// </summary>
    public sealed class TrackWidthConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length != 4 ||
                values[0] is not double value ||
                values[1] is not double minimum ||
                values[2] is not double maximum ||
                values[3] is not double containerWidth)
            {
                return 0.0;
            }

            // Calculate the proportion of the value within the range
            double range = maximum - minimum;
            if (range <= 0)
            {
                return 0.0;
            }

            double proportion = (value - minimum) / range;
            proportion = Math.Max(0, Math.Min(1, proportion)); // Clamp to [0, 1]

            // Calculate available width for the fill (container width minus margins for thumb center)
            // The track has margin="9,0,9,0" so available width = containerWidth - 18
            double availableWidth = Math.Max(0, containerWidth - 18);

            return availableWidth * proportion;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}