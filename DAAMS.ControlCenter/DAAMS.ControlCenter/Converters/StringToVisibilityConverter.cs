using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ControlCenter.Converters;

/// <summary>
/// Visible when the bound string is non-empty, Collapsed otherwise. Used for
/// optional fields like IncidentReport.Notes or SecurityAlert.RelatedAlertId
/// where BooleanToVisibilityConverter doesn't apply (the source isn't a bool).
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
