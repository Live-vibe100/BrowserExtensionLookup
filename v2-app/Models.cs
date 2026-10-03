using System.ComponentModel;

namespace BrowserExtensionLookup;

public enum Store { Chrome, Edge }

public enum StatusLevel { Info, Working, Warn }

/// <summary>
/// Outcome of one ID lookup. NotFound and Removed are definite answers from the store;
/// Error means we couldn't get an answer (timeout, rate limit, server error, offline).
/// </summary>
public enum LookupStatus { Found, NotFound, Removed, Error }

/// <summary>Display helpers shared by the grids and the CSV export.</summary>
public static class Format
{
    public static string Users(long? users) => users is > 0 ? users.Value.ToString("N0") : "";

    public static string Rating(double? rating, long? count) =>
        rating is > 0 ? $"{rating.Value:0.0} ({count ?? 0:N0})" : "";

    /// <summary>"MV2" / "MV3" when the store told us, "?" when it didn't.</summary>
    public static string Manifest(int? version) => version is int v ? $"MV{v}" : "?";
}

/// <summary>One row in a store search result grid.</summary>
public record SearchResult(string Name, string Id, Store Store, string Developer, string Url,
    long? Users = null, double? Rating = null, long? RatingCount = null)
{
    public string UsersText => Format.Users(Users);
    public string RatingText => Format.Rating(Rating, RatingCount);
}

/// <summary>Result of looking up a single extension ID against one store.</summary>
public record LookupResult(string Id, Store Store, LookupStatus Status, string Name, string Url,
    long? Users = null, int? ManifestVersion = null, string? ErrorReason = null)
{
    public bool Found => Status == LookupStatus.Found;

    public string StatusText => Status switch
    {
        LookupStatus.Found => "Found",
        LookupStatus.NotFound => "Not Found",
        LookupStatus.Removed => "Removed",
        _ => $"Error ({ErrorReason ?? "unknown"})",
    };

    public string NameText => Found ? Name : "N/A";
    public string UsersText => Found ? Format.Users(Users) : "";
    public string ManifestText => Found ? Format.Manifest(ManifestVersion) : "";
}

/// <summary>Search results, an error message when the store call failed outright, and an optional note.</summary>
public record SearchOutcome(List<SearchResult> Results, string? Error, string? Note = null);

/// <summary>One row in the Lookup by ID grid.</summary>
public record LookupRow(string StoreName, string Status, string Name, string Id, string Url,
    string Users, string Manifest, bool Found);

/// <summary>One row in the Bulk Lookup grid. Mutable so results can fill in as lookups finish.</summary>
public class BulkRow : INotifyPropertyChanged
{
    private string _chromeName = "";
    private string _chromeStatus = "";
    private string _chromeManifest = "";
    private string _chromeUsers = "";
    private string _edgeName = "";
    private string _edgeStatus = "";
    private string _edgeManifest = "";
    private string _edgeUsers = "";

    public string Id { get; init; } = "";

    public string ChromeName { get => _chromeName; set { _chromeName = value; OnChanged(nameof(ChromeName)); } }
    public string ChromeStatus { get => _chromeStatus; set { _chromeStatus = value; OnChanged(nameof(ChromeStatus)); } }
    public string ChromeManifest { get => _chromeManifest; set { _chromeManifest = value; OnChanged(nameof(ChromeManifest)); } }
    public string ChromeUsers { get => _chromeUsers; set { _chromeUsers = value; OnChanged(nameof(ChromeUsers)); } }
    public string EdgeName { get => _edgeName; set { _edgeName = value; OnChanged(nameof(EdgeName)); } }
    public string EdgeStatus { get => _edgeStatus; set { _edgeStatus = value; OnChanged(nameof(EdgeStatus)); } }
    public string EdgeManifest { get => _edgeManifest; set { _edgeManifest = value; OnChanged(nameof(EdgeManifest)); } }
    public string EdgeUsers { get => _edgeUsers; set { _edgeUsers = value; OnChanged(nameof(EdgeUsers)); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void SetResults(LookupResult chrome, LookupResult edge)
    {
        ChromeName = chrome.NameText;
        ChromeStatus = chrome.StatusText;
        ChromeManifest = chrome.ManifestText;
        ChromeUsers = chrome.UsersText;
        EdgeName = edge.NameText;
        EdgeStatus = edge.StatusText;
        EdgeManifest = edge.ManifestText;
        EdgeUsers = edge.UsersText;
    }
}
