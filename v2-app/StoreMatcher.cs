using System.Text.RegularExpressions;

namespace BrowserExtensionLookup;

/// <summary>
/// Finds an extension's listing in the other store (Chrome ID -> Edge ID and back).
///
/// Store search is full of lookalikes ("Darth Reader Inc." publishes a "Dark Reader" on Edge),
/// so a similar name is never treated as a match. A listing only counts as "Same publisher"
/// when both listings give the same publisher website. That website is self-declared by the
/// developer, so when several candidates claim it, the one with the most users wins, and the
/// user count is shown next to the match so a person can sanity-check it.
/// </summary>
public sealed class StoreMatcher
{
    public static StoreMatcher Instance { get; } = new(StoreClient.Instance);

    // Edge search results don't include the website, so each Edge candidate costs a details call.
    private const int MaxEdgeDetailChecks = 5;

    // Hosts many unrelated publishers share: the first path segment identifies the publisher.
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "gitlab.com", "bitbucket.org", "sites.google.com", "docs.google.com",
        "medium.com", "twitter.com", "x.com", "facebook.com", "linkedin.com", "youtube.com",
        "instagram.com", "t.me", "discord.gg", "discord.com", "linktr.ee", "patreon.com",
        "ko-fi.com", "buymeacoffee.com",
    };

    // Hosts that say nothing about who the publisher is.
    private static readonly HashSet<string> IgnoredHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "chromewebstore.google.com", "chrome.google.com", "microsoftedge.microsoft.com",
        "google.com", "microsoft.com", "example.com", "localhost",
    };

    private static readonly Regex BrandSplit = new(@"\s*(?::|\||\(|\s[-–—]\s)\s*", RegexOptions.Compiled);

    private readonly StoreClient _client;

    public StoreMatcher(StoreClient client) => _client = client;

    /// <summary>
    /// The result worth matching from: found in exactly one store, and definitely absent
    /// (Not Found / Removed) from the other. Null otherwise.
    /// </summary>
    public static LookupResult? SourceFor(LookupResult chrome, LookupResult edge)
    {
        static bool Absent(LookupResult r) => r.Status is LookupStatus.NotFound or LookupStatus.Removed;
        if (chrome.Found && Absent(edge)) return chrome;
        if (edge.Found && Absent(chrome)) return edge;
        return null;
    }

    /// <summary>Look for <paramref name="source"/> (a Found result) in the other store.</summary>
    public async Task<StoreMatch> FindAsync(LookupResult source, CancellationToken ct = default)
    {
        if (!source.Found) throw new ArgumentException("Only a found extension can be matched.", nameof(source));

        var target = source.Store == Store.Chrome ? Store.Edge : Store.Chrome;
        StoreMatch NoMatch(string? note = null) => new(target, MatchConfidence.None, "", "", "", Note: note);

        try
        {
            var (candidates, searchError) = await SearchCandidatesAsync(source, target, ct);
            if (candidates.Count == 0)
            {
                return searchError is null
                    ? NoMatch()
                    : new StoreMatch(target, MatchConfidence.Error, "", "", "", ErrorReason: searchError);
            }

            var checkedCandidates = await GetWebsitesAsync(candidates, target, ct);
            var sourceKey = PublisherKey(source.Website);

            // 1. Same publisher website. Most users wins if several claim it.
            if (sourceKey is not null)
            {
                var best = checkedCandidates
                    .Where(c => PublisherKey(c.Website) == sourceKey)
                    .OrderByDescending(c => c.Users ?? 0)
                    .FirstOrDefault();
                if (best is not null)
                    return ToMatch(target, best, MatchConfidence.SamePublisher);
            }

            // 2. Identical name. Only usable when one side doesn't list a website to compare.
            var sameName = checkedCandidates.FirstOrDefault(c => NormName(c.Name) == NormName(source.Name));
            if (sameName is not null)
            {
                var key = PublisherKey(sameName.Website);
                if (sourceKey is null || key is null)
                {
                    return ToMatch(target, sameName, MatchConfidence.NameOnly,
                        "Same name, but a publisher website is missing so it can't be confirmed.");
                }
                return NoMatch($"'{sameName.Name}' ({sameName.Id}) has the same name but a different publisher website " +
                               $"({key} vs {sourceKey}). Check it by hand.");
            }

            // 3. Similar-looking listing that failed the website check: say so rather than stay quiet.
            var lookalike = checkedCandidates.FirstOrDefault(c => Brand(c.Name) == Brand(source.Name));
            if (lookalike is not null)
            {
                var key = PublisherKey(lookalike.Website);
                return NoMatch(sourceKey is null
                    ? $"Skipped similar listing '{lookalike.Name}': this extension lists no publisher website to confirm it against."
                    : $"Skipped lookalike '{lookalike.Name}': " +
                      (key is null ? "it lists no publisher website" : $"its publisher website is {key}") +
                      $", but this extension's is {sourceKey}.");
            }

            return NoMatch(searchError is null ? null : $"Some searches failed ({searchError}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new StoreMatch(target, MatchConfidence.Error, "", "", "", ErrorReason: ex.Message);
        }
    }

    private sealed record Candidate(string Id, string Name, string Url, string? Website, long? Users);

    /// <summary>
    /// Search the other store by full name, brand ("NordVPN" from "NordVPN - fast VPN") and the
    /// website's name ("nordvpn" from nordvpn.com). Returns candidates best-first.
    /// </summary>
    private async Task<(List<SearchResult> Candidates, string? Error)> SearchCandidatesAsync(
        LookupResult source, Store target, CancellationToken ct)
    {
        var queries = new List<string> { source.Name };
        var brand = BrandText(source.Name);
        if (brand.Length >= 3 && NormName(brand) != NormName(source.Name)) queries.Add(brand);
        var stem = WebsiteStem(source.Website);
        var searchStem = stem is not null && !queries.Any(q => NormName(q) == stem);
        if (searchStem) queries.Add(stem!);

        var outcomes = await Task.WhenAll(queries.Select(q => target == Store.Edge
            ? _client.SearchEdgeAsync(q, ct, maxPages: 1)
            : _client.SearchChromeAsync(q, ct)));

        var error = outcomes.Select(o => o.Error).FirstOrDefault(e => e is not null);
        var stemResults = searchStem ? outcomes[^1].Results : new List<SearchResult>();
        var all = outcomes.SelectMany(o => o.Results).ToList();

        // Best-first: identical name, the top website-name hits, same brand (so lookalikes get checked
        // and reported), the remaining website-name hits, then search order.
        var ranked = all.Where(r => NormName(r.Name) == NormName(source.Name))
            .Concat(stemResults.Take(2))
            .Concat(all.Where(r => Brand(r.Name) == Brand(source.Name)))
            .Concat(stemResults)
            .Concat(all)
            .DistinctBy(r => r.Id)
            .ToList();
        return (ranked, error);
    }

    /// <summary>Chrome results already carry the website; Edge needs a details lookup per candidate.</summary>
    private async Task<List<Candidate>> GetWebsitesAsync(List<SearchResult> candidates, Store target, CancellationToken ct)
    {
        if (target == Store.Chrome)
            return candidates.Select(c => new Candidate(c.Id, c.Name, c.Url, c.Website, c.Users)).ToList();

        var details = await Task.WhenAll(candidates.Take(MaxEdgeDetailChecks)
            .Select(c => _client.LookupEdgeAsync(c.Id, ct)));
        return details.Where(d => d.Found)
            .Select(d => new Candidate(d.Id, d.Name, d.Url, d.Website, d.Users))
            .ToList();
    }

    private static StoreMatch ToMatch(Store target, Candidate c, MatchConfidence confidence, string? note = null) =>
        new(target, confidence, c.Name, c.Id, c.Url, c.Users, note);

    /// <summary>
    /// Normalise a publisher website for comparison: lower-case host without "www.", ignoring
    /// scheme, path and trailing slash. Shared hosts (github.com etc.) keep the first path segment.
    /// Returns null when the website is missing or says nothing about the publisher.
    /// </summary>
    internal static string? PublisherKey(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        if (host.Length == 0 || IgnoredHosts.Contains(host)) return null;

        if (SharedHosts.Contains(host))
        {
            var first = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return first is null ? null : host + "/" + first.ToLowerInvariant();
        }
        return host;
    }

    /// <summary>A search term from the website: "nordvpn" for nordvpn.com, "darkreader" for github.com/darkreader.</summary>
    internal static string? WebsiteStem(string? url)
    {
        var key = PublisherKey(url);
        if (key is null) return null;

        string stem;
        if (key.Contains('/'))
        {
            stem = key[(key.IndexOf('/') + 1)..];
        }
        else
        {
            var labels = key.Split('.');
            if (labels.Length < 2) return null;
            // "example.co.uk" -> "example"; "app.grammarly.com" -> "grammarly"
            var i = labels.Length >= 3 && labels[^1].Length == 2 && labels[^2] is "co" or "com" or "org" or "net" or "ac" or "gov"
                ? labels.Length - 3
                : labels.Length - 2;
            stem = labels[i];
        }
        return stem.Length >= 3 ? stem : null;
    }

    /// <summary>Case/whitespace-insensitive name, trademark symbols dropped. Keeps every script (Chinese, Cyrillic...).</summary>
    internal static string NormName(string name) =>
        Regex.Replace(name.Replace("®", "").Replace("™", "").Replace("©", ""), @"\s+", " ").Trim().ToLowerInvariant();

    /// <summary>The part of a name before ":", " - ", "|" or "(": "Grammarly: AI Writing..." -> "Grammarly".</summary>
    private static string BrandText(string name) => BrandSplit.Split(name, 2)[0].Trim();

    private static string Brand(string name) => NormName(BrandText(name));
}
