using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ControlCenter.Converters;

/// <summary>
/// Converts an integer count into a Star-sized GridLength, so a Grid's column
/// widths become proportional to the values bound to them (used by the
/// Dashboard risk-distribution bar). A minimum of 0.02 star keeps a zero-count
/// segment from disappearing into an invalid/negative width.
/// </summary>
public class IntToStarWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double count = value is int i ? i : 0;
        double weight = Math.Max(count, 0.02);
        return new GridLength(weight, GridUnitType.Star);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
