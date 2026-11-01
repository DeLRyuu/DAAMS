using System;
using System.Globalization;

namespace ControlCenter.Converters;

public static class Fmt
{
    public static string Stamp(DateTime t) => t.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Full(DateTime t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Age(DateTime t)
    {
        var today = DateTime.Today;
        var daysAgo = (today - t.Date).Days;
        var time = t.ToString("HH:mm", CultureInfo.InvariantCulture);

        return daysAgo switch
        {
            0 => $"today at {time}",
            1 => $"yesterday at {time}",
            > 1 => $"{daysAgo} days ago",
            _ => Stamp(t)
        };
    }
}
