using System.Diagnostics;

namespace BrowserExtensionLookup;

internal static class Util
{
    /// <summary>Open a URL in the default browser. URLs are always built from fixed store hosts.</summary>
    public static bool OpenUrl(string url)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Escape one CSV field: wrap in quotes, double any embedded quotes.</summary>
    public static string CsvField(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
