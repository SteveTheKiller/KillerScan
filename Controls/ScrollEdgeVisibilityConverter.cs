using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace KillerScan.Controls
{
    // Shows a scroll edge shadow only while more content lies past that edge. Values are the
    // scroll offset and the scrollable extent on one axis; the parameter picks the edge, "Start"
    // (top or left) or "End" (bottom or right).
    public sealed class ScrollEdgeVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] is not double offset || values[1] is not double extent)
                return Visibility.Collapsed;
            bool start = !string.Equals(parameter as string, "End", StringComparison.Ordinal);
            bool more = start ? offset > 0.5 : extent - offset > 0.5;
            return more ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
