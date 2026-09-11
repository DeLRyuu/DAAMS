using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ControlCenter.Models;

namespace ControlCenter.Converters;

/// <summary>
/// Maps a Classification to a brush, reusing the same four-step color scale as
/// RiskLevelToBrushConverter (Public≈Low ... Confidential≈Critical). This is a
/// deliberate design decision: classification weights already sit on the same
/// ordinal scale the risk engine uses (5/15/25/35 — see reference doc §13), so
/// reusing the palette teaches the manager one color language instead of two.
/// ConverterParameter "Bg" returns the muted background tint.
/// </summary>
public class ClassificationToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool background = string.Equals(parameter as string, "Bg", StringComparison.OrdinalIgnoreCase);

        string key = value switch
        {
            Classification.Public => background ? "Brush.RiskLowBg" : "Brush.RiskLow",
            Classification.Sensitive => background ? "Brush.RiskMediumBg" : "Brush.RiskMedium",
            Classification.Private => background ? "Brush.RiskHighBg" : "Brush.RiskHigh",
            Classification.Confidential => background ? "Brush.RiskCriticalBg" : "Brush.RiskCritical",
            _ => background ? "Brush.SurfaceElevated" : "Brush.TextSecondary"
        };

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
