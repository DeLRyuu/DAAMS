using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ControlCenter.Models;

namespace ControlCenter.Converters;

/// <summary>
/// Maps a Classification to a neutral grey/slate ramp:
/// Public (light grey) -> Sensitive -> Private -> Confidential (strong tone).
/// ConverterParameter "Bg" returns the matching background tint.
/// </summary>
public class ClassificationToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool background = string.Equals(parameter as string, "Bg", StringComparison.OrdinalIgnoreCase);

        Classification classification = Classification.Public;
        if (value is Classification c)
        {
            classification = c;
        }
        else if (value is string s && Enum.TryParse<Classification>(s, true, out var parsed))
        {
            classification = parsed;
        }

        string key = classification switch
        {
            Classification.Sensitive => background ? "Brush.ClassSensitiveBg" : "Brush.ClassSensitive",
            Classification.Private => background ? "Brush.ClassPrivateBg" : "Brush.ClassPrivate",
            Classification.Confidential => background ? "Brush.ClassConfidentialBg" : "Brush.ClassConfidential",
            _ => background ? "Brush.ClassPublicBg" : "Brush.ClassPublic"
        };

        return Application.Current.Resources[key] ?? Application.Current.Resources["Brush.TextSecondary"];
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
