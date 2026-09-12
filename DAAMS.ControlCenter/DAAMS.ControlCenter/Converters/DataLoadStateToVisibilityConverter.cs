using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ControlCenter.ViewModels;

namespace ControlCenter.Converters;

/// <summary>
/// Visible when the bound DataLoadState equals the state named by
/// ConverterParameter ("Loading", "Loaded", "Empty", or "Error"). Used to
/// show exactly one of a loading/empty/error/content overlay per section.
/// </summary>
public class DataLoadStateToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DataLoadState state &&
            parameter is string expected &&
            Enum.TryParse(expected, ignoreCase: true, out DataLoadState target))
        {
            return state == target ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
