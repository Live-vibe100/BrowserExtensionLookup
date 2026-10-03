# Browser Extension Lookup

A Windows tool for IT admins to look up browser extension IDs from the **Chrome Web
Store** and **Microsoft Edge Add-ons Store** — built for managing Intune browser
extension whitelist policies. Find the extension, copy its ID, paste it into your policy.

This repo holds two versions of the same tool:

| | Folder | What it is | Best for |
|---|---|---|---|
| **v2 (current)** | [`v2-app/`](v2-app/) | A modern WPF desktop app. Builds to a single portable `.exe` — no install, no dependencies, runs off a USB stick. | Everyone. This is the one to use. |
| **v1 (original)** | [`v1-powershell/`](v1-powershell/) | The original PowerShell/WinForms script. Kept for reference and for anyone who'd rather run a script than an exe. | No-exe environments, or reading how it started. |

Both do the same three jobs: **search by name**, **look up by ID**, and **bulk look up**
a list of IDs — across both stores at once.

## v2 — the app (recommended)

The portable exe is the easy path: grab `BrowserExtensionLookup.exe`, double-click, done.
It's fully self-contained (the .NET runtime is baked in), so it runs on any Windows 10/11
64-bit machine with nothing pre-installed.

The exe isn't checked into the repo (it's a build output). To get it, either build from
source or download it from a [Release](../../releases) if one's attached. Build details
and the full feature list are in [`v2-app/README.md`](v2-app/README.md).

Quick build (needs the .NET 10 SDK):

```
cd v2-app
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

## v1 — the PowerShell script

Double-click `v1-powershell/LAUNCH.bat`, or run the `.ps1` directly. No build step, no
exe — just PowerShell. See [`v1-powershell/`](v1-powershell/) for the script itself.

## Known quirks (both versions, and why)

These come from how the stores work, not from the tool:

- **Chrome has no search API.** Name search reads the Chrome search page, which only
  hands over the top 10 matches. Lookup by ID is always reliable, and an "Open in
  browser" link is there as the fallback. (v2.1 reads the page's own result data, so
  names are complete and come with user counts; v1 still rebuilds names from URLs,
  which Google cuts short.)
- **Edge search hides Manifest V2 extensions.** uBlock Origin on Edge never shows up in
  Edge search, but looking it up by ID still works. v2.1 flags MV2 extensions so you can
  plan replacements.
- **Extension IDs are 32 characters, letters a–p only.** Both versions validate this.

## Author

Joe Livesey
