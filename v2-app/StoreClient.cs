using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BrowserExtensionLookup;

/// <summary>
/// Talks to the Chrome Web Store and Edge Add-ons Store.
///   - Chrome: read the structured data block embedded in the detail/search pages
///             (falls back to og:title / URL slugs if the block isn't there)
///   - Edge:   getproductdetailsbycrxid API first, page scrape as fallback;
///             search via the official v4 getfilteredorderedsearch API (up to 3 pages)
/// Every lookup ends as Found / NotFound / Removed / Error - a network problem is never
/// reported as "not found".
/// Store responses are untrusted input: parsed with regex/JSON only, never executed.
/// </summary>
public sealed class StoreClient
{
    public static StoreClient Instance { get; } = new();

    private const string ChromeUA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/152.0.0.0 Safari/537.36";
    private const string EdgeUA = ChromeUA + " Edg/152.0.0.0";
    private const string HtmlAccept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
    private const string JsonAccept = "application/json, text/plain, */*";

    // Store pages are ~0.7 MB; anything far bigger is not a page we want to parse.
    private const long MaxResponseBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;

    /// <param name="handler">Only the self-test passes one (to simulate store failures offline).</param>
    public StoreClient(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    /// <summary>Extension IDs are exactly 32 characters, letters a-p only.</summary>
    public static bool IsValidId(string id) => Regex.IsMatch(id, @"^[a-p]{32}\z");

    public static string ChromeSearchUrl(string query) =>
        "https://chromewebstore.google.com/search/" + Uri.EscapeDataString(query);

    public static string EdgeSearchUrl(string query) =>
        "https://microsoftedge.microsoft.com/addons/search/" + Uri.EscapeDataString(query);

    public async Task<LookupResult> LookupChromeAsync(string id, CancellationToken ct = default)
    {
        var url = $"https://chromewebstore.google.com/detail/{id}";
        LookupResult Result(LookupStatus status, string name = "", long? users = null, int? manifest = null,
            string? error = null, string? website = null) =>
            new(id, Store.Chrome, status, name, url, users, manifest, error, website);

        try
        {
            var resp = await GetAsync(url, ChromeUA, HtmlAccept, ct);
            if (resp.Status == HttpStatusCode.NotFound) return Result(LookupStatus.NotFound);
            if (!resp.Ok) return Result(LookupStatus.Error, error: Describe(resp.Status));

            // 1. The structured data block for this exact ID
            var item = ChromeData.FindItems(resp.Body).FirstOrDefault(i => i.Id == id);
            if (item is not null)
                return Result(LookupStatus.Found, item.Name, item.Users, item.ManifestVersion, website: item.Website);

            // 2. The store's own status code for the page. Observed 2026-10-02:
            //    5 (NOT_FOUND) for IDs that never existed, 7 for listings the store took down.
            switch (ChromeData.FindErrorStatus(resp.Body))
            {
                case 5: return Result(LookupStatus.NotFound);
                case 7: return Result(LookupStatus.Removed);
                case int code: return Result(LookupStatus.Error, error: $"store status {code}");
            }

            // 3. Older page layout: name from the page title, manifest unknown
            var title = ExtractTitle(resp.Body);
            if (title is not null && title != "Chrome Web Store")
            {
                var name = Regex.Replace(title, @"\s*-+\s*Chrome Web Store\s*$", "").Trim();
                if (name.Length > 0) return Result(LookupStatus.Found, name);
            }
            if (resp.FinalUri?.AbsolutePath.Contains("/empty-title/") == true)
                return Result(LookupStatus.NotFound);

            return Result(LookupStatus.Error, error: "unexpected page");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Result(LookupStatus.Error, error: Describe(ex));
        }
    }

    public async Task<LookupResult> LookupEdgeAsync(string id, CancellationToken ct = default)
    {
        var url = $"https://microsoftedge.microsoft.com/addons/detail/{id}";
        LookupResult Result(LookupStatus status, string name = "", long? users = null, int? manifest = null,
            string? error = null, string? website = null) =>
            new(id, Store.Edge, status, name, url, users, manifest, error, website);

        // Strategy 1: the JSON API. A 404 here is a definite "no such extension".
        string? apiProblem = null;
        try
        {
            var resp = await GetAsync(
                $"https://microsoftedge.microsoft.com/addons/getproductdetailsbycrxid/{id}",
                EdgeUA, JsonAccept, ct);
            if (resp.Status == HttpStatusCode.NotFound) return Result(LookupStatus.NotFound);

            if (!resp.Ok)
            {
                apiProblem = Describe(resp.Status);
            }
            else
            {
                using var doc = JsonDocument.Parse(resp.Body);
                var root = doc.RootElement;
                var name = GetPropCI(root, "name") is { ValueKind: JsonValueKind.String } n ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var users = GetPropCI(root, "activeInstallCount") is { ValueKind: JsonValueKind.Number } u
                        && u.TryGetInt64(out var count) ? count : (long?)null;
                    int? manifest = GetPropCI(root, "isManifestV2")?.ValueKind switch
                    {
                        JsonValueKind.True => 2,
                        JsonValueKind.False => 3,
                        _ => null,
                    };
                    var website = GetPropCI(root, "publisherWebsiteUri") is { ValueKind: JsonValueKind.String } w ? w.GetString() : null;
                    return Result(LookupStatus.Found, WebUtility.HtmlDecode(name).Trim(), users, manifest, website: website);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            apiProblem = Describe(ex);
        }

        // Strategy 2: page title scrape (API failed, or answered without a name)
        try
        {
            var resp = await GetAsync(url, EdgeUA, HtmlAccept, ct);
            if (resp.Status == HttpStatusCode.NotFound) return Result(LookupStatus.NotFound);
            if (!resp.Ok) return Result(LookupStatus.Error, error: apiProblem ?? Describe(resp.Status));

            var title = ExtractTitle(resp.Body);
            if (title is not null && title != "Microsoft Edge Add-ons")
            {
                var name = Regex.Replace(title, @"\s*-+\s*Microsoft Edge Add-?ons\s*$", "").Trim();
                if (name.Length > 0) return Result(LookupStatus.Found, name);
            }

            // Generic store page = no such extension, unless the API failed - then we can't be sure.
            return apiProblem is null
                ? Result(LookupStatus.NotFound)
                : Result(LookupStatus.Error, error: apiProblem);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Result(LookupStatus.Error, error: apiProblem ?? Describe(ex));
        }
    }

    /// <summary>
    /// Chrome has no public search API. The search page embeds its first 10 results as a
    /// structured data block, which gives real names and user counts. If that block is
    /// missing, fall back to pulling links out of the HTML (names rebuilt from URL slugs).
    /// </summary>
    public async Task<SearchOutcome> SearchChromeAsync(string query, CancellationToken ct = default)
    {
        var results = new List<SearchResult>();
        try
        {
            var resp = await GetAsync(ChromeSearchUrl(query), ChromeUA, HtmlAccept, ct);
            if (!resp.Ok) throw new HttpRequestException(Describe(resp.Status));

            foreach (var item in ChromeData.FindItems(resp.Body))
            {
                results.Add(new SearchResult(item.Name, item.Id, Store.Chrome, "",
                    $"https://chromewebstore.google.com/detail/{item.Id}",
                    item.Users, item.Rating, item.RatingCount, item.Website));
            }
            if (results.Count > 0) return new SearchOutcome(results, null);

            // Fallback: slug-based names. Google truncates slugs, so say so.
            var seen = new HashSet<string>();
            foreach (Match m in Regex.Matches(resp.Body, @"/detail/([^/""'?#]+)/([a-p]{32})"))
            {
                var slug = m.Groups[1].Value;
                var id = m.Groups[2].Value;
                if (!seen.Add(id)) continue;

                var name = Uri.UnescapeDataString(slug).Replace('-', ' ');
                name = CultureInfo.GetCultureInfo("en-US").TextInfo.ToTitleCase(name.ToLowerInvariant());
                results.Add(new SearchResult(name.Trim(), id, Store.Chrome, "",
                    $"https://chromewebstore.google.com/detail/{slug}/{id}"));
            }
            var note = results.Count > 0
                ? "Chrome's result data wasn't in the page, so names were rebuilt from links and may be cut short."
                : null;
            return new SearchOutcome(results, null, note);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new SearchOutcome(results, Describe(ex));
        }
    }

    /// <summary>Edge search via the official v4 API, following pagination up to 3 pages (20 results each).</summary>
    public async Task<SearchOutcome> SearchEdgeAsync(string query, CancellationToken ct = default, int maxPages = 3)
    {
        var results = new List<SearchResult>();
        try
        {
            var seen = new HashSet<string>();
            for (var page = 1; page <= maxPages; page++)
            {
                var url = "https://microsoftedge.microsoft.com/addons/v4/getfilteredorderedsearch" +
                          "?hl=en-US&gl=US&filteredCategories=Edge-Extensions&filteredAddon=0" +
                          "&filterFeaturedAddons=false&filteredRating=0&sortBy=Relevance" +
                          $"&pgNo={page}&Query={Uri.EscapeDataString(query)}";

                var resp = await GetAsync(url, EdgeUA, JsonAccept, ct);
                if (!resp.Ok) throw new HttpRequestException(Describe(resp.Status));

                using var doc = JsonDocument.Parse(resp.Body);
                if (GetPropCI(doc.RootElement, "extensionList") is not { ValueKind: JsonValueKind.Array } list)
                    break;

                foreach (var ext in list.EnumerateArray())
                {
                    var id = GetPropCI(ext, "crxId")?.GetString();
                    var name = GetPropCI(ext, "name")?.GetString();
                    if (id is null || name is null || !seen.Add(id)) continue;

                    var dev = GetPropCI(ext, "developerName")?.GetString() ?? "";
                    // activeInstallCount is always 0 in search results, so users are left blank here.
                    var rating = GetPropCI(ext, "averageRating") is { ValueKind: JsonValueKind.Number } r ? r.GetDouble() : (double?)null;
                    var ratingCount = GetPropCI(ext, "noOfRatings") is { ValueKind: JsonValueKind.Number } rc
                        && rc.TryGetInt64(out var c) ? c : (long?)null;
                    results.Add(new SearchResult(WebUtility.HtmlDecode(name).Trim(), id, Store.Edge,
                        WebUtility.HtmlDecode(dev),
                        $"https://microsoftedge.microsoft.com/addons/detail/{id}",
                        null, rating, ratingCount));
                }

                if (GetPropCI(doc.RootElement, "hasMorePages")?.ValueKind != JsonValueKind.True)
                    break;
            }
            return new SearchOutcome(results, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new SearchOutcome(results, Describe(ex));
        }
    }

    private sealed record Response(HttpStatusCode Status, string Body, Uri? FinalUri)
    {
        public bool Ok => (int)Status is >= 200 and < 300;
    }

    /// <summary>
    /// GET a page/API response. Error statuses come back as a Response (so callers can tell
    /// 404 from 503); network failures and timeouts throw.
    /// </summary>
    private async Task<Response> GetAsync(string url, string userAgent, string accept, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        req.Headers.TryAddWithoutValidation("Accept", accept);
        req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        using var resp = await _http.SendAsync(req, ct);
        var body = resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : "";
        return new Response(resp.StatusCode, body, resp.RequestMessage?.RequestUri);
    }

    /// <summary>Short, grid-friendly reason for an HTTP error status.</summary>
    private static string Describe(HttpStatusCode status) => (int)status switch
    {
        429 => "rate limited",
        _ => $"HTTP {(int)status}",
    };

    /// <summary>Short, grid-friendly reason for a failed request.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or TimeoutException => "timed out",
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError } => "no connection",
        HttpRequestException { StatusCode: null, HttpRequestError: not HttpRequestError.Unknown } => "network error",
        HttpRequestException h => h.Message,
        JsonException or RegexMatchTimeoutException => "unreadable response",
        _ => ex.Message,
    };

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
}

/// <summary>One extension as described by the Chrome store's embedded data.</summary>
internal sealed record ChromeItem(string Id, string Name, double? Rating, long? RatingCount,
    string? Website, long? Users, int? ManifestVersion);

/// <summary>
/// Reads the structured data Chrome Web Store pages embed as
/// <c>AF_initDataCallback({key: 'ds:N', hash: '..', data:[...], sideChannel: {}});</c>.
/// Items are found by shape, not by a fixed path: any array whose [0] is an extension ID
/// and whose [2] is a string. Field positions observed 2026-10-02 (same on search and detail pages):
/// [2] name, [3] rating, [4] rating count, [7] website, [14] users, [18] manifest JSON.
/// </summary>
internal static class ChromeData
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    private static readonly Regex DataBlock = new(
        @"AF_initDataCallback\(\{key:\s*'ds:\d+',\s*hash:\s*'\d+',\s*data:(.*?), sideChannel: \{\}\}\);</script>",
        RegexOptions.Singleline | RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex ErrorBlock = new(
        @"AF_initDataCallback\(\{key:\s*'ds:\d+',\s*data:\[(\d+)\],\s*errorHasStatus:\s*true",
        RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex ManifestVersion = new(
        @"""manifest_version""\s*:\s*(\d+)", RegexOptions.Compiled, RegexTimeout);

    private static readonly Regex IdShape = new("^[a-p]{32}$", RegexOptions.Compiled);

    public static List<ChromeItem> FindItems(string html)
    {
        var items = new List<ChromeItem>();
        var seen = new HashSet<string>();
        foreach (Match m in DataBlock.Matches(html))
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(m.Groups[1].Value, new JsonDocumentOptions { MaxDepth = 256 });
            }
            catch (JsonException)
            {
                continue; // one malformed block shouldn't hide the others; callers fall back if nothing is found
            }
            using (doc) Walk(doc.RootElement, items, seen);
        }
        return items;
    }

    /// <summary>The store's status code when the page is an error page (e.g. 5 or 7), else null.</summary>
    public static int? FindErrorStatus(string html)
    {
        var m = ErrorBlock.Match(html);
        return m.Success && int.TryParse(m.Groups[1].Value, out var code) ? code : null;
    }

    private static void Walk(JsonElement node, List<ChromeItem> items, HashSet<string> seen)
    {
        if (node.ValueKind != JsonValueKind.Array) return;

        if (node.GetArrayLength() >= 3
            && node[0].ValueKind == JsonValueKind.String && IdShape.IsMatch(node[0].GetString()!)
            && node[2].ValueKind == JsonValueKind.String)
        {
            var id = node[0].GetString()!;
            if (seen.Add(id)) items.Add(ToItem(id, node));
            return;
        }

        foreach (var child in node.EnumerateArray())
            Walk(child, items, seen);
    }

    private static ChromeItem ToItem(string id, JsonElement a)
    {
        double? Num(int i) => a.GetArrayLength() > i && a[i].ValueKind == JsonValueKind.Number ? a[i].GetDouble() : null;
        string? Str(int i) => a.GetArrayLength() > i && a[i].ValueKind == JsonValueKind.String ? a[i].GetString() : null;

        int? manifest = null;
        if (Str(18) is { } manifestJson && ManifestVersion.Match(manifestJson) is { Success: true } mv
            && int.TryParse(mv.Groups[1].Value, out var v))
            manifest = v;

        return new ChromeItem(id, Str(2)!.Trim(), Num(3), (long?)Num(4),
            Str(7), (long?)Num(14), manifest);
    }
}
