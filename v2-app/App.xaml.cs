using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace BrowserExtensionLookup;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless verification mode: run store lookups against known-good data and exit.
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(-1);
            int exitCode;
            try
            {
                // Task.Run avoids deadlocking the STA thread while we block on async work.
                exitCode = Task.Run(SelfTest.RunAsync).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Self-test crashed: " + ex);
                exitCode = 2;
            }
            Shutdown(exitCode);
            return;
        }

        // Nothing should take the app down silently: show what went wrong and keep running.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            MessageBox.Show("Browser Extension Lookup hit an unexpected error and has to close:\n\n" + args.ExceptionObject,
                "Browser Extension Lookup", MessageBoxButton.OK, MessageBoxImage.Error);

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        MessageBox.Show(MainWindow, "Something went wrong, but the app is still running:\n\n" + e.Exception.Message,
            "Browser Extension Lookup", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
