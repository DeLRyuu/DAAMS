using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ControlCenter;

public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog(e.ExceptionObject?.ToString() ?? "null");
        DispatcherUnhandledException += (_, e) =>
        {
            WriteCrashLog(e.Exception.ToString());
            e.Handled = true;
        };
    }

    private static void WriteCrashLog(string text)
    {
        string logDirectory = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "DAAMS", "logs");
        Directory.CreateDirectory(logDirectory);
        File.WriteAllText(Path.Combine(logDirectory, "crash.txt"), text);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var loginWindow = new Views.LoginWindow();
        loginWindow.Show();
    }
}
