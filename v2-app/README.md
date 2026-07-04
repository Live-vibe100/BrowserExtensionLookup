# Browser Extension Lookup (v3.0)

A portable Windows app for looking up browser extension IDs from the Chrome Web Store
and Microsoft Edge Add-ons Store. Built for managing Intune browser extension
whitelist policies — find the ID, hit Copy, paste it into your policy. Done.

This is the WPF rewrite of the original PowerShell tool (which still lives in the
parent folder and still works fine). Same proven lookup logic, better everything else.

## What it does

Three tabs across the top:

- **Search by Name** — search both stores at once, results side by side
- **Lookup by ID** — paste an extension ID, see its name and which store(s) it's in
- **Bulk Lookup** — paste a pile of IDs (one per line), get them all resolved at once,
  then **Export CSV** if you want the results in a spreadsheet

Every result has a Copy button for the ID. Double-click a result row to open the
extension's store page in your browser.

## What's new over the PowerShell version

- **It's fast.** Lookups run in parallel — a 20-ID bulk job takes seconds, not minutes,
  and the window never freezes while it works.
- **Progress bar + Cancel** on bulk jobs.
- **CSV export** for bulk results.
- Proper modern dark UI (dark title bar included).

## Running it

Grab `BrowserExtensionLookup.exe` and double-click it. That's the whole install.
It's fully self-contained — nothing to install on the machine, works on any
Windows 10/11 x64 box, happy on a USB stick. The file is ~70 MB because the .NET
runtime is baked in; that's the price of "runs anywhere with no setup."

## Building from source

Needs the .NET 8 SDK:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

## Verifying it works

The exe has a built-in self-test that checks real lookups against known extensions
(uBlock Origin in both stores, plus negative and validation cases):

```
BrowserExtensionLookup.exe --selftest
```

Writes `selftest-results.txt` next to wherever you ran it and exits 0 if everything passed.

## Known quirks (inherited from how the stores work)

- Chrome Web Store has no search API, so name search scrapes the search page and only
  sees what's in the initial HTML. Lookup by ID is always reliable; the
  "Open in browser" link is the fallback for search.
- The Edge search API hides some brand-verified listings (NordVPN and friends).
  Same fallback: "Open in browser."
- Extension IDs are 32 characters, letters a–p only. The app validates this before
  making any requests.
- Feeding an extension ID to the Edge *search* API returns junk fuzzy matches, so if
  you paste an ID into Search by Name, the app notices and jumps you straight to a
  proper Lookup by ID instead.
