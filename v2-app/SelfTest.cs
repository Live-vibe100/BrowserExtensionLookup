using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace BrowserExtensionLookup;

/// <summary>
/// Headless verification (run with --selftest). Offline checks first (ID parsing, page parsing,
/// simulated store failures), then live checks against known-good extensions.
/// Prints PASS/FAIL per check. Exit code 0 = all passed.
/// </summary>
internal static class SelfTest
{
    // Live IDs, all checked against the real stores on 2026-10-02
    private const string ChromeTranslateId = "aapbdbdomjkkjkaonfhkkikfgjllcleb"; // Google Translate, MV3
    private const string EdgeGrammarlyId = "cnlefmmeadmemmdciolhbnfeacpdfbkd";   // Grammarly
    private const string EdgeUblockId = "odfafepnkmbhccpbejgmiehpchacaeak";      // uBlock Origin on Edge, still listed, MV2
    private const string ChromeUblockId = "cjpalhdlnbpafiamejdnhcphjbkeiagm";    // uBlock Origin on Chrome, removed (MV2 phase-out)
    private const string BogusId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // Cross-store pairs, checked 2026-10-03
    private const string ChromeNordVpnId = "fjoaledfpmneenckfbpdfhkmimnjocfa";   // "VPN for Chrome: NordVPN proxy protection"
    private const string EdgeNordVpnId = "fphgeikpdcdcheaochkhldmnfblfogla";     // "NordVPN - the Fastest VPN proxy for privacy"
    private const string ChromeGrammarlyId = "kbfnbcaeplbcioakkpcpgfkobkghlhen";
    private const string ChromeDarkReaderId = "eimadpbcbfnmbkopoojfekhnkhdbieeh";
    private const string EdgeDarkReaderLookalikeId = "ooeaeegkhfeikelcapagcgeofffkjind"; // "Darth Reader Inc.", no website

    private const string IntuneSample = """
        ExtensionInstallForcelist:
        aapbdbdomjkkjkaonfhkkikfgjllcleb;https://clients2.google.com/service/update2/crx
        cnlefmmeadmemmdciolhbnfeacpdfbkd;https://edge.microsoft.com/extensionwebstorebase/v1/crx
        https://chromewebstore.google.com/detail/google-translate/aapbdbdomjkkjkaonfhkkikfgjllcleb
        "odfafepnkmbhccpbejgmiehpchacaeak","uBlock Origin"
        not-an-id-just-a-comment
        CJPALHDLNBPAFIAMEJDNHCPHJBKEIAGM
        """;

    public static async Task<int> RunAsync()
    {
        var log = new StringBuilder();
        var failures = 0;

        void Line(string text)
        {
            log.AppendLine(text);
            Console.WriteLine(text);
        }

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            Line($"[{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
        }

        Line($"Browser Extension Lookup self-test, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Line("--- Offline checks ---");

        Check("ID validation accepts valid", StoreClient.IsValidId(ChromeTranslateId), ChromeTranslateId);
        Check("ID validation rejects invalid", !StoreClient.IsValidId("notARealId123"), "notARealId123");

        var ex = IdExtractor.Extract(IntuneSample);
        var expected = new[] { ChromeTranslateId, EdgeGrammarlyId, EdgeUblockId, ChromeUblockId };
        Check("ID extraction from Intune/URL/CSV text",
            ex.Ids.SequenceEqual(expected) && ex.Duplicates == 1 && ex.LinesWithoutId == 2,
            $"ids=[{string.Join(", ", ex.Ids)}] duplicates={ex.Duplicates} linesWithoutId={ex.LinesWithoutId}");

        var page = """<script>AF_initDataCallback({key: 'ds:0', hash: '2', data:[["aapbdbdomjkkjkaonfhkkikfgjllcleb","icon","Google Translate",4.2,44848,"icon2","desc","http://translate.google.com/",1,1,1,["cat",null,6],1,1,38000000,1,"img",[1,2],"{\"manifest_version\": 3}","Google Translate"]], sideChannel: {}});</script>""";
        var items = ChromeData.FindItems(page);
        Check("Chrome data block parser", items.Count == 1 && items[0].Name == "Google Translate"
            && items[0].Users == 38000000 && items[0].ManifestVersion == 3,
            $"items={items.Count} name='{items.FirstOrDefault()?.Name}' users={items.FirstOrDefault()?.Users} mv={items.FirstOrDefault()?.ManifestVersion}");

        // Publisher website comparison used by cross-store matching
        var pk = StoreMatcher.PublisherKey;
        Check("Publisher website: scheme/www/slash ignored",
            pk("http://grammarly.com") == pk("https://www.grammarly.com/") && pk("www.adobe.com/in/about-adobe.html") == "adobe.com",
            $"{pk("http://grammarly.com")} / {pk("https://www.grammarly.com/")} / {pk("www.adobe.com/in/about-adobe.html")}");
        Check("Publisher website: shared hosts split by owner",
            pk("https://github.com/gorhill/uBlock") == pk("https://github.com/gorhill/uMatrix")
            && pk("https://github.com/alice/tool") != pk("https://github.com/bob/tool")
            && pk("https://chromewebstore.google.com/detail/x") is null && pk("") is null,
            $"{pk("https://github.com/gorhill/uBlock")} vs {pk("https://github.com/alice/tool")}");
        Check("Website search term",
            StoreMatcher.WebsiteStem("https://nordvpn.com/") == "nordvpn"
            && StoreMatcher.WebsiteStem("https://github.com/darkreader/darkreader") == "darkreader"
            && StoreMatcher.WebsiteStem("http://shop.example.co.uk") == "example",
            $"{StoreMatcher.WebsiteStem("https://nordvpn.com/")}, {StoreMatcher.WebsiteStem("https://github.com/darkreader/darkreader")}, {StoreMatcher.WebsiteStem("http://shop.example.co.uk")}");
        Check("Name comparison keeps non-Latin text",
            StoreMatcher.NormName("Salesforce 快速登录助手") != StoreMatcher.NormName("Salesforce")
            && StoreMatcher.NormName("Keeper® Password  Manager") == StoreMatcher.NormName("keeper password manager"),
            $"'{StoreMatcher.NormName("Salesforce 快速登录助手")}'");

        // Simulated store responses: errors must never look like "not found"
        await CheckFake("Chrome HTTP 503 -> Error", req => Respond(req, HttpStatusCode.ServiceUnavailable),
            c => c.LookupChromeAsync(ChromeTranslateId), LookupStatus.Error);
        await CheckFake("Chrome HTTP 429 -> Error", req => Respond(req, HttpStatusCode.TooManyRequests),
            c => c.LookupChromeAsync(ChromeTranslateId), LookupStatus.Error);
        await CheckFake("Chrome timeout -> Error", _ => throw new TaskCanceledException("simulated", new TimeoutException()),
            c => c.LookupChromeAsync(ChromeTranslateId), LookupStatus.Error);
        await CheckFake("Edge no connection -> Error", _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "simulated"),
            c => c.LookupEdgeAsync(EdgeGrammarlyId), LookupStatus.Error);
        await CheckFake("Edge HTTP 404 -> Not Found", req => Respond(req, HttpStatusCode.NotFound),
            c => c.LookupEdgeAsync(BogusId), LookupStatus.NotFound);
        await CheckFake("Chrome store status 7 -> Removed",
            req => Respond(req, HttpStatusCode.OK, "<script>AF_initDataCallback({key: 'ds:0',  data:[7],errorHasStatus: true,});</script>"),
            c => c.LookupChromeAsync(ChromeUblockId), LookupStatus.Removed);
        await CheckFake("Chrome store status 5 -> Not Found",
            req => Respond(req, HttpStatusCode.OK, "<script>AF_initDataCallback({key: 'ds:0',  data:[5],errorHasStatus: true,});</script>"),
            c => c.LookupChromeAsync(BogusId), LookupStatus.NotFound);

        var deadMatcher = new StoreMatcher(new StoreClient(new FakeHandler(
            _ => throw new HttpRequestException(HttpRequestError.ConnectionError, "simulated"))));
        var deadMatch = await deadMatcher.FindAsync(new LookupResult(ChromeNordVpnId, Store.Chrome, LookupStatus.Found,
            "VPN for Chrome: NordVPN proxy protection", "", Website: "https://nordvpn.com/"));
        Check("Match search with no connection -> Error, not No match",
            deadMatch.Confidence == MatchConfidence.Error, deadMatch.ConfidenceText);

        Line("--- Live checks (real stores) ---");
        var client = StoreClient.Instance;

        var chrome = await client.LookupChromeAsync(ChromeTranslateId);
        Check("Chrome lookup by ID", chrome.Found && chrome.Name.Contains("Translate", StringComparison.OrdinalIgnoreCase),
            $"{chrome.StatusText} Name='{chrome.Name}' Users={chrome.UsersText} Manifest={chrome.ManifestText}");
        Check("Chrome manifest version read", chrome.ManifestVersion == 3, $"Manifest={chrome.ManifestText}");

        var edge = await client.LookupEdgeAsync(EdgeGrammarlyId);
        Check("Edge lookup by ID", edge.Found && edge.Name.Contains("Grammarly", StringComparison.OrdinalIgnoreCase),
            $"{edge.StatusText} Name='{edge.Name}' Users={edge.UsersText} Manifest={edge.ManifestText}");

        var edgeUbo = await client.LookupEdgeAsync(EdgeUblockId);
        Check("Edge uBlock Origin flagged MV2", edgeUbo.Found && edgeUbo.ManifestVersion == 2,
            $"{edgeUbo.StatusText} Name='{edgeUbo.Name}' Manifest={edgeUbo.ManifestText}");

        var chromeRemoved = await client.LookupChromeAsync(ChromeUblockId);
        Check("Chrome removed ID is Removed/Not Found, never Error",
            chromeRemoved.Status is LookupStatus.Removed or LookupStatus.NotFound,
            $"{chromeRemoved.StatusText}");

        var chromeBogus = await client.LookupChromeAsync(BogusId);
        Check("Chrome bogus ID not found", chromeBogus.Status == LookupStatus.NotFound, chromeBogus.StatusText);

        var edgeBogus = await client.LookupEdgeAsync(BogusId);
        Check("Edge bogus ID not found", edgeBogus.Status == LookupStatus.NotFound, edgeBogus.StatusText);

        var chromeSearch = await client.SearchChromeAsync("nordvpn");
        var nord = chromeSearch.Results.FirstOrDefault(r => r.Name.Contains("NordVPN", StringComparison.Ordinal));
        Check("Chrome search returns real names (not URL slugs)",
            chromeSearch.Error is null && chromeSearch.Note is null && nord is not null && nord.Users > 0,
            $"{chromeSearch.Results.Count} result(s), error={chromeSearch.Error ?? "none"}, note={chromeSearch.Note ?? "none"}, " +
            $"match='{nord?.Name}' users={nord?.UsersText} rating={nord?.RatingText}");

        var edgeSearch = await client.SearchEdgeAsync("grammarly");
        Check("Edge search returns results", edgeSearch.Results.Count > 0,
            $"{edgeSearch.Results.Count} result(s), error={edgeSearch.Error ?? "none"}, first='{edgeSearch.Results.FirstOrDefault()?.Name}'");

        Line("--- Live cross-store matching ---");
        var matcher = StoreMatcher.Instance;

        var nordChrome = await client.LookupChromeAsync(ChromeNordVpnId);
        var nordMatch = nordChrome.Found ? await matcher.FindAsync(nordChrome) : null;
        Check("Chrome NordVPN -> Edge NordVPN (different names, same publisher)",
            nordMatch?.Confidence == MatchConfidence.SamePublisher && nordMatch.Id == EdgeNordVpnId,
            $"{nordMatch?.ConfidenceText ?? nordChrome.StatusText} '{nordMatch?.Name}' {nordMatch?.Id}");

        var grammarlyEdge = await client.LookupEdgeAsync(EdgeGrammarlyId);
        var grammarlyMatch = grammarlyEdge.Found ? await matcher.FindAsync(grammarlyEdge) : null;
        Check("Edge Grammarly -> Chrome Grammarly",
            grammarlyMatch?.Confidence == MatchConfidence.SamePublisher && grammarlyMatch.Id == ChromeGrammarlyId,
            $"{grammarlyMatch?.ConfidenceText ?? grammarlyEdge.StatusText} '{grammarlyMatch?.Name}' {grammarlyMatch?.Id}");

        var darkChrome = await client.LookupChromeAsync(ChromeDarkReaderId);
        var darkMatch = darkChrome.Found ? await matcher.FindAsync(darkChrome) : null;
        Check("Chrome Dark Reader is NOT matched to the Edge lookalike",
            darkMatch is not null && darkMatch.Confidence != MatchConfidence.Error && darkMatch.Id != EdgeDarkReaderLookalikeId,
            $"{darkMatch?.ConfidenceText ?? darkChrome.StatusText} '{darkMatch?.Name}' note={darkMatch?.Note ?? "none"}");

        var translateMatch = chrome.Found ? await matcher.FindAsync(chrome) : null;
        Check("Chrome-only Google Translate -> no Edge match",
            translateMatch?.Confidence == MatchConfidence.None,
            $"{translateMatch?.ConfidenceText} '{translateMatch?.Name}'");

        var verdict = failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED";
        Line(verdict);

        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "selftest-results.txt"), log.ToString());
        return failures == 0 ? 0 : 1;

        async Task CheckFake(string name, Func<HttpRequestMessage, HttpResponseMessage> respond,
            Func<StoreClient, Task<LookupResult>> lookup, LookupStatus want)
        {
            var result = await lookup(new StoreClient(new FakeHandler(respond)));
            Check(name, result.Status == want, result.StatusText);
        }
    }

    private static HttpResponseMessage Respond(HttpRequestMessage req, HttpStatusCode status, string body = "") =>
        new(status) { RequestMessage = req, Content = new StringContent(body) };

    /// <summary>Stands in for the network so failure handling can be tested offline.</summary>
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }
}
