using System;
using System.Globalization;
using System.Windows.Data;

namespace ControlCenter.Converters;

public class GroupExpandedConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2)
        {
            return true;
        }

        string? groupName = values[0]?.ToString();
        string? selectedGroup = values[1]?.ToString();

        // If this group contains the selected alert, always expand it
        if (!string.IsNullOrEmpty(selectedGroup) &&
            string.Equals(groupName, selectedGroup, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // By default, Closed group starts collapsed; others start expanded
        if (string.Equals(groupName, "Closed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
