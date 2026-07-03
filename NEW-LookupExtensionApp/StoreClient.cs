using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrowserExtensionLookup;

/// <summary>
/// Talks to the Chrome Web Store and Edge Add-ons Store.
/// Same lookup strategies as the proven PowerShell v2.1 tool:
///   - Chrome: scrape the detail/search pages (og:title first, then &lt;title&gt;)
///   - Edge:   getproductdetailsbycrxid API first, page scrape as fallback;
///             search via the official v4 getfilteredorderedsearch API (up to 3 pages)
/// Store responses are untrusted input: parsed with regex/JSON only, never executed.
/// </summary>
public sealed class StoreClient
{
    public static StoreClient Instance { get; } = new();

    private const string ChromeUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36";
    private const string EdgeUA = ChromeUA + " Edg/125.0.0.0";
    private const string HtmlAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string JsonAccept = "application/json, text/plain, */*";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Extension IDs are exactly 32 characters, letters a-p only.</summary>
    public static bool IsValidId(string id) => Regex.IsMatch(id, "^[a-p]{32}$");

    public static string ChromeSearchUrl(string query) =>
        "https://chromewebstore.google.com/search/" + Uri.EscapeDataString(query);

    public static string EdgeSearchUrl(string query) =>
        "https://microsoftedge.microsoft.com/addons/search/" + Uri.EscapeDataString(query);

    public async Task<LookupResult> LookupChromeAsync(string id, CancellationToken ct = default)
    {
        var url = $"https://chromewebstore.google.com/detail/{id}";
        try
        {
            var html = await GetStringAsync(url, ChromeUA, HtmlAccept, ct);
            var title = ExtractTitle(html);
            if (title is not null && title != "Chrome Web Store")
            {
                var name = Regex.Replace(title, @"\s*-+\s*Chrome Web Store\s*$", "").Trim();
                if (name.Length > 0)
                    return new LookupResult(id, Store.Chrome, true, name, url);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* network/parse failure = not found, same as the PS tool */ }
        return new LookupResult(id, Store.Chrome, false, "", url);
    }

    public async Task<LookupResult> LookupEdgeAsync(string id, CancellationToken ct = default)
    {
        var url = $"https://microsoftedge.microsoft.com/addons/detail/{id}";

        // Strategy 1: the JSON API
        try
        {
            var json = await GetStringAsync(
                $"https://microsoftedge.microsoft.com/addons/getproductdetailsbycrxid/{id}",
                EdgeUA, JsonAccept, ct);
            if (json is not null)
            {
                using var doc = JsonDocument.Parse(json);
                var name = GetPropCI(doc.RootElement, "name")?.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    return new LookupResult(id, Store.Edge, true, name.Trim(), url);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        // Strategy 2: page title scrape
        try
        {
            var html = await GetStringAsync(url, EdgeUA, HtmlAccept, ct);
            var title = ExtractTitle(html);
            if (title is not null && title != "Microsoft Edge Add-ons")
            {
                var name = Regex.Replace(title, @"\s*-+\s*Microsoft Edge Add-?ons\s*$", "").Trim();
                if (name.Length > 0)
                    return new LookupResult(id, Store.Edge, true, name, url);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        return new LookupResult(id, Store.Edge, false, "", url);
    }

    /// <summary>
    /// Chrome has no public search API; parse extension links out of the search page HTML.
    /// Only results present in the server-rendered HTML are returned (JS-only results are missed).
    /// </summary>
    public async Task<SearchOutcome> SearchChromeAsync(string query, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        try
        {
            var html = await GetStringAsync(ChromeSearchUrl(query), ChromeUA, HtmlAccept, ct)
                ?? throw new HttpRequestException("search page returned an error");

            var seen = new HashSet<string>();
            foreach (Match m in Regex.Matches(html, @"/detail/([^/""'?#]+)/([a-p]{32})"))
            {
                var slug = m.Groups[1].Value;
                var id = m.Groups[2].Value;
                if (!seen.Add(id)) continue;

                var name = Uri.UnescapeDataString(slug).Replace('-', ' ');
                name = CultureInfo.GetCultureInfo("en-US").TextInfo.ToTitleCase(name.ToLowerInvariant());
                results.Add(new SearchResult(name.Trim(), id, Store.Chrome, "",
                    $"https://chromewebstore.google.com/detail/{slug}/{id}"));
            }
            return new SearchOutcome(results, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new SearchOutcome(results, Summarize(ex));
        }
    }

    /// <summary>Edge search via the official v4 API, following pagination up to 3 pages.</summary>
    public async Task<SearchOutcome> SearchEdgeAsync(string query, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        try
        {
            var seen = new HashSet<string>();
            for (var page = 1; page <= 3; page++)
            {
                var url = "https://microsoftedge.microsoft.com/addons/v4/getfilteredorderedsearch" +
                          "?hl=en-US&gl=US&filteredCategories=Edge-Extensions&filteredAddon=0" +
                          "&filterFeaturedAddons=false&filteredRating=0&sortBy=Relevance" +
                          $"&pgNo={page}&Query={Uri.EscapeDataString(query)}";

                var json = await GetStringAsync(url, EdgeUA, JsonAccept, ct)
                    ?? throw new HttpRequestException("search API returned an error");

                using var doc = JsonDocument.Parse(json);
                if (GetPropCI(doc.RootElement, "extensionList") is not { ValueKind: JsonValueKind.Array } list)
                    break;

                foreach (var ext in list.EnumerateArray())
                {
                    var id = GetPropCI(ext, "crxId")?.GetString();
                    var name = GetPropCI(ext, "name")?.GetString();
                    if (id is null || name is null || !seen.Add(id)) continue;

                    var dev = GetPropCI(ext, "developerName")?.GetString() ?? "";
                    results.Add(new SearchResult(WebUtility.HtmlDecode(name).Trim(), id, Store.Edge,
                        WebUtility.HtmlDecode(dev),
                        $"https://microsoftedge.microsoft.com/addons/detail/{id}"));
                }

                if (GetPropCI(doc.RootElement, "hasMorePages")?.ValueKind != JsonValueKind.True)
                    break;
            }
            return new SearchOutcome(results, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new SearchOutcome(results, Summarize(ex));
        }
    }

    /// <summary>GET a page/API response as a string; null when the server answers with an error status.</summary>
    private static async Task<string?> GetStringAsync(string url, string userAgent, string accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.TryAddWithoutValidation("Accept", accept);
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>og:title meta tag first, plain &lt;title&gt; as fallback; HTML entities decoded.</summary>
    private static string? ExtractTitle(string? html)
    {
        if (html is null) return null;
        var m = Regex.Match(html, @"<meta\s+property=""og:title""\s+content=""([^""]+)""");
        if (!m.Success) m = Regex.Match(html, @"<title[^>]*>([^<]+)</title>");
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value).Trim() : null;
    }

    /// <summary>Case-insensitive JSON property lookup (the store APIs are inconsistent about casing).</summary>
    private static JsonElement? GetPropCI(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in element.EnumerateObject())
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                return prop.Value;
        return null;
    }

    private static string Summarize(Exception ex) => ex switch
    {
        TaskCanceledException => "timed out",
        HttpRequestException h => h.Message,
        _ => ex.Message,
    };
}
