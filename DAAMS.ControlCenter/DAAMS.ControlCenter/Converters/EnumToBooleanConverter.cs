using System;
using System.Globalization;
using System.Windows.Data;

namespace ControlCenter.Converters;

/// <summary>
/// Binds a RadioButton's IsChecked to an enum property, comparing against
/// the enum value passed as ConverterParameter. Used by the main navigation list.
/// </summary>
public class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null)
        {
            return false;
        }

        return value.ToString() == parameter.ToString();
    }

    public object? ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isChecked && isChecked && parameter is not null)
        {
            return Enum.Parse(targetType, parameter.ToString()!);
        }

        return Binding.DoNothing;
    }
}
