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

    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunBulkAsync();

    private async Task RunBulkAsync()
    {
        var ids = IdsBox.Text
            .Split('\n')
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => l.Length > 0)
            .ToList();

        if (ids.Count == 0)
        {
            Report("Please paste one or more extension IDs (one per line).", StatusLevel.Warn);
            return;
        }

        _rows.Clear();
        foreach (var id in ids)
        {
            var valid = StoreClient.IsValidId(id);
            _rows.Add(new BulkRow
            {
                Id = id,
                IsValid = valid,
                ChromeName = valid ? "" : "N/A",
                ChromeStatus = valid ? "Pending" : "Invalid",
                EdgeName = valid ? "" : "N/A",
                EdgeStatus = valid ? "Pending" : "Invalid",
            });
        }

        var pending = _rows.Where(r => r.IsValid).ToList();
        var invalidCount = _rows.Count - pending.Count;
        if (pending.Count == 0)
        {
            Report($"All {invalidCount} line(s) are invalid IDs. IDs are 32 characters, letters a-p only.", StatusLevel.Warn);
            return;
        }

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
            var invalidNote = invalidCount > 0 ? $" ({invalidCount} invalid line(s) skipped)" : "";
            Report($"Bulk lookup complete: {found} of {pending.Count} extension(s) found in at least one store.{invalidNote}");
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
            sb.AppendLine("Extension ID,Chrome Name,Chrome Status,Edge Name,Edge Status");
            foreach (var r in _rows)
            {
                sb.AppendLine(string.Join(",",
                    Util.CsvField(r.Id), Util.CsvField(r.ChromeName), Util.CsvField(r.ChromeStatus),
                    Util.CsvField(r.EdgeName), Util.CsvField(r.EdgeStatus)));
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
        Clipboard.SetText(r.Id);
        Report($"Copied ID: {r.Id}");
    }
}
