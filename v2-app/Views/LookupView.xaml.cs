using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BrowserExtensionLookup.Views;

public partial class LookupView : UserControl
{
    public event Action<string, StatusLevel>? StatusReported;

    private readonly ObservableCollection<LookupRow> _rows = new();
    private bool _busy;

    public LookupView()
    {
        InitializeComponent();
        ResultGrid.ItemsSource = _rows;
    }

    private void Report(string message, StatusLevel level = StatusLevel.Info) =>
        StatusReported?.Invoke(message, level);

    // async void so any unexpected exception reaches App.DispatcherUnhandledException instead of vanishing.
    private async void IdBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunLookupAsync();
    }

    private async void Lookup_Click(object sender, RoutedEventArgs e) => await RunLookupAsync();

    /// <summary>Fill in an ID and run the lookup (used when name search detects a pasted ID).</summary>
    public async void LookupId(string id)
    {
        if (_busy)
        {
            Report("A lookup is already running. Try again when it finishes.", StatusLevel.Warn);
            return;
        }
        IdBox.Text = id;
        await RunLookupAsync();
    }

    private async Task RunLookupAsync()
    {
        // Repeated Enter presses while a lookup is running are ignored (they used to mix two result sets).
        if (_busy) return;

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

        _busy = true;
        LookupButton.IsEnabled = false;
        _rows.Clear();
        Report($"Looking up {id} in both stores...", StatusLevel.Working);

        try
        {
            var chromeTask = StoreClient.Instance.LookupChromeAsync(id);
            var edgeTask = StoreClient.Instance.LookupEdgeAsync(id);
            var chrome = await chromeTask;
            var edge = await edgeTask;

            _rows.Add(ToRow("Chrome", chrome));
            _rows.Add(ToRow("Edge", edge));

            // Found in one store only: look for the same extension's listing (and ID) in the other.
            StoreMatch? match = null;
            if (StoreMatcher.SourceFor(chrome, edge) is { } source)
            {
                var target = source.Store == Store.Chrome ? "Edge" : "Chrome";
                Report($"Found in {source.Store}. Looking for the same extension in {target}...", StatusLevel.Working);
                match = await StoreMatcher.Instance.FindAsync(source);
                _rows.Add(new LookupRow($"{target} match", match.ConfidenceText, match.HasListing ? match.Name : "N/A",
                    match.Id, match.Url, match.UsersText, "", match.HasListing));
            }
            ReportSummary(id, chrome, edge, match);
        }
        finally
        {
            _busy = false;
            LookupButton.IsEnabled = true;
        }
    }

    private static LookupRow ToRow(string storeName, LookupResult r) =>
        new(storeName, r.StatusText, r.NameText, r.Id, r.Url, r.UsersText, r.ManifestText, r.Found);

    private void ReportSummary(string id, LookupResult chrome, LookupResult edge, StoreMatch? match)
    {
        static string? Describe(string store, LookupResult r) => r.Status switch
        {
            LookupStatus.Found => $"{store}: \"{r.Name}\"" + (r.ManifestVersion == 2 ? " (Manifest V2, being phased out)" : ""),
            LookupStatus.Removed => $"{store}: removed from the store",
            LookupStatus.Error => $"{store}: check failed ({r.ErrorReason})",
            _ => null,
        };

        var parts = new[] { Describe("Chrome", chrome), Describe("Edge", edge) }.Where(p => p is not null).ToList();
        var anyError = chrome.Status == LookupStatus.Error || edge.Status == LookupStatus.Error;

        if (match is not null)
        {
            parts.Add(match.Confidence switch
            {
                MatchConfidence.SamePublisher => $"{match.Store} match: \"{match.Name}\" (same publisher)",
                MatchConfidence.NameOnly => $"{match.Store} match: \"{match.Name}\" (same name only, check it before using)",
                MatchConfidence.None => $"No {match.Store} match",
                _ => $"{match.Store} match check failed ({match.ErrorReason})",
            } + (match.Note is null || match.Confidence == MatchConfidence.NameOnly ? "" : $". {match.Note}"));
            anyError |= match.Confidence == MatchConfidence.Error;
        }

        if (parts.Count == 0)
            Report($"Extension ID '{id}' was not found in either store.", StatusLevel.Warn);
        else
            Report(string.Join("  |  ", parts),
                anyError || match?.Confidence == MatchConfidence.NameOnly || (!chrome.Found && !edge.Found)
                    ? StatusLevel.Warn : StatusLevel.Info);
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LookupRow r) return;
        if (r.Id.Length == 0)
        {
            Report("No match was found, so there's no ID to copy.", StatusLevel.Warn);
            return;
        }
        if (Util.TryCopy(r.Id) is { } error) Report(error, StatusLevel.Warn);
        else Report($"Copied ID: {r.Id}");
    }

    private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as DataGrid)?.SelectedItem is not LookupRow r) return;
        if (!r.Found) return;
        if (Util.OpenUrl(r.Url))
            Report($"Opened '{r.Name}' in your default browser.");
    }
}
