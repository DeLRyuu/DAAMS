using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace ControlCenter.Converters;

/// <summary>
/// Display text for the Asset column in Activities.
///   Move   -> file name only             (e.g. "Payroll.txt")
///   Rename -> "OldName → NewName"        (e.g. "Payroll.txt → Payrol.txt")
///   Others -> unchanged
/// The full path is still available in the row details and the tooltip.
/// values[0] = Asset, values[1] = Action
/// </summary>
public class ActivityAssetDisplayConverter : IMultiValueConverter
{
    private static readonly string[] Separators = { " -> ", " → " };

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        string asset = values.Length > 0 ? values[0]?.ToString() ?? string.Empty : string.Empty;
        string action = values.Length > 1 ? values[1]?.ToString() ?? string.Empty : string.Empty;

        string[] parts = asset.Split(Separators, 2, StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return asset;
        }

        string oldName = NameOf(parts[0]);
        string newName = NameOf(parts[1]);

        if (action.Equals("Rename", StringComparison.OrdinalIgnoreCase))
        {
            return $"{oldName} → {newName}";
        }

        if (action.Equals("Move", StringComparison.OrdinalIgnoreCase))
        {
            return newName;
        }

        return asset;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string NameOf(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}
