using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ControlCenter.Models;

namespace ControlCenter.Converters;

/// <summary>
/// Maps a status enum (ProtectionStatus, AlertStatus, or InvestigationStatus) to a brush.
/// Kept as one converter rather than three near-identical ones so the status color
/// vocabulary — "needs attention" = accent, "in progress" = warning amber,
/// "resolved/positive" = green, "inactive/dismissed" = muted gray — stays consistent
/// everywhere a status badge appears.
/// ConverterParameter "Bg" returns the muted background tint.
/// </summary>
public class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool background = string.Equals(parameter as string, "Bg", StringComparison.OrdinalIgnoreCase);

        string key = value switch
        {
            ProtectionStatus.Protected => background ? "Brush.RiskLowBg" : "Brush.StatusPositive",
            ProtectionStatus.Unprotected => background ? "Brush.SurfaceElevated" : "Brush.StatusInactive",

            AlertStatus.New => background ? "Brush.AccentSubtleBg" : "Brush.Accent",
            AlertStatus.Investigating => background ? "Brush.RiskMediumBg" : "Brush.StatusWarning",
            AlertStatus.Reviewed => background ? "Brush.SurfaceElevated" : "Brush.TextSecondary",
            AlertStatus.Resolved => background ? "Brush.RiskLowBg" : "Brush.StatusPositive",

            InvestigationStatus.New => background ? "Brush.AccentSubtleBg" : "Brush.Accent",
            InvestigationStatus.UnderInvestigation => background ? "Brush.RiskMediumBg" : "Brush.StatusWarning",
            InvestigationStatus.Resolved => background ? "Brush.RiskLowBg" : "Brush.StatusPositive",
            InvestigationStatus.Dismissed => background ? "Brush.SurfaceElevated" : "Brush.StatusInactive",

            _ => background ? "Brush.SurfaceElevated" : "Brush.TextSecondary"
        };

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
