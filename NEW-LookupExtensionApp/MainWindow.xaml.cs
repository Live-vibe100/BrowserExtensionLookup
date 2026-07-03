using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BrowserExtensionLookup;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        SearchTabView.StatusReported += SetStatus;
        LookupTabView.StatusReported += SetStatus;
        BulkTabView.StatusReported += SetStatus;

        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Loaded += (_, _) => SearchTabView.FocusQuery();
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Fires during XAML parse before the views exist; ignore until everything is built.
        if (SearchTabView is null || LookupTabView is null || BulkTabView is null) return;

        SearchTabView.Visibility = TabSearch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        LookupTabView.Visibility = TabLookup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BulkTabView.Visibility = TabBulk.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message, StatusLevel level)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(level switch
        {
            StatusLevel.Working => "YellowBrush",
            StatusLevel.Warn => "OrangeBrush",
            _ => "GreenBrush",
        });
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void EnableDarkTitleBar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Windows 10 2004+); harmless no-op if unsupported
        _ = DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
    }
}
