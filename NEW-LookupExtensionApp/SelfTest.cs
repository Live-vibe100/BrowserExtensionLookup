using System.IO;
using System.Text;

namespace BrowserExtensionLookup;

/// <summary>
/// Headless verification (run with --selftest). Checks the store clients against
/// known-good extensions and prints PASS/FAIL per check. Exit code 0 = all passed.
/// </summary>
internal static class SelfTest
{
    // uBlock Origin's well-known store IDs
    private const string ChromeUblockId = "cjpalhdlnbpafiamejdnhcphjbkeiagm";
    private const string EdgeUblockId = "odfafepnkmbhccpbejgmiehpchacaeak";
    private const string BogusId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static async Task<int> RunAsync()
    {
        var log = new StringBuilder();
        var failures = 0;

        void Check(string name, bool ok, string detail)
        {
            if (!ok) failures++;
            var line = $"[{(ok ? "PASS" : "FAIL")}] {name}: {detail}";
            log.AppendLine(line);
            Console.WriteLine(line);
        }

        Check("ID validation accepts valid", StoreClient.IsValidId(ChromeUblockId), ChromeUblockId);
        Check("ID validation rejects invalid", !StoreClient.IsValidId("notARealId123"), "notARealId123");

        var client = StoreClient.Instance;

        var chrome = await client.LookupChromeAsync(ChromeUblockId);
        Check("Chrome lookup by ID", chrome.Found && chrome.Name.Contains("uBlock", StringComparison.OrdinalIgnoreCase),
            $"Found={chrome.Found} Name='{chrome.Name}'");

        var edge = await client.LookupEdgeAsync(EdgeUblockId);
        Check("Edge lookup by ID", edge.Found && edge.Name.Contains("uBlock", StringComparison.OrdinalIgnoreCase),
            $"Found={edge.Found} Name='{edge.Name}'");

        var chromeBogus = await client.LookupChromeAsync(BogusId);
        Check("Chrome bogus ID not found", !chromeBogus.Found, $"Found={chromeBogus.Found}");

        var edgeBogus = await client.LookupEdgeAsync(BogusId);
        Check("Edge bogus ID not found", !edgeBogus.Found, $"Found={edgeBogus.Found}");

        var chromeSearch = await client.SearchChromeAsync("ublock");
        Check("Chrome search returns results", chromeSearch.Results.Count > 0,
            $"{chromeSearch.Results.Count} result(s), error={chromeSearch.Error ?? "none"}, first='{chromeSearch.Results.FirstOrDefault()?.Name}'");

        var edgeSearch = await client.SearchEdgeAsync("ublock");
        Check("Edge search returns results", edgeSearch.Results.Count > 0,
            $"{edgeSearch.Results.Count} result(s), error={edgeSearch.Error ?? "none"}, first='{edgeSearch.Results.FirstOrDefault()?.Name}'");

        var verdict = failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED";
        log.AppendLine(verdict);
        Console.WriteLine(verdict);

        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "selftest-results.txt"), log.ToString());
        return failures == 0 ? 0 : 1;
    }
}
