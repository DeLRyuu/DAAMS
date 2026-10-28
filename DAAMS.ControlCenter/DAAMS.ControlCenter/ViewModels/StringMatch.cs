using System;

namespace ControlCenter.ViewModels;

/// <summary>
/// Null-safe string matching helpers used by ViewModel filter predicates.
/// All comparisons use OrdinalIgnoreCase so filtering is culture-insensitive
/// and never throws on null values.
/// </summary>
public static class StringMatch
{
    /// <summary>Returns true when <paramref name="value"/> contains <paramref name="term"/>,
    /// treating null/empty value as non-matching and null/empty term as matching everything.</summary>
    public static bool Has(string? value, string term)
    {
        if (string.IsNullOrEmpty(term))
        {
            return true;
        }
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        return value.Contains(term, StringComparison.OrdinalIgnoreCase);
    }
}