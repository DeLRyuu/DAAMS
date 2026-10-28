using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ControlCenter.Services;

namespace ControlCenter.Views;

public partial class PinPromptWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private readonly LocalDatabaseService _db;

    public PinPromptWindow(LocalDatabaseService db, string? description = null)
    {
        InitializeComponent();
        _db = db;

        if (!string.IsNullOrWhiteSpace(description))
        {
            DescriptionTextBlock.Text = description;
        }

        SourceInitialized += (_, _) =>
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                int useDarkMode = 1;
                DwmSetWindowAttribute(handle, 20, ref useDarkMode, sizeof(int));
            }
            catch
            {
                // Non-fatal if OS version does not support dark title bar attribute
            }
        };

        Loaded += (_, _) => PinBox.Focus();
    }

    private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        await ProcessPinAsync();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void PinBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ErrorTextBlock.Visibility == Visibility.Visible)
        {
            ErrorTextBlock.Visibility = Visibility.Collapsed;
        }
    }

    private async void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await ProcessPinAsync();
        }
    }

    private async Task ProcessPinAsync()
    {
        string pin = PinBox.Password;

        if (string.IsNullOrEmpty(pin) || pin.Length < 4 || pin.Length > 8 || !pin.All(char.IsDigit))
        {
            ShowError();
            return;
        }

        ConfirmButton.IsEnabled = false;
        try
        {
            bool isValid = await _db.VerifyPinAsync(pin);
            if (isValid)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                ShowError();
            }
        }
        catch
        {
            ShowError();
        }
        finally
        {
            ConfirmButton.IsEnabled = true;
        }
    }

    private void ShowError()
    {
        ErrorTextBlock.Visibility = Visibility.Visible;
        PinBox.SelectAll();
        PinBox.Focus();
    }
}
