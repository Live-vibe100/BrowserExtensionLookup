using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BrowserExtensionLookup.Views;

public partial class BulkView : UserControl
{
    // Polite ceiling on simultaneous IDs in flight (each ID = 1 Chrome + 1 Edge request).
    private const int MaxConcurrentLookups = 6;

    public event Action<string, StatusLevel>? StatusReported;

    private readonly ObservableCollection<BulkRow> _rows = new();
    private CancellationTokenSource? _cts;

    public BulkView()
    {
        InitializeComponent();
        ResultGrid.ItemsSource = _rows;
    }

    private void Report(string message, StatusLevel level = StatusLevel.Info) =>
        StatusReported?.Invoke(message, level);

    // async void so any unexpected exception reaches App.DispatcherUnhandledException instead of vanishing.
    private async void Run_Click(object sender, RoutedEventArgs e) => await RunBulkAsync();

    private async Task RunBulkAsync()
    {
        var extraction = IdExtractor.Extract(IdsBox.Text);
        var noIdNote = extraction.LinesWithoutId == 0 ? ""
            : $" {extraction.LinesWithoutId} line(s) had no extension ID in them (first: '{Shorten(extraction.FirstLineWithoutId!)}').";
        var dupeNote = extraction.Duplicates > 0 ? $" {extraction.Duplicates} duplicate(s) removed." : "";

        if (extraction.Ids.Count == 0)
        {
            Report(IdsBox.Text.Trim().Length == 0
                ? "Please paste some extension IDs, store URLs or policy lines."
                : "No extension IDs found. IDs are 32 characters, letters a-p only." + noIdNote, StatusLevel.Warn);
            return;
        }

        _rows.Clear();
        foreach (var id in extraction.Ids)
            _rows.Add(new BulkRow { Id = id, ChromeStatus = "Pending", EdgeStatus = "Pending" });
        var pending = _rows.ToList();

        RunButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ExportButton.IsEnabled = false;
        ProgressPanel.Visibility = Visibility.Visible;
        Progress.Maximum = pending.Count;
        Progress.Value = 0;
        ProgressText.Text = $"0 / {pending.Count}";
        Report($"Looking up {pending.Count} ID(s) across both stores...", StatusLevel.Working);

        _cts = new CancellationTokenSource();
        var done = 0;
        var gate = new SemaphoreSlim(MaxConcurrentLookups);

        // Started from (and resumed on) the UI thread, so row/progress updates are safe here.
        async Task ProcessRow(BulkRow row, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            try
            {
                var chromeTask = StoreClient.Instance.LookupChromeAsync(row.Id, ct);
                var edgeTask = StoreClient.Instance.LookupEdgeAsync(row.Id, ct);
                await Task.WhenAll(chromeTask, edgeTask);
                row.SetResults(chromeTask.Result, edgeTask.Result);
            }
            finally
            {
                gate.Release();
            }

            done++;
            Progress.Value = done;
            ProgressText.Text = $"{done} / {pending.Count}";
        }

        try
        {
            await Task.WhenAll(pending.Select(r => ProcessRow(r, _cts.Token)));

            var found = _rows.Count(r => r.ChromeStatus == "Found" || r.EdgeStatus == "Found");
            var removed = _rows.Count(r => r.ChromeStatus == "Removed" || r.EdgeStatus == "Removed");
            var mv2 = _rows.Count(r => r.ChromeManifest == "MV2" || r.EdgeManifest == "MV2");
            var errors = _rows.Count(r => r.ChromeStatus.StartsWith("Error") || r.EdgeStatus.StartsWith("Error"));

            var summary = $"Bulk lookup complete: {found} of {pending.Count} extension(s) found in at least one store.";
            if (removed > 0) summary += $" {removed} removed from a store.";
            if (mv2 > 0) summary += $" {mv2} still Manifest V2.";
            if (errors > 0) summary += $" {errors} lookup(s) failed with an error; run again to retry them.";
            Report(summary + dupeNote + noIdNote, errors > 0 ? StatusLevel.Warn : StatusLevel.Info);
        }
        catch (OperationCanceledException)
        {
            foreach (var row in _rows.Where(r => r.ChromeStatus == "Pending"))
            {
                row.ChromeStatus = "Cancelled";
                row.EdgeStatus = "Cancelled";
            }
            Report($"Bulk lookup cancelled after {done} of {pending.Count} ID(s).", StatusLevel.Warn);
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            RunButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ExportButton.IsEnabled = _rows.Count > 0;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.IsEnabled = false;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"extension-lookup-{DateTime.Now:yyyy-MM-dd-HHmm}.csv",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("Extension ID,Chrome Name,Chrome Status,Chrome Manifest,Chrome Users,Edge Name,Edge Status,Edge Manifest,Edge Users");
            foreach (var r in _rows)
            {
                sb.AppendLine(string.Join(",",
                    Util.CsvField(r.Id),
                    Util.CsvField(r.ChromeName), Util.CsvField(r.ChromeStatus), Util.CsvField(r.ChromeManifest), Util.CsvField(r.ChromeUsers),
                    Util.CsvField(r.EdgeName), Util.CsvField(r.EdgeStatus), Util.CsvField(r.EdgeManifest), Util.CsvField(r.EdgeUsers)));
            }
            // UTF-8 with BOM so Excel opens it cleanly
            File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
            Report($"Exported {_rows.Count} row(s) to {dlg.FileName}");
        }
        catch (Exception ex)
        {
            Report($"CSV export failed: {ex.Message}", StatusLevel.Warn);
        }
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BulkRow r) return;
        if (Util.TryCopy(r.Id) is { } error) Report(error, StatusLevel.Warn);
        else Report($"Copied ID: {r.Id}");
    }

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..40] + "...";
}
