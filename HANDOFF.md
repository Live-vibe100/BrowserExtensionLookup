# HANDOFF — Browser Extension Lookup: search + reliability upgrades

**Written:** 2026-10-02 (review done from an ARCANA session; nothing has been changed in the code yet)
**Branch:** `feature/search-and-reliability-upgrades` (local only, nothing pushed)
**Scope:** `v2-app/` (the WPF app). Leave `v1-powershell/` alone; it's kept as a reference copy.

---

## 00. PHASE 2 STATUS (2026-10-03): cross-store matching DONE + verified on branch `feature/cross-store-matching`, committed locally, NOT pushed

- PR #1 merged (merge commit ed14c0b); v2.1 release promoted to Latest.
- New `StoreMatcher.cs`: finds an extension's listing in the other store. Searches by full name, brand and website name;
  verdict by **publisher website** (Same publisher / Name match, check it / No match / Error). Never matches on name similarity
  alone (live lookalike: "Dark Reader" on Edge by "Darth Reader Inc.", ooeaeegkhfeikelcapagcgeofffkjind).
  Publisher website is developer-declared, so most-users wins among same-site candidates and users are shown.
- Lookup tab: third "Edge match"/"Chrome match" row. Bulk: opt-in checkbox, Match columns + "Copy match" (only on matched rows), CSV Match columns.
- Version 2.2.0. Self-test 29/29. Matcher sweep over 30 real extensions: 18 same publisher, 6 name-only (all genuine; Chrome lists no website
  for them), 6 no match (all correct). GUI verified with screenshots.
- **Next:** Joe reviews; ask about push / PR / v2.2 release.

## 0. STATUS UPDATE (2026-10-02, build session): Phase 1 DONE + verified, committed locally, NOT pushed

- .NET 10 SDK 10.0.401 installed via winget (hash verified). Project retargeted to `net10.0-windows`, version 2.1.0.
- All of Phase 1 is built: B1–B4, real Chrome search data, MV2 flag (Chrome too: item `[18]` has the manifest JSON),
  Removed status, Users/Rating columns, paste-anything Bulk, self-test additions, READMEs.
- Extra findings made while building (all live-checked):
  - Chrome detail pages carry a status code in `AF_initDataCallback({key: 'ds:0', data:[N], errorHasStatus: true})`:
    **5 = never existed, 7 = removed** (checked with 2 of each: uBO + The Great Suspender vs two made-up IDs).
    A removed page and a made-up page are otherwise identical (both `empty-title` + `unsupported`).
  - Edge details API returns a clean **404** for a made-up ID.
  - Edge search results include `averageRating` + `noOfRatings`, so Edge search shows ratings (no user counts).
- Self-test: 21/21 pass on the published exe (11 offline incl. simulated 503/429/timeout/no-connection, 10 live).
- GUI verified by driving the real exe with UI Automation: nordvpn search, rapid 5x Enter on "zoom" (10/47 rows, no
  doubling), Edge uBO Found+MV2, Chrome uBO Removed, Intune paste in Bulk + CSV export, dead proxy -> Error (no connection),
  clipboard locked -> status warning, no crash.
- Follow-up (same day): CSV users now plain numbers; 28-query search sweep (scratchpad harness calling StoreClient) found no app bugs, 54/54 top-result lookups matched. Joe approved pushing the branch.
- PR opened: https://github.com/Live-vibe100/BrowserExtensionLookup/pull/1 (no CI configured). Release v2.1 created as a PRE-RELEASE pinned to 6600d4a with the exe (SHA256 EBB4C231...EDF57).
- **Next:** when Joe merges PR #1, flip v2.1 to a full/Latest release. Phase 2 (cross-store matching) after Joe says go. Phase 2 = cross-store matching only, after Joe says go.

## 1. What was done so far

- Read all of the v2 code (`StoreClient.cs`, the three views, `Models.cs`, `SelfTest.cs`, `App.xaml.cs`).
- Sent real requests to both stores (curl and Node) to find out **why searches miss extensions**. Everything below was observed live on 2026-10-02.
- **Nothing has been built or run yet.** This VM has no .NET 8 SDK, and no Python either (Node is available for quick probes).

## 2. What we learned from the live stores

### Chrome Web Store
- **Search returns exactly 10 results per page.** The page HTML includes a structured data block:
  `AF_initDataCallback({key: 'ds:1', hash: '..', data:[...], sideChannel: {}});`
  Each result inside it is an array shaped like
  `[id, iconUrl, NAME, rating, ratingCount, iconUrl2, shortDescription, website, ..., USERCOUNT at index 14, ...]`
  (seen at path `0.0.0.5.0.0.N.0.0`, but **don't hardcode the path**: walk the tree and match arrays whose `[0]` is a 32-char a–p ID and whose `[2]` is a string).
- When more results exist, a **continuation token** sits at `data[2][0]` (present for "zoom", missing for "nordvpn", which only had 3 results). The app ignores it today.
- **The current code builds names from the URL slug, and Google truncates slugs.** Example: NordVPN shows as "Vpn For Chrome Nordvpn Pr". The real name is "VPN for Chrome: NordVPN proxy protection".
- **uBlock Origin (`cjpalhdlnbpafiamejdnhcphjbkeiagm`) has been removed from the Chrome store** (the Manifest V2 removal). Its detail page redirects to `/detail/empty-title/<id>`, the og:title is just "Chrome Web Store", and the page contains an `unsupported` marker. A made-up ID returns a 301 instead (final destination not checked yet).
  → **This means `--selftest` is failing right now** on the "Chrome lookup by ID" check.

### Edge Add-ons
- Search API: `/addons/v4/getfilteredorderedsearch`. The response has keys `title, extensionList, totalExtensions, nextPageNo, hasMorePages, aggregations`. `activeInstallCount` comes back as **0** in search results, so real counts have to come from the details API.
- **Edge hides Manifest V2 extensions from search.** uBlock Origin on Edge (`odfafepnkmbhccpbejgmiehpchacaeak`) never shows up, even for "ublock origin". The details API still finds it fine and returns `isManifestV2: true` and `activeInstallCount: 13638822`.
- Details API: `/addons/getproductdetailsbycrxid/<id>`. Useful fields: `name, developer, activeInstallCount, isManifestV2, version, lastUpdateDate, averageRating, ratingCount, shortDescription, publisherWebsiteUri`.
- **The README's NordVPN note is out of date.** "nordvpn" now returns NordVPN as the first Edge result.

## 3. Bugs found in the code (fix all of these in Phase 1)

| # | Bug | Where | Fix |
|---|---|---|---|
| B1 | Self-test uses uBO on Chrome, which has been removed, so the test fails | `SelfTest.cs` | Pick stable, popular IDs and **check them live first** (e.g. Google Translate `aapbdbdomjkkjkaonfhkkikfgjllcleb` on Chrome, Grammarly `cnlefmmeadmemmdciolhbnfeacpdfbkd` on Edge). Keep uBO-on-Edge as the MV2 check. |
| B2 | Network errors, timeouts, 429s and 5xx responses all show as **"Not Found"** (the bare `catch {}` blocks, and `GetStringAsync` returning null for any non-2xx) | `StoreClient.cs` lookups | Add a three-way status: Found / NotFound / Error (+ reason). 404 or a "removed" page means NotFound; timeouts, 429 and 5xx mean Error. Show "Error (timed out)" in the grids and the CSV. |
| B3 | Pressing Enter starts a second search/lookup while one is already running. Both clear the grid, then both add results, so the results get mixed | `SearchView.QueryBox_KeyDown`, `LookupView.IdBox_KeyDown` | Add a busy guard (or cancel the previous run with a CancellationTokenSource) |
| B4 | `Clipboard.SetText` can throw a COMException (CLIPBRD_E_CANT_OPEN) when another app holds the clipboard; nothing catches it, so the app crashes | all three `CopyId_Click` | Wrap it in try/catch and show a warning in the status bar. Also add an `App.DispatcherUnhandledException` handler so nothing crashes the app silently. |

## 4. The plan

### Phase 1 — reliability + better results (do this first)
1. **B1–B4** above.
2. **Real Chrome search data:** parse the `ds:1` block in `SearchChromeAsync` to get the real name, user count, rating and website. Fall back to the current slug regex if the block isn't there (and say so in the status bar).
3. **Manifest V2 flag:** show an "MV2" marker (the extension is on its way out) in Lookup, Bulk and the CSV. Edge gets it from the details API (`isManifestV2`). **Chrome: still to check** whether the detail page's data block includes the manifest version; if it doesn't, show "?" rather than guessing.
4. **"Removed from store" status for Chrome:** if the redirect goes to `empty-title` and the page has the `unsupported` marker, show "Removed" instead of "Not Found". That's much more useful for policy cleanup. Check what a made-up ID does first.
5. **Add columns** to the search grids: Users and Rating (Chrome from `ds:1`; for Edge, either leave Users blank or fetch details only on demand. Don't call the details API for every search result).
6. **Paste-anything Bulk input:** pull every `[a-p]{32}` out of whatever is pasted (store URLs, Intune `id;https://clients2.google.com/service/update2/crx` lines, CSV, JSON), remove duplicates, and report how many lines had no ID in them.
7. **Self-test additions:** Chrome search returns real names (not slugs), Edge uBO comes back as MV2, ID extraction works on sample Intune text (offline check), and a removed ID is reported as Removed or NotFound, never Error.
8. Update both READMEs (Known quirks: drop the NordVPN line, add the Edge hides-MV2 explanation, the Chrome 10-per-page note, and the new features). Bump the version to **2.1.0**.

### Phase 2 — nice-to-haves (only after Phase 1 is verified and Joe says go)
- **Cross-store matching:** when a result is found in one store, search the other store by its exact name and show the match.
- **Chrome "load more":** follow the continuation token. This means copying the store page's own internal request (a `batchexecute` POST), which could break whenever Google changes it. **Off by default**, and if it fails, quietly fall back to the first 10 results with a note.

### Rules while building
- Store responses are **untrusted input**: parse them with JSON or regex only, never run anything from them, and only open `https://` URLs built from the two known store hosts (what `Util.OpenUrl` already does).
- No new NuGet packages. `System.Text.Json` and regex are enough.
- No silent failures (that's the whole point of B2).
- Keep the `UA` constants but bump them to a current Chrome/Edge version.

## 5. How to verify
1. Install the .NET 8 SDK (**needs Joe's OK first**; see Decisions). Official source: `winget install Microsoft.DotNet.SDK.8`.
2. `cd v2-app` → `dotnet build` → then the publish command from the README.
3. `publish\BrowserExtensionLookup.exe --selftest`: every check passes, and keep a copy of `selftest-results.txt` as proof.
4. Launch the app and check by hand: search "nordvpn" (Chrome shows the full real name and user count), search "zoom" (no mixed results when you hit Enter quickly several times), look up `odfafepnkmbhccpbejgmiehpchacaeak` (Edge Found + MV2), look up `cjpalhdlnbpafiamejdnhcphjbkeiagm` (Chrome Removed), paste an Intune-style policy block into Bulk, and export the CSV.
5. Error path: unplug the network or point at a dead proxy. Lookups should show **Error**, not Not Found.
6. Screenshot the results for Joe.

## 6. Decisions needing Joe
Answered by Joe on 2026-10-02:
- [x] SDK: install the **.NET 10 SDK** and **retarget the project to `net10.0-windows`** (.NET 8 support ends 2026-11-10).
- [x] **v1-powershell**: leave it untouched.
- [x] Phase 2: **cross-store matching only** (skip Chrome "load more"). Only after Phase 1 is verified and Joe says go.
- [x] GitHub: commit locally only; ask Joe about the push, PR and release **after he's reviewed** Phase 1.

---

## 7. Prompt for the next session

Start the new session with its working folder set to `C:\CLAUDE\Playground\BrowserExtensionLookup`, then paste:

> We're working on my Browser Extension Lookup app (this folder, branch `feature/search-and-reliability-upgrades`). A previous session reviewed the code and tested the live stores. Read `HANDOFF.md` first; it has the findings, the bugs, and the plan. Check the plan still makes sense against the code (don't re-do the whole review), then ask me the open questions in section 6 before you install anything or start building. Once I've said yes, do Phase 1, verify it as section 5 describes, and show me the self-test output and screenshots. Don't push anything to GitHub without asking me.
