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
