using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

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

    /// <summary>
    /// Put text on the clipboard. Returns an error message instead of throwing when another
    /// app is holding the clipboard open (CLIPBRD_E_CANT_OPEN).
    /// </summary>
    public static string? TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return null;
        }
        catch (ExternalException ex)
        {
            return $"Couldn't copy: the clipboard is in use by another app. Try again. ({ex.Message})";
        }
    }

    /// <summary>Escape one CSV field: wrap in quotes, double any embedded quotes.</summary>
    public static string CsvField(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}

/// <summary>What the Bulk tab pulled out of the pasted text.</summary>
public sealed record IdExtraction(List<string> Ids, int Duplicates, int LinesWithoutId, string? FirstLineWithoutId);

public static class IdExtractor
{
    // 32 letters a-p with no other letter/digit glued on either side, so IDs are found inside
    // store URLs, Intune "id;update-url" lines, CSV and JSON alike.
    private static readonly Regex IdInText = new("(?<![a-z0-9])[a-p]{32}(?![a-z0-9])", RegexOptions.Compiled);

    public static IdExtraction Extract(string text)
    {
        var ids = new List<string>();
        var seen = new HashSet<string>();
        int duplicates = 0, linesWithoutId = 0;
        string? firstWithoutId = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var matches = IdInText.Matches(line.ToLowerInvariant());
            if (matches.Count == 0)
            {
                linesWithoutId++;
                firstWithoutId ??= line;
                continue;
            }
            foreach (Match m in matches)
            {
                if (seen.Add(m.Value)) ids.Add(m.Value);
                else duplicates++;
            }
        }
        return new IdExtraction(ids, duplicates, linesWithoutId, firstWithoutId);
    }
}

/// <summary>Colours a status cell: Found/Same publisher green, Not Found red, Removed/Error/check-it orange, the rest muted.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "Found" or "Same publisher" => "GreenBrush",
            "Name match, check it" => "OrangeBrush",
            "No match" => "TextMutedBrush",
            "Not Found" => "RedBrush",
            "Removed" => "OrangeBrush",
            { } s when s.StartsWith("Error", StringComparison.Ordinal) => "OrangeBrush",
            "MV2" => "OrangeBrush",
            "Pending" or "Cancelled" or "?" => "TextMutedBrush",
            _ => "TextSecondaryBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
