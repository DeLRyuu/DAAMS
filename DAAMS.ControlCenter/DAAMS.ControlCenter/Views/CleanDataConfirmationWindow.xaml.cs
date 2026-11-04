using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ControlCenter.Views;

public partial class CleanDataConfirmationWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private readonly bool _isCloud;
    private readonly bool _isAll;
    private readonly string _expectedPhrase;

    public CleanDataConfirmationWindow(bool isCloud, bool isAll, string? dryRunOutput = null)
    {
        InitializeComponent();
        _isCloud = isCloud;
        _isAll = isAll;

        if (_isAll)
        {
            DescriptionTextBlock.Text = "Activity logs, security alerts and incident reports will be deleted.";
            AllWarningTextBlock.Visibility = Visibility.Visible;
            AllWarningTextBlock.Text = "Protected assets and PIN settings will ALSO be deleted and reset to default.";
        }
        else
        {
            DescriptionTextBlock.Text = "Activity logs, security alerts and incident reports will be deleted; protected assets and PIN are kept.";
            AllWarningTextBlock.Visibility = Visibility.Collapsed;
        }

        if (_isCloud)
        {
            Title = _isAll ? "Confirm Clean Cloud Data (--all)" : "Confirm Clean Cloud Data";
            HeadingTextBlock.Text = _isAll ? "Clean Cloud Data (--all)" : "Clean Cloud Data";
            NoteTextBlock.Text = "Note: Permanent, no backup.";
            DryRunPanel.Visibility = Visibility.Visible;
            DryRunOutputTextBlock.Text = dryRunOutput ?? "No dry run output available.";

            _expectedPhrase = "DELETE CLOUD DATA";
            PhrasePromptTextBlock.Text = $"To confirm, type {_expectedPhrase} below:";
            PhraseConfirmationPanel.Visibility = Visibility.Visible;
            ConfirmButton.Content = _isAll ? "Delete Cloud Data (--all)" : "Delete Cloud Data";
            ConfirmButton.IsEnabled = false;

            Loaded += (_, _) => ConfirmationTextBox.Focus();
        }
        else
        {
            Title = _isAll ? "Confirm Clean Local Data (--all)" : "Confirm Clean Local Data";
            HeadingTextBlock.Text = _isAll ? "Clean Local Data (--all)" : "Clean Local Data";
            NoteTextBlock.Text = "Note: This computer only, backup is made.";
            DryRunPanel.Visibility = Visibility.Collapsed;

            if (_isAll)
            {
                _expectedPhrase = "RESET ALL";
                PhrasePromptTextBlock.Text = $"To confirm, type {_expectedPhrase} below:";
                PhraseConfirmationPanel.Visibility = Visibility.Visible;
                ConfirmButton.Content = "Clean Local Data (--all)";
                ConfirmButton.IsEnabled = false;

                Loaded += (_, _) => ConfirmationTextBox.Focus();
            }
            else
            {
                _expectedPhrase = string.Empty;
                PhraseConfirmationPanel.Visibility = Visibility.Collapsed;
                ConfirmButton.Content = "Clean Local Data";
                ConfirmButton.IsEnabled = true;

                Loaded += (_, _) => ConfirmButton.Focus();
            }
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    private void ConfirmationTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_expectedPhrase))
        {
            ConfirmButton.IsEnabled = string.Equals(ConfirmationTextBox.Text?.Trim(), _expectedPhrase, StringComparison.Ordinal);
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
