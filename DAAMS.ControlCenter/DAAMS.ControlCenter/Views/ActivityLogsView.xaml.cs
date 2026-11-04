using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ControlCenter.Views;

public partial class ActivityLogsView : UserControl
{
    public ActivityLogsView()
    {
        InitializeComponent();
    }

    private void OnRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow row)
            return;

        // If the click originated inside the row details template, do not toggle the row.
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source != null && source != row)
        {
            if (source is DataGridDetailsPresenter)
                return;

            if (source is Visual || source is System.Windows.Media.Media3D.Visual3D)
                source = VisualTreeHelper.GetParent(source);
            else if (source is FrameworkContentElement fce)
                source = fce.Parent;
            else
                break;
        }

        row.DetailsVisibility = row.DetailsVisibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
