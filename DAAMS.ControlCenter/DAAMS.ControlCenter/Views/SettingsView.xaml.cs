using System.Windows.Controls;

namespace ControlCenter.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += SettingsView_Loaded;
    }

    private void SettingsView_Loaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm)
        {
            vm.SyncSmtpPasswordBox(SmtpPasswordBox);
        }
    }

    private async void SavePinButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is ViewModels.SettingsViewModel vm)
        {
            bool success = await vm.ChangePinAsync(CurrentPinBox.Password, NewPinBox.Password, ConfirmPinBox.Password);
            if (success)
            {
                CurrentPinBox.Password = string.Empty;
                NewPinBox.Password = string.Empty;
                ConfirmPinBox.Password = string.Empty;
            }
        }
    }
}
