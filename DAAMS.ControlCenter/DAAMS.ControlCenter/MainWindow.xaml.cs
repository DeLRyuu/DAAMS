using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ControlCenter.Services;
using ControlCenter.Views;

namespace ControlCenter;

/// <summary>
/// Application shell. Deliberately thin — all state and navigation logic
/// live in MainViewModel, not here.
/// </summary>
public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        ViewContext.Current.Reset();
        var loginWindow = new LoginWindow();
        Application.Current.MainWindow = loginWindow;
        loginWindow.Show();
        Close();
    }
}
