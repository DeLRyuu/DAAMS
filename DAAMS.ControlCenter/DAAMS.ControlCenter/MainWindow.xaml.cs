using System.Windows;

namespace ControlCenter;

/// <summary>
/// Application shell. Deliberately thin — all state and navigation logic
/// live in MainViewModel, not here.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
