using System.Runtime.InteropServices;
using System.Windows;

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

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
