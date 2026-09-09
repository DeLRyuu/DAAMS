using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ControlCenter.Models;

namespace ControlCenter.Converters;

/// <summary>
/// Maps a RiskLevel to its semantic brush. ConverterParameter "Bg" returns the
/// muted background tint; any other/no parameter returns the foreground/accent tone.
/// This is the ONLY place risk color is decided, so risk color stays consistent
/// across Dashboard, Activity Logs, Security Alerts, and Incident Reports.
/// </summary>
public class RiskLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool background = string.Equals(parameter as string, "Bg", StringComparison.OrdinalIgnoreCase);

        string key = value switch
        {
            RiskLevel.Low => background ? "Brush.RiskLowBg" : "Brush.RiskLow",
            RiskLevel.Medium => background ? "Brush.RiskMediumBg" : "Brush.RiskMedium",
            RiskLevel.High => background ? "Brush.RiskHighBg" : "Brush.RiskHigh",
            RiskLevel.Critical => background ? "Brush.RiskCriticalBg" : "Brush.RiskCritical",
            _ => background ? "Brush.SurfaceElevated" : "Brush.TextSecondary"
        };

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
