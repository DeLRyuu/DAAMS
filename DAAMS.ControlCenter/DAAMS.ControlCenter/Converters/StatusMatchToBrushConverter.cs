using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ControlCenter.Converters;

/// <summary>
/// Used by the status option buttons in the Security Alerts and Incident
/// Reports investigation panels. Compares the entity's current status
/// (values[0]) against a single button's own CommandParameter (values[1],
/// bound via RelativeSource Self) and returns the accent brush when they
/// match, or a neutral brush otherwise — so exactly one status button
/// reads as "this is the current state" instead of all four looking
/// identical (reference doc §15: an alert/incident always has one real
/// current status; the UI should say so at a glance).
///
/// ConverterParameter selects which visual aspect to return:
///   "Bg"     -> background (Brush.Accent when active, Transparent otherwise) — not currently used;
///               see the Foreground-only note below before wiring this up anywhere
///   "Border" -> border brush (Brush.Accent when active, Brush.Border otherwise) — not currently
///               used; Button.Secondary's BorderBrush is hardcoded in its template, not bindable
///   anything else / omitted -> foreground (Brush.Accent when active, Brush.TextPrimary otherwise)
///
/// Bound directly to a Button's Foreground via a MultiBinding local value — this intentionally
/// does NOT go through a shared Style, because the "current status" binding path differs between
/// SelectedAlert.Status and SelectedIncident.InvestigationStatus, and a Style's DataTrigger binding
/// is fixed at definition time. It also intentionally only ever binds Foreground, not Background:
/// Button.Secondary's only hover trigger sets Background, and a local value always outranks a style
/// trigger in WPF's property-value precedence, so binding Background here would silently disable
/// hover feedback on every status button, not just the active one. Foreground has no such trigger,
/// so overriding it is safe. Because Background is never touched, the active state must read as
/// bright accent-colored TEXT on the button's normal (transparent/dark) background — that's why the
/// active foreground here is Brush.Accent, not Brush.TextOnAccent (which assumes a filled accent
/// background that this converter's callers never actually set).
/// </summary>
public class StatusMatchToBrushConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isActive = values.Length == 2
                        && values[0] is not null
                        && values[1] is not null
                        && values[0].ToString() == values[1].ToString();

        string aspect = parameter as string ?? "Fg";

        if (isActive)
        {
            return aspect switch
            {
                "Bg" => Application.Current.Resources["Brush.Accent"],
                "Border" => Application.Current.Resources["Brush.Accent"],
                _ => Application.Current.Resources["Brush.Accent"]
            };
        }

        return aspect switch
        {
            "Bg" => Brushes.Transparent,
            "Border" => Application.Current.Resources["Brush.Border"],
            _ => Application.Current.Resources["Brush.TextPrimary"]
        };
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
