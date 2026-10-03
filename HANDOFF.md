# HANDOFF — Browser Extension Lookup

**Last updated:** 2026-10-03
**State:** v2.2.0 shipped. `main` is at `220f85f`, release **v2.2 is Latest** on GitHub. Nothing is in progress.
**Scope:** `v2-app/` (the WPF app). `v1-powershell/` is a reference copy; leave it alone (Joe's call).

---

## 1. What's been done

| Version | What | PR | Release |
|---|---|---|---|
| 2.1.0 | Reliability fixes + real Chrome data (Phase 1) | [#1](https://github.com/Live-vibe100/BrowserExtensionLookup/pull/1), merge `ed14c0b` | v2.1 (exe from `6600d4a`) |
| 2.2.0 | Cross-store matching (Phase 2) | [#2](https://github.com/Live-vibe100/BrowserExtensionLookup/pull/2), merge `220f85f` | **v2.2, Latest** (exe from `3aa6053`, SHA256 `2D1815F1…E41DA1`) |

**2.1.0**
- Lookups end as **Found / Not Found / Removed / Error (reason)**. Timeouts, 429s, 5xx and offline used to show as "Not Found".
- Chrome search reads the store page's embedded data: full names (not truncated URL slugs), users, rating.
- **MV2 flag** in Lookup, Bulk and CSV, for both stores. **Removed** status for Chrome listings that were taken down.
- Bulk accepts anything containing IDs (store URLs, Intune `id;update-url` lines, CSV, JSON), drops duplicates, counts lines with no ID.
- Fixed: repeated Enter mixed two result sets; Copy crashed when another app held the clipboard; self-test used uBO on Chrome (removed).
- Global unhandled-error handler. CSV user counts are plain numbers (Joe asked).
- Retargeted to **.NET 10** (.NET 8 support ends 2026-11-10).

**2.2.0**
- `StoreMatcher.cs`: when an ID is in only one store, finds the same extension in the other store (Chrome ID → Edge ID and back).
- Lookup tab: adds an "Edge match" / "Chrome match" row. Bulk: opt-in tick box, Match columns, **Copy match** button (only on matched rows), CSV Match columns.
- Verdicts: **Same publisher** (publisher websites match) / **Name match, check it** (identical name, one side lists no website) / **No match** (skipped lookalikes are named in the status bar) / **Error**.

## 2. What's been tested

- `--selftest`: **29/29 pass** on the released v2.2 exe. 16 offline (ID extraction, Chrome data parser, website comparison, simulated 503 / 429 / timeout / no-connection, match search with no connection → Error) + 13 live (lookups, removed/bogus IDs, both searches, NordVPN/Grammarly matching, Dark Reader lookalike rejected, Chrome-only Google Translate unmatched).
- 28-query search sweep across both stores: no app bugs; 54/54 top results looked up by ID matched the search name.
- Matcher sweep over 30 real extensions: 18 same publisher, 6 name-only (all genuine), 6 no match (all correct).
- GUI driven for real (see §5) with screenshots: searches, rapid Enter, MV2/Removed lookups, Intune paste + CSV, dead-proxy error path, locked clipboard, Lookup matches, Bulk matching, Copy match.

## 3. What's left / ideas (none started, none promised)

- **Chrome "load more":** follow Chrome's continuation token for results past the top 10. Means copying Google's internal `batchexecute` request, which could break any time. **Joe chose to skip this.** Don't build without asking.
- **Better confirmation for Chrome listings with no website.** LastPass, uBO Lite, React DevTools, Citrix and Redux DevTools all come back "Name match, check it" because Chrome's data shows no website for them. The Chrome data block may hold a verified-publisher field we haven't found. Unverified, needs a look at the raw item arrays.
- **Keep the GUI test driver in the repo.** The UI Automation scripts lived in a session scratchpad and are gone. A `tools/` folder would make re-verification cheap. Ask Joe first.
- **Optional cleanup:** the merged branches `feature/search-and-reliability-upgrades` and `feature/cross-store-matching` still exist on GitHub. Deleting them needs Joe's explicit yes.

## 4. Decisions needing Joe

- None open.

## 5. How to build and verify

- **Tools on the build VM:** .NET 10 SDK at `C:\Program Files\dotnet` (`export PATH="$PATH:/c/Program Files/dotnet"` in Bash). `gh` at `C:\Program Files\GitHub CLI`, logged in as Live-vibe100. Node is available for quick store probes; Python isn't.
- **Build:** in `v2-app/`, `dotnet build`, then the publish command from `v2-app/README.md` (single-file, self-contained, about 62 MB).
- **Self-test:** `publish\BrowserExtensionLookup.exe --selftest` → exit code 0 and `selftest-results.txt`.
- **GUI checks:** drive the real exe from **Windows PowerShell 5.1** with `UIAutomationClient`. Every control has an AutomationId from its `x:Name` (`QueryBox`, `SearchButton`, `IdBox`, `LookupButton`, `IdsBox`, `RunButton`, `ExportButton`, `MatchBox`, `ResultGrid`, `ChromeGrid`, `EdgeGrid`, `StatusText`, tabs `TabSearch`/`TabLookup`/`TabBulk`). Screenshot with `PrintWindow(hwnd, hdc, 2)`. Gotchas:
  - Treat a status ending in "..." as still working (the match step says "Found in Chrome. Looking for…").
  - The Save As dialog is a **child of the main window**, not a top-level window. Its file-name box isn't exposed to UIA, so use Alt+N, type the path, Enter.
  - Filter "Copy" buttons by ControlType Button (a text label inside the button also matches by name).
- **Error path:** launch with env `HTTPS_PROXY=http://127.0.0.1:9` (and `HTTP_PROXY`). .NET honours it, so every request fails → should show `Error (no connection)`.
- **Clipboard-locked path:** another process calls `OpenClipboard(0)` and holds it, then click Copy → status-bar warning, no crash.

## 6. How the stores actually work (checked live, 2026-10-02/03)

**Chrome Web Store** (no public API; the app reads data the page embeds for itself)
- Pages embed `AF_initDataCallback({key: 'ds:N', hash: '..', data:[...], sideChannel: {}});`. Find items by shape, not path: any array whose `[0]` is a 32-char a–p ID and `[2]` is a string. Same layout on search and detail pages: `[2]` name, `[3]` rating, `[4]` rating count, `[7]` website (often null), `[14]` users, `[18]` manifest JSON (contains `"manifest_version"`).
- Search gives **10 results only**. A continuation token sits at `data[2][0]` when there are more.
- Detail page for a missing ID: `AF_initDataCallback({key: 'ds:0', data:[N], errorHasStatus: true})`. **5 = never existed, 7 = removed.** Otherwise the two pages look identical (`/detail/empty-title/<id>`, og:title "Chrome Web Store").
- A removed listing's page still shows *recommendations* in `ds:1` (e.g. uBO Lite on uBO's page), so always match on the requested ID.

**Edge Add-ons**
- Search: `/addons/v4/getfilteredorderedsearch` (20 per page; the app reads up to 3 pages, 1 page when matching). `activeInstallCount` is always **0** here; `averageRating` and `noOfRatings` are real.
- **Edge search hides MV2 extensions** (uBO on Edge, `odfafepnkmbhccpbejgmiehpchacaeak`, never shows up), but the details API still returns them.
- Details: `/addons/getproductdetailsbycrxid/<id>`. Gives `name, developer, activeInstallCount, isManifestV2, publisherWebsiteUri, ...`. A made-up ID → clean **404**.
- Search quirks that aren't app bugs: "c++" returns unrelated results (Edge seems to drop the `++`); ranking can be odd ("lastpass" puts 1Password first).

**Cross-store matching facts**
- The same extension has **different IDs** in each store, and often different names (NordVPN: "VPN for Chrome: NordVPN proxy protection" vs "NordVPN - the Fastest VPN proxy for privacy").
- Lookalikes are real: Edge `ooeaeegkhfeikelcapagcgeofffkjind` is "Dark Reader - dark mode for Edge" by "Darth Reader Inc.", with no website. **Never match on name similarity alone.**
- The publisher website is **typed in by the developer, not verified.** That's why most-users wins among same-website candidates and user counts are always shown.

## 7. Rules and lessons (from working with Joe)

- Store responses are **untrusted input**: JSON/regex parsing only, never execute anything, only open `https://` URLs on the two store hosts.
- No new NuGet packages. No silent failures: anything that couldn't be checked shows **Error**, never a confident wrong answer.
- **Joe's merges have twice not reached GitHub** (PR stayed open; probably GitHub Desktop without "Push origin"). Always check `gh pr view N --json state` before touching releases. When asked, merge with a **merge commit** (not squash) so the release's commit stays in `main`'s history.
- Releases: build the exe from the exact pushed commit, put the SHA256 in the notes, and publish as a **pre-release until the PR is merged**, then promote to Latest (Joe agreed to this).
- Pushes, PRs, releases and merges each need Joe's yes. Deleting anything on GitHub needs his explicit yes.

## 8. Prompt for the next session

Start with the working folder set to `C:\CLAUDE\Playground\BrowserExtensionLookup`, then paste:

> We're working on my Browser Extension Lookup app (this folder). Read `HANDOFF.md` first: v2.2 is shipped and nothing's in progress. Pull the latest `main`, make a new feature branch for whatever I ask for, and don't push anything to GitHub without asking me.
