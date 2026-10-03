# Browser Extension Lookup (v2.1)

A portable Windows app for looking up browser extension IDs from the Chrome Web Store
and Microsoft Edge Add-ons Store. Built for managing Intune browser extension
whitelist policies — find the ID, hit Copy, paste it into your policy. Done.

This is the WPF rewrite of the original PowerShell tool (which still lives in the
parent folder and still works fine). Same proven lookup logic, better everything else.

## What it does

Three tabs across the top:

- **Search by Name** — search both stores at once, results side by side, with user
  counts and ratings so you can tell the real extension from the knock-offs
- **Lookup by ID** — paste an extension ID, see its name, which store(s) it's in, how
  many users it has, and whether it's still Manifest V2
- **Bulk Lookup** — paste a pile of IDs and get them all resolved at once, then
  **Export CSV** if you want the results in a spreadsheet

Every result has a Copy button for the ID. Double-click a result row to open the
extension's store page in your browser.

## What's new in 2.1

- **Real Chrome names.** Chrome search used to rebuild names from the page's URLs, which
  Google cuts short ("Vpn For Chrome Nordvpn Pr"). Now it reads the store's own result
  data, so you get the full name ("VPN for Chrome: NordVPN proxy protection") plus
  users and rating.
- **Found / Not Found / Removed / Error.** A timeout or a store outage used to show up as
  "Not Found", which is a lie. Now:
  - **Not Found**: the store says that ID doesn't exist
  - **Removed**: Chrome used to list it but has taken it down (e.g. uBlock Origin after
    the Manifest V2 phase-out). Handy for policy clean-up.
  - **Error (timed out / rate limited / HTTP 503 / no connection)**: we couldn't get an
    answer. Run it again.
- **Manifest V2 flag.** Lookup, Bulk and the CSV show **MV2** / **MV3** for each store.
  MV2 extensions are on their way out (Chrome has already pulled them), so these are the
  ones to plan replacements for. "?" means the store didn't say.
- **Paste anything into Bulk.** Store URLs, Intune `id;https://clients2.google.com/...`
  lines, CSV, JSON, a mix of all of it. The app pulls out every ID, drops duplicates, and
  tells you how many lines had no ID in them.
- **No more doubled-up results** from hitting Enter several times while a search runs.
- **No more crash on Copy** when another app is hogging the clipboard. You get a warning
  in the status bar instead.
- Built on .NET 10 (the current long-term support release; .NET 8 support ends Nov 2026).

## Running it

Grab `BrowserExtensionLookup.exe` and double-click it. That's the whole install.
It's fully self-contained — nothing to install on the machine, works on any
Windows 10/11 x64 box, happy on a USB stick. The file is ~70 MB because the .NET
runtime is baked in; that's the price of "runs anywhere with no setup."

## Building from source

Needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`):

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

## Verifying it works

The exe has a built-in self-test:

```
BrowserExtensionLookup.exe --selftest
```

It runs offline checks first (ID extraction from Intune-style text, the Chrome data
parser, and fake store failures to prove errors never show as "Not Found"), then live
checks against real extensions: Google Translate on Chrome, Grammarly and uBlock Origin
(MV2) on Edge, uBlock Origin on Chrome (removed), a made-up ID in both stores, and a
search in each store.

Writes `selftest-results.txt` next to wherever you ran it and exits 0 if everything passed.

## Known quirks (inherited from how the stores work)

- **Chrome search only gives its top 10 matches.** That's all the Chrome search page
  hands over without running its own JavaScript. If what you want isn't in the 10, use
  "Open in browser" or look it up by ID. Lookup by ID is always reliable.
- **Edge search hides Manifest V2 extensions.** uBlock Origin on Edge never shows up in
  Edge search, even for "ublock origin", but Lookup by ID still finds it (and flags it
  MV2). If an Edge search comes back empty for something you know exists, that's
  probably why.
- **Edge search doesn't give user counts** (the API returns 0 for everything), so the
  Edge results grid shows ratings only. Lookup by ID gets the real number.
- Extension IDs are 32 characters, letters a–p only. The app validates this before
  making any requests.
- Feeding an extension ID to the Edge *search* API returns junk fuzzy matches, so if
  you paste an ID into Search by Name, the app notices and jumps you straight to a
  proper Lookup by ID instead.
- The Chrome store doesn't have a public API, so the app reads the data the store page
  embeds for its own use. If Google changes that page, Chrome search falls back to the
  old link-scraping (and says so in the status bar), and the self-test will flag it.
