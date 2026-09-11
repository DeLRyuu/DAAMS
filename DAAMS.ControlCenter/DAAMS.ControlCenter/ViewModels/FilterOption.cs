using System;
using System.Collections.Generic;
using System.Linq;

namespace ControlCenter.ViewModels;

/// <summary>
/// A selectable filter choice for a ComboBox. Value == null represents "All"
/// (no filtering on that field). Reused by Protected Assets, Activity Logs,
/// Security Alerts, and Incident Reports so filtering behaves identically everywhere.
/// </summary>
public class FilterOption<T> where T : struct, Enum
{
    public string Label { get; init; } = string.Empty;
    public T? Value { get; init; }

    public static List<FilterOption<T>> AllOptions(string allLabel = "All")
    {
        var options = new List<FilterOption<T>>
        {
            new() { Label = allLabel, Value = null }
        };
        options.AddRange(Enum.GetValues<T>().Select(v => new FilterOption<T> { Label = v.ToString(), Value = v }));
        return options;
    }
}
