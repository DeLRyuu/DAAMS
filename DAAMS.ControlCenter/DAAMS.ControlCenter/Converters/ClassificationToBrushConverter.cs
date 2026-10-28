using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ControlCenter.Models;

namespace ControlCenter.Converters;

/// <summary>
/// Maps any Classification to the violet classification brush so classification
/// badges are visually distinct from risk-level badges (green/amber/orange/red).
/// ConverterParameter "Bg" returns the muted background tint.
/// </summary>
public class ClassificationToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool background = string.Equals(parameter as string, "Bg", StringComparison.OrdinalIgnoreCase);

        // Classification uses a single violet family so it is never confused
        // with the risk-level palette (green/amber/orange/red). The level itself
        // is still shown as text; color no longer doubles as a risk signal.
        string key = background ? "Brush.ClassificationBg" : "Brush.Classification";

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
