using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BrowserExtensionLookup.Views;

public partial class SearchView : UserControl
{
    public event Action<string, StatusLevel>? StatusReported;

    /// <summary>Raised when the user types an extension ID into the name search box.</summary>
    public event Action<string>? IdDetected;

    private readonly ObservableCollection<SearchResult> _chromeResults = new();
    private readonly ObservableCollection<SearchResult> _edgeResults = new();

    public SearchView()
    {
        InitializeComponent();
        ChromeGrid.ItemsSource = _chromeResults;
        EdgeGrid.ItemsSource = _edgeResults;
    }

    public void FocusQuery() => QueryBox.Focus();

    private void Report(string message, StatusLevel level = StatusLevel.Info) =>
        StatusReported?.Invoke(message, level);

    private void QueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = RunSearchAsync();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = RunSearchAsync();

    private async Task RunSearchAsync()
    {
        var query = QueryBox.Text.Trim();
        if (query.Length == 0)
        {
            Report("Please enter an extension name to search.", StatusLevel.Warn);
            return;
        }

        // An ID pasted into name search gives garbage fuzzy matches from the Edge
        // search API - hand it to the Lookup by ID tab instead.
        var maybeId = query.ToLowerInvariant();
        if (StoreClient.IsValidId(maybeId))
        {
            IdDetected?.Invoke(maybeId);
            return;
        }

        SearchButton.IsEnabled = false;
        _chromeResults.Clear();
        _edgeResults.Clear();
        Report($"Searching both stores for '{query}'...", StatusLevel.Working);

        try
        {
            var chromeTask = StoreClient.Instance.SearchChromeAsync(query);
            var edgeTask = StoreClient.Instance.SearchEdgeAsync(query);
            var chrome = await chromeTask;
            var edge = await edgeTask;

            foreach (var r in chrome.Results) _chromeResults.Add(r);
            foreach (var r in edge.Results) _edgeResults.Add(r);

            ReportSummary(query, chrome, edge);
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private void ReportSummary(string query, SearchOutcome chrome, SearchOutcome edge)
    {
        var c = chrome.Results.Count;
        var e = edge.Results.Count;

        var errors = "";
        if (chrome.Error is not null) errors += $" Chrome search failed ({chrome.Error}).";
        if (edge.Error is not null) errors += $" Edge search failed ({edge.Error}).";

        if (c == 0 && e == 0)
            Report($"No results from either store for '{query}'.{errors} Click 'Open in browser' on either side to search the live store directly.", StatusLevel.Warn);
        else if (c == 0)
            Report($"Found {e} Edge result(s). Chrome returned nothing.{errors} Click Chrome 'Open in browser' to search the live store.", StatusLevel.Warn);
        else if (e == 0)
            Report($"Found {c} Chrome result(s). Edge returned nothing (brand-verified listings may be hidden).{errors} Click Edge 'Open in browser' to search the live store.", StatusLevel.Warn);
        else
            Report($"Found {c} Chrome result(s) and {e} Edge result(s) for '{query}'");
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SearchResult r) return;
        Clipboard.SetText(r.Id);
        Report($"Copied {r.Store} ID: {r.Id}");
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as DataGrid)?.SelectedItem is not SearchResult r) return;
        if (Util.OpenUrl(r.Url))
            Report($"Opened '{r.Name}' in your default browser.");
        else
            Report("Could not open the browser.", StatusLevel.Warn);
    }

    private void OpenChrome_Click(object sender, RoutedEventArgs e) => OpenStoreSearch(Store.Chrome);
    private void OpenEdge_Click(object sender, RoutedEventArgs e) => OpenStoreSearch(Store.Edge);

    private void OpenStoreSearch(Store store)
    {
        var query = QueryBox.Text.Trim();
        if (query.Length == 0)
        {
            Report("Enter an extension name first, then click 'Open in browser'.", StatusLevel.Warn);
            return;
        }

        var url = store == Store.Chrome ? StoreClient.ChromeSearchUrl(query) : StoreClient.EdgeSearchUrl(query);
        var storeName = store == Store.Chrome ? "Chrome Web Store" : "Edge Add-ons";
        if (Util.OpenUrl(url))
            Report($"Opened {storeName} search for '{query}' in your default browser.");
        else
            Report("Could not open the browser.", StatusLevel.Warn);
    }
}
