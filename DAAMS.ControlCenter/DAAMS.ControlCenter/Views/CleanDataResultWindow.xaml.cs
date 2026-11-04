using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ControlCenter.Views;

public partial class CleanDataResultWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public CleanDataResultWindow(string title, string statusText, string output, bool isSuccess)
    {
        InitializeComponent();
        Title = title;
        HeadingTextBlock.Text = title;
        StatusTextBlock.Text = statusText;
        OutputTextBlock.Text = output;

        var brush = isSuccess
            ? TryFindResource("Brush.StatusPositive") as Brush ?? Brushes.LightGreen
            : TryFindResource("Brush.RiskCritical") as Brush ?? Brushes.Salmon;
        StatusTextBlock.Foreground = brush;
    }

    protected override void OnSourceInitialized(System.EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
