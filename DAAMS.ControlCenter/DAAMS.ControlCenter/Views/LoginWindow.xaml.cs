using System.Windows;
using ControlCenter;

namespace ControlCenter.Views;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UsernameTextBox.Focus();
    }

    private void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameTextBox.Text?.Trim() ?? string.Empty;
        var password = PasswordBox.Password;

        if (username == "admin" && password == "admin123")
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
            var mainWindow = new MainWindow();
            Application.Current.MainWindow = mainWindow;
            mainWindow.Show();
            Close();
        }
        else
        {
            ErrorTextBlock.Text = "Incorrect username or password";
            ErrorTextBlock.Visibility = Visibility.Visible;
            PasswordBox.Password = string.Empty;
            PasswordBox.Focus();
        }
    }
}
