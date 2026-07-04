using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BrowserExtensionLookup.Views;

public partial class LookupView : UserControl
{
    public event Action<string, StatusLevel>? StatusReported;

    private readonly ObservableCollection<LookupRow> _rows = new();

    public LookupView()
    {
        InitializeComponent();
        ResultGrid.ItemsSource = _rows;
    }

    private void Report(string message, StatusLevel level = StatusLevel.Info) =>
        StatusReported?.Invoke(message, level);

    private void IdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = RunLookupAsync();
    }

    private void Lookup_Click(object sender, RoutedEventArgs e) => _ = RunLookupAsync();

    /// <summary>Fill in an ID and run the lookup (used when name search detects a pasted ID).</summary>
    public void LookupId(string id)
    {
        IdBox.Text = id;
        _ = RunLookupAsync();
    }

    private async Task RunLookupAsync()
    {
        var id = IdBox.Text.Trim().ToLowerInvariant();
        if (id.Length == 0)
        {
            Report("Please enter an extension ID to look up.", StatusLevel.Warn);
            return;
        }
        if (!StoreClient.IsValidId(id))
        {
            Report("Invalid extension ID format. Must be 32 characters using only letters a-p.", StatusLevel.Warn);
            return;
        }

        LookupButton.IsEnabled = false;
        _rows.Clear();
        Report($"Looking up {id} in both stores...", StatusLevel.Working);

        try
        {
            var chromeTask = StoreClient.Instance.LookupChromeAsync(id);
            var edgeTask = StoreClient.Instance.LookupEdgeAsync(id);
            var chrome = await chromeTask;
            var edge = await edgeTask;

            _rows.Add(new LookupRow("Chrome", chrome.Found ? "Found" : "Not Found",
                chrome.Found ? chrome.Name : "N/A", id, chrome.Url));
            _rows.Add(new LookupRow("Edge", edge.Found ? "Found" : "Not Found",
                edge.Found ? edge.Name : "N/A", id, edge.Url));

            if (chrome.Found && edge.Found)
                Report($"Found in both stores: Chrome (\"{chrome.Name}\") & Edge (\"{edge.Name}\")");
            else if (chrome.Found)
                Report($"Found in Chrome (\"{chrome.Name}\")");
            else if (edge.Found)
                Report($"Found in Edge (\"{edge.Name}\")");
            else
                Report($"Extension ID '{id}' was not found in either store.", StatusLevel.Warn);
        }
        finally
        {
            LookupButton.IsEnabled = true;
        }
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LookupRow r) return;
        Clipboard.SetText(r.Id);
        Report($"Copied ID: {r.Id}");
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as DataGrid)?.SelectedItem is not LookupRow r) return;
        if (r.Status != "Found") return;
        if (Util.OpenUrl(r.Url))
            Report($"Opened '{r.Name}' in your default browser.");
    }
}
