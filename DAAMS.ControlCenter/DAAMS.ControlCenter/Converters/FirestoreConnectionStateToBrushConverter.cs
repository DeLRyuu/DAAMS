using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ControlCenter.Services;

namespace ControlCenter.Converters;

/// <summary>
/// Maps FirestoreConnectionState to a status-dot brush for the shell footer.
/// </summary>
public class FirestoreConnectionStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value switch
        {
            FirestoreConnectionState.Connected => "Brush.StatusPositive",
            FirestoreConnectionState.Connecting => "Brush.StatusWarning",
            FirestoreConnectionState.Unavailable => "Brush.RiskCritical",
            _ => "Brush.StatusInactive"
        };

        return Application.Current.Resources[key];
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
