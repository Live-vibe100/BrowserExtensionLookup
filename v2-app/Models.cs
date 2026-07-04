using System.ComponentModel;

namespace BrowserExtensionLookup;

public enum Store { Chrome, Edge }

public enum StatusLevel { Info, Working, Warn }

/// <summary>One row in a store search result grid.</summary>
public record SearchResult(string Name, string Id, Store Store, string Developer, string Url);

/// <summary>Result of looking up a single extension ID against one store.</summary>
public record LookupResult(string Id, Store Store, bool Found, string Name, string Url);

/// <summary>Search results plus an error message when the store call failed outright.</summary>
public record SearchOutcome(List<SearchResult> Results, string? Error);

/// <summary>One row in the Lookup by ID grid.</summary>
public record LookupRow(string StoreName, string Status, string Name, string Id, string Url);

/// <summary>One row in the Bulk Lookup grid. Mutable so results can fill in as lookups finish.</summary>
public class BulkRow : INotifyPropertyChanged
{
    private string _chromeName = "";
    private string _chromeStatus = "";
    private string _edgeName = "";
    private string _edgeStatus = "";

    public string Id { get; init; } = "";
    public bool IsValid { get; init; }

    public string ChromeName { get => _chromeName; set { _chromeName = value; OnChanged(nameof(ChromeName)); } }
    public string ChromeStatus { get => _chromeStatus; set { _chromeStatus = value; OnChanged(nameof(ChromeStatus)); } }
    public string EdgeName { get => _edgeName; set { _edgeName = value; OnChanged(nameof(EdgeName)); } }
    public string EdgeStatus { get => _edgeStatus; set { _edgeStatus = value; OnChanged(nameof(EdgeStatus)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void SetResults(LookupResult chrome, LookupResult edge)
    {
        ChromeName = chrome.Found ? chrome.Name : "N/A";
        ChromeStatus = chrome.Found ? "Found" : "Not Found";
        EdgeName = edge.Found ? edge.Name : "N/A";
        EdgeStatus = edge.Found ? "Found" : "Not Found";
    }
}
