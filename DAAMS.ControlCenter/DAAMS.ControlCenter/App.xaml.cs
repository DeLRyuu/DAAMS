using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ControlCenter;

public partial class App : Application
{
    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            File.WriteAllText("C:\\temp\\crash.txt", e.ExceptionObject?.ToString() ?? "null");
        DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText("C:\\temp\\crash.txt", e.Exception.ToString());
            e.Handled = true;
        };
    }
}
