<#
.SYNOPSIS
    Browser Extension Lookup Tool - Intune Policy Helper

.DESCRIPTION
    A WinForms GUI tool for IT administrators to quickly look up browser extension
    IDs (GUIDs) from both the Chrome Web Store and Microsoft Edge Add-ons Store.
    
    Features:
      - Search by Name:  Search both stores simultaneously, side-by-side results
      - Lookup by ID:    Paste an extension ID to find its name and which store(s) it belongs to
      - Bulk Lookup:     Paste multiple IDs to resolve all at once
      - Copy buttons:    One-click copy of extension IDs for pasting into Intune policies
    
    Designed for managing Intune browser extension whitelist policies.

.AUTHOR
    Joe Livesey

.VERSION
    2.1

.NOTES
    - Chrome Web Store search is limited because the search page is JavaScript-rendered;
      only results in the initial SSR'd HTML are returned. Use Lookup by ID for guaranteed
      results, or click "Open in browser" to search the live store directly.
    - Edge Add-ons search uses the official API, but some brand-verified listings
      (e.g. NordPass, NordVPN) are absent from the API response even though they exist
      in the store. Use "Open in browser" as a fallback when expected results are missing.
    - Extension IDs are 32-character strings using only letters a-p.
#>

# ============================================================================
# PREREQUISITES
# ============================================================================
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

try {
    Add-Type -AssemblyName System.Web
    $script:CanDecodeHtml = $true
} catch {
    $script:CanDecodeHtml = $false
}

[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
[System.Windows.Forms.Application]::EnableVisualStyles()

# ============================================================================
# HELPER FUNCTIONS
# ============================================================================

function Decode-HtmlEntities {
    param([string]$Text)
    if ([string]::IsNullOrEmpty($Text)) { return $Text }
    
    if ($script:CanDecodeHtml) {
        try {
            return [System.Web.HttpUtility]::HtmlDecode($Text)
        } catch { }
    }
    
    # Manual fallback
    $Text = $Text -replace '&amp;',  '&'
    $Text = $Text -replace '&lt;',   '<'
    $Text = $Text -replace '&gt;',   '>'
    $Text = $Text -replace '&quot;', '"'
    $Text = $Text -replace '&#39;',  "'"
    $Text = $Text -replace '&apos;', "'"
    $Text = $Text -replace '&#x([0-9a-fA-F]+);', { [char][int]("0x" + $_.Groups[1].Value) }
    $Text = $Text -replace '&#(\d+);', { [char][int]$_.Groups[1].Value }
    return $Text
}

function Open-StoreSearch {
    <#
    .SYNOPSIS
        Opens the user's default browser to the live search page for the given store + query.
        Use as a fallback when the in-app API search misses results (e.g. brand-verified
        Edge listings or JS-only Chrome results).
    #>
    param(
        [ValidateSet('Chrome','Edge')] [string]$Store,
        [string]$Query
    )
    if ([string]::IsNullOrWhiteSpace($Query)) { return }
    $encoded = [System.Uri]::EscapeDataString($Query)
    $url = if ($Store -eq 'Chrome') {
        "https://chromewebstore.google.com/search/$encoded"
    } else {
        "https://microsoftedge.microsoft.com/addons/search/$encoded"
    }
    try { Start-Process $url } catch { }
}

function Get-ChromeExtensionById {
    <#
    .SYNOPSIS
        Looks up a Chrome extension by its 32-character ID.
    #>
    param([string]$ExtensionId)
    
    $result = [PSCustomObject]@{
        Name  = ""
        Id    = $ExtensionId
        Store = "Chrome"
        Found = $false
        Url   = "https://chromewebstore.google.com/detail/$ExtensionId"
    }
    
    try {
        $headers = @{
            "User-Agent"      = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36"
            "Accept"          = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
            "Accept-Language" = "en-US,en;q=0.9"
        }
        
        $response = Invoke-WebRequest -Uri $result.Url -UseBasicParsing -TimeoutSec 15 -Headers $headers -ErrorAction Stop
        $html = $response.Content
        
        # Try og:title meta tag first
        if ($html -match '<meta\s+property="og:title"\s+content="([^"]+)"') {
            $title = Decode-HtmlEntities $Matches[1]
        }
        # Fallback to <title> tag
        elseif ($html -match '<title[^>]*>([^<]+)</title>') {
            $title = Decode-HtmlEntities $Matches[1]
        }
        else {
            $title = $null
        }
        
        if ($title -and $title -notmatch "^Chrome Web Store$") {
            $name = $title -replace '\s*[-]+\s*Chrome Web Store\s*$', ''
            $result.Name  = $name.Trim()
            $result.Found = $true
        }
    }
    catch {
        $result.Found = $false
    }
    
    return $result
}

function Get-EdgeExtensionById {
    <#
    .SYNOPSIS
        Looks up an Edge extension by its 32-character ID.
        Uses the undocumented getproductdetailsbycrxid API first, falls back to page scraping.
    #>
    param([string]$ExtensionId)
    
    $result = [PSCustomObject]@{
        Name  = ""
        Id    = $ExtensionId
        Store = "Edge"
        Found = $false
        Url   = "https://microsoftedge.microsoft.com/addons/detail/$ExtensionId"
    }
    
    # Strategy 1: Use the Edge API (returns JSON with "name" property)
    try {
        $apiUrl = "https://microsoftedge.microsoft.com/addons/getproductdetailsbycrxid/$ExtensionId"
        $apiHeaders = @{
            "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0"
            "Accept"     = "application/json, text/plain, */*"
        }
        $apiResponse = Invoke-RestMethod -Uri $apiUrl -Headers $apiHeaders -TimeoutSec 15 -ErrorAction Stop
        
        if ($apiResponse -and $apiResponse.name) {
            $result.Name  = $apiResponse.name.Trim()
            $result.Found = $true
            return $result
        }
    }
    catch { }
    
    # Strategy 2: Fall back to page title scraping
    try {
        $headers = @{
            "User-Agent"      = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0"
            "Accept"          = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
            "Accept-Language" = "en-US,en;q=0.9"
        }
        
        $response = Invoke-WebRequest -Uri $result.Url -UseBasicParsing -TimeoutSec 15 -Headers $headers -ErrorAction Stop
        $html = $response.Content
        
        if ($html -match '<meta\s+property="og:title"\s+content="([^"]+)"') {
            $title = Decode-HtmlEntities $Matches[1]
        }
        elseif ($html -match '<title[^>]*>([^<]+)</title>') {
            $title = Decode-HtmlEntities $Matches[1]
        }
        else {
            $title = $null
        }
        
        if ($title -and $title -notmatch "^Microsoft Edge Add-ons$") {
            $name = $title -replace '\s*[-]+\s*Microsoft Edge Add-?ons\s*$', ''
            $result.Name  = $name.Trim()
            $result.Found = $true
        }
    }
    catch {
        $result.Found = $false
    }
    
    return $result
}

function Search-ChromeStore {
    <#
    .SYNOPSIS
        Searches the Chrome Web Store by extension name.
        Filters results to only show relevant matches.
    #>
    param([string]$Query)
    
    $results = [System.Collections.ArrayList]::new()
    
    try {
        $encodedQuery = [System.Uri]::EscapeDataString($Query)
        $url = "https://chromewebstore.google.com/search/$encodedQuery"
        
        $headers = @{
            "User-Agent"      = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36"
            "Accept"          = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"
            "Accept-Language" = "en-US,en;q=0.9"
        }
        
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 15 -Headers $headers -ErrorAction Stop
        $html = $response.Content
        
        # Parse extension links: /detail/extension-name-slug/abcdefghijklmnop...
        $regexMatches = [regex]::Matches($html, '/detail/([^/\"''?#]+)/([a-p]{32})')
        $seen = @{}
        
        foreach ($m in $regexMatches) {
            $slug = $m.Groups[1].Value
            $id   = $m.Groups[2].Value

            if ($seen.ContainsKey($id)) { continue }
            $seen[$id] = $true

            # URL-decode the slug so %C2%AE -> ®, %E2%80%94 -> em-dash, etc.
            $slugDecoded = [System.Uri]::UnescapeDataString($slug)
            $name = $slugDecoded -replace '-', ' '
            $name = (Get-Culture).TextInfo.ToTitleCase($name.ToLower())

            [void]$results.Add([PSCustomObject]@{
                Name  = $name.Trim()
                Id    = $id
                Store = "Chrome"
                Url   = "https://chromewebstore.google.com/detail/$slug/$id"
            })
        }
    }
    catch { }
    
    return $results
}

function Search-EdgeStore {
    <#
    .SYNOPSIS
        Searches the Microsoft Edge Add-ons Store using the official v4 API.
        Returns clean, structured results with extension name, ID, developer, and rating.
        Supports pagination for comprehensive results.
    #>
    param([string]$Query)
    
    $results = [System.Collections.ArrayList]::new()
    $seen = @{}
    $maxPages = 3
    $currentPage = 1
    $hasMore = $true
    
    try {
        $headers = @{
            "User-Agent"      = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36 Edg/125.0.0.0"
            "Accept"          = "application/json, text/plain, */*"
            "Accept-Language" = "en-US,en;q=0.9"
        }
        
        while ($hasMore -and $currentPage -le $maxPages) {
            $encodedQuery = [System.Uri]::EscapeDataString($Query)
            $apiUrl = "https://microsoftedge.microsoft.com/addons/v4/getfilteredorderedsearch?hl=en-US&gl=US&filteredCategories=Edge-Extensions&filteredAddon=0&filterFeaturedAddons=false&filteredRating=0&sortBy=Relevance&pgNo=$currentPage&Query=$encodedQuery"
            
            $response = Invoke-RestMethod -Uri $apiUrl -Headers $headers -TimeoutSec 15 -ErrorAction Stop
            
            if ($response -and $response.extensionList) {
                foreach ($ext in $response.extensionList) {
                    if ($seen.ContainsKey($ext.crxId)) { continue }
                    $seen[$ext.crxId] = $true

                    $extName = Decode-HtmlEntities $ext.name

                    [void]$results.Add([PSCustomObject]@{
                        Name      = $extName
                        Id        = $ext.crxId
                        Store     = "Edge"
                        Developer = (Decode-HtmlEntities $ext.developerName)
                        Rating    = $ext.averageRating
                        Ratings   = $ext.noOfRatings
                        Url       = "https://microsoftedge.microsoft.com/addons/detail/$($ext.crxId)"
                    })
                }
                
                # Check for more pages
                $hasMore = $response.hasMorePages -eq $true
                $currentPage++
            }
            else {
                $hasMore = $false
            }
        }
    }
    catch {
        # API failed - return empty results with a note
    }
    
    return $results
}

# ============================================================================
# COLOR THEME
# ============================================================================
$darkBg        = [System.Drawing.Color]::FromArgb(30, 30, 30)
$darkBgCtrl    = [System.Drawing.Color]::FromArgb(45, 45, 45)
$darkBgInput   = [System.Drawing.Color]::FromArgb(55, 55, 55)
$darkBgGrid    = [System.Drawing.Color]::FromArgb(40, 40, 40)
$darkBgGridAlt = [System.Drawing.Color]::FromArgb(50, 50, 50)
$gridLine      = [System.Drawing.Color]::FromArgb(60, 60, 60)
$accentBlue    = [System.Drawing.Color]::FromArgb(0, 120, 212)
$selectBlue    = [System.Drawing.Color]::FromArgb(0, 90, 158)
$textWhite     = [System.Drawing.Color]::White
$textLight     = [System.Drawing.Color]::FromArgb(220, 220, 220)
$chromeGold    = [System.Drawing.Color]::FromArgb(255, 200, 50)
$edgeBlue      = [System.Drawing.Color]::FromArgb(100, 180, 255)
$statusGreen   = [System.Drawing.Color]::FromArgb(80, 200, 80)
$statusYellow  = [System.Drawing.Color]::FromArgb(255, 200, 50)
$statusOrange  = [System.Drawing.Color]::FromArgb(255, 160, 50)

$fontMain   = New-Object System.Drawing.Font("Segoe UI", 9.5)
$fontLabel  = New-Object System.Drawing.Font("Segoe UI", 10, [System.Drawing.FontStyle]::Bold)
$fontTitle  = New-Object System.Drawing.Font("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
$fontMono   = New-Object System.Drawing.Font("Consolas", 9.5)
$fontStatus = New-Object System.Drawing.Font("Segoe UI", 9)
$fontBtn    = New-Object System.Drawing.Font("Segoe UI", 9.5, [System.Drawing.FontStyle]::Bold)

# ============================================================================
# HELPER: Configure a DataGridView with dark theme
# ============================================================================
function New-DarkDataGridView {
    $dgv = New-Object System.Windows.Forms.DataGridView
    $dgv.Dock = 'Fill'
    $dgv.BackgroundColor       = $darkBgGrid
    $dgv.GridColor             = $gridLine
    $dgv.BorderStyle           = 'None'
    $dgv.CellBorderStyle       = 'SingleHorizontal'
    $dgv.RowHeadersVisible     = $false
    $dgv.AllowUserToAddRows    = $false
    $dgv.AllowUserToDeleteRows = $false
    $dgv.AllowUserToResizeRows = $false
    $dgv.ReadOnly              = $true
    $dgv.SelectionMode         = 'FullRowSelect'
    $dgv.MultiSelect           = $false
    $dgv.AutoSizeColumnsMode   = 'Fill'
    $dgv.Font                  = $fontMain
    $dgv.RowTemplate.Height    = 38
    $dgv.EnableHeadersVisualStyles = $false
    
    # Header style
    $dgv.ColumnHeadersDefaultCellStyle.BackColor  = [System.Drawing.Color]::FromArgb(50, 50, 50)
    $dgv.ColumnHeadersDefaultCellStyle.ForeColor  = $textWhite
    $dgv.ColumnHeadersDefaultCellStyle.Font       = $fontLabel
    $dgv.ColumnHeadersDefaultCellStyle.Alignment  = 'MiddleLeft'
    $dgv.ColumnHeadersDefaultCellStyle.Padding    = New-Object System.Windows.Forms.Padding(5,0,0,0)
    $dgv.ColumnHeadersHeightSizeMode              = 'AutoSize'
    $dgv.ColumnHeadersBorderStyle                 = 'Single'
    
    # Cell style
    $dgv.DefaultCellStyle.BackColor          = $darkBgGrid
    $dgv.DefaultCellStyle.ForeColor          = $textLight
    $dgv.DefaultCellStyle.SelectionBackColor = $selectBlue
    $dgv.DefaultCellStyle.SelectionForeColor = $textWhite
    $dgv.DefaultCellStyle.Padding            = New-Object System.Windows.Forms.Padding(5,0,0,0)
    
    # Alternating row style
    $dgv.AlternatingRowsDefaultCellStyle.BackColor = $darkBgGridAlt
    
    return $dgv
}

# ============================================================================
# MAIN FORM
# ============================================================================
$form = New-Object System.Windows.Forms.Form
$form.Text            = "Browser Extension Lookup"
$form.Size            = New-Object System.Drawing.Size(1280, 820)
$form.MinimumSize     = New-Object System.Drawing.Size(1000, 720)
$form.StartPosition   = 'CenterScreen'
$form.BackColor       = $darkBg
$form.ForeColor       = $textWhite
$form.Font            = $fontMain
$form.FormBorderStyle = 'Sizable'

# Subtle additional colors used by the new visuals
$headerBg     = [System.Drawing.Color]::FromArgb(22, 22, 22)
$cardBg       = [System.Drawing.Color]::FromArgb(38, 38, 38)
$tabInactive  = [System.Drawing.Color]::FromArgb(150, 150, 150)
$subtleText   = [System.Drawing.Color]::FromArgb(140, 140, 140)

$fontHeader   = New-Object System.Drawing.Font("Segoe UI Semibold", 15)
$fontSub      = New-Object System.Drawing.Font("Segoe UI", 9)
$fontTabSel   = New-Object System.Drawing.Font("Segoe UI Semibold", 10)
$fontTabIdle  = New-Object System.Drawing.Font("Segoe UI", 10)

# ============================================================================
# STATUS BAR
# ============================================================================
$statusStrip = New-Object System.Windows.Forms.StatusStrip
$statusStrip.BackColor  = [System.Drawing.Color]::FromArgb(25, 25, 25)
$statusStrip.SizingGrip = $false

$statusLabel = New-Object System.Windows.Forms.ToolStripStatusLabel
$statusLabel.Text      = "Ready - Search by name or look up an extension ID"
$statusLabel.ForeColor = $statusGreen
$statusLabel.Font      = $fontStatus
$statusLabel.Spring    = $true
$statusLabel.TextAlign = 'MiddleLeft'
$statusStrip.Items.Add($statusLabel) | Out-Null
$form.Controls.Add($statusStrip)

# ============================================================================
# HEADER BAR (app title + version + accent underline)
# ============================================================================
$headerBar = New-Object System.Windows.Forms.Panel
$headerBar.Dock      = 'Top'
$headerBar.Height    = 56
$headerBar.BackColor = $headerBg
$headerBar.Padding   = New-Object System.Windows.Forms.Padding(24, 0, 24, 0)

$headerTitle = New-Object System.Windows.Forms.Label
$headerTitle.Text      = "Browser Extension Lookup"
$headerTitle.Font      = $fontHeader
$headerTitle.ForeColor = $textWhite
$headerTitle.Dock      = 'Fill'
$headerTitle.TextAlign = 'MiddleLeft'

$headerAccent = New-Object System.Windows.Forms.Panel
$headerAccent.Dock      = 'Bottom'
$headerAccent.Height    = 2
$headerAccent.BackColor = $accentBlue

$headerBar.Controls.Add($headerTitle)
$headerBar.Controls.Add($headerAccent)
$form.Controls.Add($headerBar)

function Set-Status {
    param([string]$Message, [string]$Color = "Green")
    switch ($Color) {
        "Green"  { $statusLabel.ForeColor = $statusGreen  }
        "Yellow" { $statusLabel.ForeColor = $statusYellow }
        "Orange" { $statusLabel.ForeColor = $statusOrange }
    }
    $statusLabel.Text = $Message
    [System.Windows.Forms.Application]::DoEvents()
}

# ============================================================================
# TAB CONTROL
# ============================================================================
$tabControl = New-Object System.Windows.Forms.TabControl
$tabControl.Dock        = 'Fill'
$tabControl.Font        = $fontMain
$tabControl.Appearance  = 'Normal'
$tabControl.DrawMode    = 'OwnerDrawFixed'
$tabControl.SizeMode    = 'Fixed'
$tabControl.ItemSize    = New-Object System.Drawing.Size(170, 36)
$tabControl.Padding     = New-Object System.Drawing.Point(0, 0)

# Custom-draw each tab: flat dark background, muted text for idle tabs, bold white + accent underline for selected.
$tabControl.Add_DrawItem({
    param($sender, $e)
    $g        = $e.Graphics
    $page     = $sender.TabPages[$e.Index]
    $rect     = $sender.GetTabRect($e.Index)
    $selected = ($e.Index -eq $sender.SelectedIndex)

    $bg = New-Object System.Drawing.SolidBrush($darkBg)
    $g.FillRectangle($bg, $rect)
    $bg.Dispose()

    if ($selected) {
        $accent = New-Object System.Drawing.SolidBrush($accentBlue)
        $underline = New-Object System.Drawing.Rectangle(($rect.X + 16), ($rect.Bottom - 3), ($rect.Width - 32), 3)
        $g.FillRectangle($accent, $underline)
        $accent.Dispose()
    }

    $fgColor = if ($selected) { $textWhite } else { $tabInactive }
    $font    = if ($selected) { $fontTabSel } else { $fontTabIdle }
    $fg      = New-Object System.Drawing.SolidBrush($fgColor)
    $sf      = New-Object System.Drawing.StringFormat
    $sf.Alignment     = 'Center'
    $sf.LineAlignment = 'Center'
    $g.DrawString($page.Text, $font, $fg, [System.Drawing.RectangleF]$rect, $sf)
    $fg.Dispose()
    $sf.Dispose()
})

# Force a full redraw whenever the active tab changes so the underline updates cleanly.
$tabControl.Add_SelectedIndexChanged({ $tabControl.Invalidate() })

# ============================================================================
# TAB 1: SEARCH BY NAME
# ============================================================================
$tabSearch = New-Object System.Windows.Forms.TabPage
$tabSearch.Text      = "Search by Name"
$tabSearch.BackColor = $darkBg
$tabSearch.Padding   = New-Object System.Windows.Forms.Padding(16, 14, 16, 14)

# --- Search panel ---
$searchPanel = New-Object System.Windows.Forms.Panel
$searchPanel.Dock      = 'Top'
$searchPanel.Height    = 55
$searchPanel.BackColor = $darkBg

$lblSearchName = New-Object System.Windows.Forms.Label
$lblSearchName.Text      = "Extension Name:"
$lblSearchName.Location  = New-Object System.Drawing.Point(5, 18)
$lblSearchName.Size      = New-Object System.Drawing.Size(140, 22)
$lblSearchName.ForeColor = $textWhite
$lblSearchName.Font      = $fontLabel
$searchPanel.Controls.Add($lblSearchName)

$txtSearchName = New-Object System.Windows.Forms.TextBox
$txtSearchName.Location    = New-Object System.Drawing.Point(150, 15)
$txtSearchName.Size        = New-Object System.Drawing.Size(495, 28)
$txtSearchName.BackColor   = $darkBgInput
$txtSearchName.ForeColor   = $textWhite
$txtSearchName.Font        = $fontMain
$txtSearchName.BorderStyle = 'FixedSingle'
$searchPanel.Controls.Add($txtSearchName)

$btnSearch = New-Object System.Windows.Forms.Button
$btnSearch.Text      = "Search Both Stores"
$btnSearch.Location  = New-Object System.Drawing.Point(660, 11)
$btnSearch.Size      = New-Object System.Drawing.Size(200, 34)
$btnSearch.BackColor = $accentBlue
$btnSearch.ForeColor = $textWhite
$btnSearch.Font      = $fontBtn
$btnSearch.FlatStyle = 'Flat'
$btnSearch.FlatAppearance.BorderSize = 0
$btnSearch.Cursor    = 'Hand'
$searchPanel.Controls.Add($btnSearch)
$tabSearch.Controls.Add($searchPanel)

# --- Split container for side-by-side results ---
$splitContainer = New-Object System.Windows.Forms.SplitContainer
$splitContainer.Dock             = 'Fill'
$splitContainer.Orientation      = 'Vertical'
$splitContainer.SplitterWidth    = 6
$splitContainer.BackColor        = $darkBg
$splitContainer.Panel1.BackColor = $darkBg
$splitContainer.Panel2.BackColor = $darkBg

# Add margin around the card so it visually "lifts" off the background.
$splitContainer.Panel1.Padding = New-Object System.Windows.Forms.Padding(4, 4, 0, 4)

# Chrome card (Fill inside Panel1)
$cardChrome = New-Object System.Windows.Forms.Panel
$cardChrome.Dock      = 'Fill'
$cardChrome.BackColor = $cardBg

# Top accent stripe (Chrome gold)
$stripChrome = New-Object System.Windows.Forms.Panel
$stripChrome.Dock      = 'Top'
$stripChrome.Height    = 4
$stripChrome.BackColor = $chromeGold

# Header row inside the card
$pnlChromeHeader = New-Object System.Windows.Forms.Panel
$pnlChromeHeader.Dock      = 'Top'
$pnlChromeHeader.Height    = 44
$pnlChromeHeader.BackColor = $cardBg
$pnlChromeHeader.Padding   = New-Object System.Windows.Forms.Padding(14, 0, 12, 0)

$lblChrome = New-Object System.Windows.Forms.Label
$lblChrome.Text      = "Chrome Web Store Results"
$lblChrome.Dock      = 'Left'
$lblChrome.Width     = 260
$lblChrome.ForeColor = $chromeGold
$lblChrome.Font      = $fontTitle
$lblChrome.TextAlign = 'MiddleLeft'

$linkChromeStore = New-Object System.Windows.Forms.LinkLabel
$linkChromeStore.Text             = "Open in browser ->"
$linkChromeStore.Dock             = 'Right'
$linkChromeStore.Width            = 170
$linkChromeStore.LinkColor        = $accentBlue
$linkChromeStore.ActiveLinkColor  = $chromeGold
$linkChromeStore.VisitedLinkColor = $accentBlue
$linkChromeStore.Font             = $fontMain
$linkChromeStore.TextAlign        = 'MiddleRight'
$linkChromeStore.Cursor           = 'Hand'

$pnlChromeHeader.Controls.Add($lblChrome)
$pnlChromeHeader.Controls.Add($linkChromeStore)

# Card assembled below after dgvChrome is constructed.

# Chrome DataGridView
$dgvChrome = New-DarkDataGridView

$colChromeName = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colChromeName.HeaderText = "Extension Name"
$colChromeName.Name       = "ExtName"
$colChromeName.FillWeight = 60

$colChromeId = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colChromeId.HeaderText = "Extension ID"
$colChromeId.Name       = "ExtId"
$colChromeId.FillWeight = 28
$colChromeId.DefaultCellStyle.Font = $fontMono

$colChromeCopy = New-Object System.Windows.Forms.DataGridViewButtonColumn
$colChromeCopy.HeaderText = ""
$colChromeCopy.Name       = "CopyBtn"
$colChromeCopy.Text       = "Copy"
$colChromeCopy.UseColumnTextForButtonValue = $true
$colChromeCopy.FillWeight    = 12
$colChromeCopy.FlatStyle     = 'Flat'
$colChromeCopy.MinimumWidth  = 65

# Add columns individually
$dgvChrome.Columns.Add($colChromeName) | Out-Null
$dgvChrome.Columns.Add($colChromeId) | Out-Null
$dgvChrome.Columns.Add($colChromeCopy) | Out-Null

# Assemble the Chrome card: dgv (Fill) first, then header (Top), then accent stripe (Top).
# Add order matters — last Dock=Top added sits at the top.
$cardChrome.Controls.Add($dgvChrome)
$cardChrome.Controls.Add($pnlChromeHeader)
$cardChrome.Controls.Add($stripChrome)
$splitContainer.Panel1.Controls.Add($cardChrome)

# Add margin around the card so it visually "lifts" off the background.
$splitContainer.Panel2.Padding = New-Object System.Windows.Forms.Padding(0, 4, 4, 4)

# Edge card (Fill inside Panel2)
$cardEdge = New-Object System.Windows.Forms.Panel
$cardEdge.Dock      = 'Fill'
$cardEdge.BackColor = $cardBg

# Top accent stripe (Edge blue)
$stripEdge = New-Object System.Windows.Forms.Panel
$stripEdge.Dock      = 'Top'
$stripEdge.Height    = 4
$stripEdge.BackColor = $edgeBlue

# Header row inside the card
$pnlEdgeHeader = New-Object System.Windows.Forms.Panel
$pnlEdgeHeader.Dock      = 'Top'
$pnlEdgeHeader.Height    = 44
$pnlEdgeHeader.BackColor = $cardBg
$pnlEdgeHeader.Padding   = New-Object System.Windows.Forms.Padding(14, 0, 12, 0)

$lblEdge = New-Object System.Windows.Forms.Label
$lblEdge.Text      = "Edge Add-ons Results"
$lblEdge.Dock      = 'Left'
$lblEdge.Width     = 260
$lblEdge.ForeColor = $edgeBlue
$lblEdge.Font      = $fontTitle
$lblEdge.TextAlign = 'MiddleLeft'

$linkEdgeStore = New-Object System.Windows.Forms.LinkLabel
$linkEdgeStore.Text             = "Open in browser ->"
$linkEdgeStore.Dock             = 'Right'
$linkEdgeStore.Width            = 170
$linkEdgeStore.LinkColor        = $accentBlue
$linkEdgeStore.ActiveLinkColor  = $edgeBlue
$linkEdgeStore.VisitedLinkColor = $accentBlue
$linkEdgeStore.Font             = $fontMain
$linkEdgeStore.TextAlign        = 'MiddleRight'
$linkEdgeStore.Cursor           = 'Hand'

$pnlEdgeHeader.Controls.Add($lblEdge)
$pnlEdgeHeader.Controls.Add($linkEdgeStore)

# Card assembled below after dgvEdge is constructed.

# Edge DataGridView - includes Developer column since the API provides it
$dgvEdge = New-DarkDataGridView

$colEdgeName = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colEdgeName.HeaderText = "Extension Name"
$colEdgeName.Name       = "ExtName"
$colEdgeName.FillWeight = 40

$colEdgeDev = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colEdgeDev.HeaderText = "Developer"
$colEdgeDev.Name       = "Developer"
$colEdgeDev.FillWeight = 18

$colEdgeId = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colEdgeId.HeaderText = "Extension ID"
$colEdgeId.Name       = "ExtId"
$colEdgeId.FillWeight = 28
$colEdgeId.DefaultCellStyle.Font = $fontMono

$colEdgeCopy = New-Object System.Windows.Forms.DataGridViewButtonColumn
$colEdgeCopy.HeaderText = ""
$colEdgeCopy.Name       = "CopyBtn"
$colEdgeCopy.Text       = "Copy"
$colEdgeCopy.UseColumnTextForButtonValue = $true
$colEdgeCopy.FillWeight    = 14
$colEdgeCopy.FlatStyle     = 'Flat'
$colEdgeCopy.MinimumWidth  = 65

# Add columns individually
$dgvEdge.Columns.Add($colEdgeName) | Out-Null
$dgvEdge.Columns.Add($colEdgeDev) | Out-Null
$dgvEdge.Columns.Add($colEdgeId) | Out-Null
$dgvEdge.Columns.Add($colEdgeCopy) | Out-Null

# Assemble the Edge card: dgv (Fill) first, then header (Top), then accent stripe (Top).
$cardEdge.Controls.Add($dgvEdge)
$cardEdge.Controls.Add($pnlEdgeHeader)
$cardEdge.Controls.Add($stripEdge)
$splitContainer.Panel2.Controls.Add($cardEdge)

$tabSearch.Controls.Add($splitContainer)
$splitContainer.BringToFront()

# --- Search event handlers ---
$dgvChrome.Add_CellContentClick({
    param($s, $e)
    if ($dgvChrome.Columns[$e.ColumnIndex].Name -eq "CopyBtn" -and $e.RowIndex -ge 0) {
        $id = $dgvChrome.Rows[$e.RowIndex].Cells["ExtId"].Value
        if ($id) {
            [System.Windows.Forms.Clipboard]::SetText($id)
            Set-Status "Copied Chrome ID: $id" "Green"
        }
    }
})

$dgvEdge.Add_CellContentClick({
    param($s, $e)
    if ($dgvEdge.Columns[$e.ColumnIndex].Name -eq "CopyBtn" -and $e.RowIndex -ge 0) {
        $id = $dgvEdge.Rows[$e.RowIndex].Cells["ExtId"].Value
        if ($id) {
            [System.Windows.Forms.Clipboard]::SetText($id)
            Set-Status "Copied Edge ID: $id" "Green"
        }
    }
})

# Fallback hyperlinks: open the live store search if the API misses something.
$linkChromeStore.Add_LinkClicked({
    $q = $txtSearchName.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($q)) {
        Set-Status "Enter an extension name first, then click 'Open in browser'." "Orange"
        return
    }
    Open-StoreSearch -Store 'Chrome' -Query $q
    Set-Status "Opened Chrome Web Store search for '$q' in your default browser." "Green"
})

$linkEdgeStore.Add_LinkClicked({
    $q = $txtSearchName.Text.Trim()
    if ([string]::IsNullOrWhiteSpace($q)) {
        Set-Status "Enter an extension name first, then click 'Open in browser'." "Orange"
        return
    }
    Open-StoreSearch -Store 'Edge' -Query $q
    Set-Status "Opened Edge Add-ons search for '$q' in your default browser." "Green"
})

$searchAction = {
    $query = $txtSearchName.Text.Trim()
    if ([string]::IsNullOrEmpty($query)) {
        Set-Status "Please enter an extension name to search." "Orange"
        return
    }
    
    $btnSearch.Enabled = $false
    $dgvChrome.Rows.Clear()
    $dgvEdge.Rows.Clear()
    
    # Search Chrome
    Set-Status "Searching Chrome Web Store for '$query'..." "Yellow"
    $chromeResults = @(Search-ChromeStore -Query $query)
    
    foreach ($ext in $chromeResults) {
        [void]$dgvChrome.Rows.Add($ext.Name, $ext.Id, "Copy")
    }
    
    # Search Edge using the official API
    Set-Status "Searching Edge Add-ons Store for '$query'..." "Yellow"
    $edgeResults = @(Search-EdgeStore -Query $query)
    
    foreach ($ext in $edgeResults) {
        [void]$dgvEdge.Rows.Add($ext.Name, $ext.Developer, $ext.Id, "Copy")
    }
    
    # Summary with empty-state guidance pointing at the fallback hyperlinks.
    $chromeCount = $chromeResults.Count
    $edgeCount   = $edgeResults.Count

    if ($chromeCount -eq 0 -and $edgeCount -eq 0) {
        Set-Status "No results from either store API for '$query'. Click 'Open in browser' on either side to search the live store directly." "Orange"
    }
    elseif ($chromeCount -eq 0) {
        Set-Status "Found $edgeCount Edge result(s). Chrome API returned nothing - click Chrome 'Open in browser' to search the live store." "Orange"
    }
    elseif ($edgeCount -eq 0) {
        Set-Status "Found $chromeCount Chrome result(s). Edge API returned nothing (brand-verified listings may be hidden) - click Edge 'Open in browser' to search the live store." "Orange"
    }
    else {
        Set-Status "Found $chromeCount Chrome result(s) and $edgeCount Edge result(s) for '$query'" "Green"
    }

    $btnSearch.Enabled = $true
}

$btnSearch.Add_Click($searchAction)
$txtSearchName.Add_KeyDown({
    param($s, $e)
    if ($e.KeyCode -eq 'Return') {
        $e.SuppressKeyPress = $true
        & $searchAction
    }
})

# ============================================================================
# TAB 2: LOOKUP BY ID
# ============================================================================
$tabLookup = New-Object System.Windows.Forms.TabPage
$tabLookup.Text      = "Lookup by ID"
$tabLookup.BackColor = $darkBg
$tabLookup.Padding   = New-Object System.Windows.Forms.Padding(16, 14, 16, 14)

# --- Lookup panel ---
$lookupPanel = New-Object System.Windows.Forms.Panel
$lookupPanel.Dock      = 'Top'
$lookupPanel.Height    = 55
$lookupPanel.BackColor = $darkBg

$lblLookupId = New-Object System.Windows.Forms.Label
$lblLookupId.Text      = "Extension ID:"
$lblLookupId.Location  = New-Object System.Drawing.Point(5, 18)
$lblLookupId.Size      = New-Object System.Drawing.Size(130, 22)
$lblLookupId.ForeColor = $textWhite
$lblLookupId.Font      = $fontLabel
$lookupPanel.Controls.Add($lblLookupId)

$txtLookupId = New-Object System.Windows.Forms.TextBox
$txtLookupId.Location    = New-Object System.Drawing.Point(140, 15)
$txtLookupId.Size        = New-Object System.Drawing.Size(505, 28)
$txtLookupId.BackColor   = $darkBgInput
$txtLookupId.ForeColor   = $textWhite
$txtLookupId.Font        = $fontMono
$txtLookupId.BorderStyle = 'FixedSingle'
$lookupPanel.Controls.Add($txtLookupId)

$btnLookup = New-Object System.Windows.Forms.Button
$btnLookup.Text      = "Lookup in Both Stores"
$btnLookup.Location  = New-Object System.Drawing.Point(660, 11)
$btnLookup.Size      = New-Object System.Drawing.Size(200, 34)
$btnLookup.BackColor = $accentBlue
$btnLookup.ForeColor = $textWhite
$btnLookup.Font      = $fontBtn
$btnLookup.FlatStyle = 'Flat'
$btnLookup.FlatAppearance.BorderSize = 0
$btnLookup.Cursor    = 'Hand'
$lookupPanel.Controls.Add($btnLookup)
$tabLookup.Controls.Add($lookupPanel)

# --- Lookup DataGridView ---
$dgvLookup = New-DarkDataGridView

$colLookupStore = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colLookupStore.HeaderText = "Store"
$colLookupStore.Name       = "Store"
$colLookupStore.FillWeight = 12

$colLookupStatus = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colLookupStatus.HeaderText = "Status"
$colLookupStatus.Name       = "Status"
$colLookupStatus.FillWeight = 10

$colLookupName = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colLookupName.HeaderText = "Extension Name"
$colLookupName.Name       = "ExtName"
$colLookupName.FillWeight = 43

$colLookupId = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colLookupId.HeaderText = "Extension ID"
$colLookupId.Name       = "ExtId"
$colLookupId.FillWeight = 25
$colLookupId.DefaultCellStyle.Font = $fontMono

$colLookupCopy = New-Object System.Windows.Forms.DataGridViewButtonColumn
$colLookupCopy.HeaderText = ""
$colLookupCopy.Name       = "CopyBtn"
$colLookupCopy.Text       = "Copy"
$colLookupCopy.UseColumnTextForButtonValue = $true
$colLookupCopy.FillWeight    = 10
$colLookupCopy.FlatStyle     = 'Flat'
$colLookupCopy.MinimumWidth  = 65

# Add columns individually
$dgvLookup.Columns.Add($colLookupStore) | Out-Null
$dgvLookup.Columns.Add($colLookupStatus) | Out-Null
$dgvLookup.Columns.Add($colLookupName) | Out-Null
$dgvLookup.Columns.Add($colLookupId) | Out-Null
$dgvLookup.Columns.Add($colLookupCopy) | Out-Null

# Dual gold|blue accent stripe sits between the input panel and the grid.
$stripLookup = New-Object System.Windows.Forms.Panel
$stripLookup.Dock      = 'Top'
$stripLookup.Height    = 4
$stripLookup.BackColor = $darkBg
$stripLookup.Add_Paint({
    param($sender, $e)
    $mid = [int]($sender.Width / 2)
    $lb = New-Object System.Drawing.SolidBrush($chromeGold)
    $rb = New-Object System.Drawing.SolidBrush($edgeBlue)
    $e.Graphics.FillRectangle($lb, 0, 0, $mid, $sender.Height)
    $e.Graphics.FillRectangle($rb, $mid, 0, ($sender.Width - $mid), $sender.Height)
    $lb.Dispose()
    $rb.Dispose()
})
$stripLookup.Add_Resize({ $this.Invalidate() })

# Add dgv (Fill) first, then stripe (Top) — last-added Top control sits at top edge
# of the remaining space (which is just below the input panel).
$tabLookup.Controls.Add($dgvLookup)
$tabLookup.Controls.Add($stripLookup)
$dgvLookup.BringToFront()
$stripLookup.BringToFront()

# --- Lookup event handlers ---
$dgvLookup.Add_CellContentClick({
    param($s, $e)
    if ($dgvLookup.Columns[$e.ColumnIndex].Name -eq "CopyBtn" -and $e.RowIndex -ge 0) {
        $id = $dgvLookup.Rows[$e.RowIndex].Cells["ExtId"].Value
        if ($id) {
            [System.Windows.Forms.Clipboard]::SetText($id)
            Set-Status "Copied ID: $id" "Green"
        }
    }
})

$dgvLookup.Add_CellFormatting({
    param($s, $e)
    if ($e.ColumnIndex -eq 1 -and $e.RowIndex -ge 0) {
        $val = $dgvLookup.Rows[$e.RowIndex].Cells["Status"].Value
        if ($val -eq "Found") {
            $e.CellStyle.ForeColor = $statusGreen
        } else {
            $e.CellStyle.ForeColor = [System.Drawing.Color]::FromArgb(255, 80, 80)
        }
    }
    if ($e.ColumnIndex -eq 0 -and $e.RowIndex -ge 0) {
        $val = $dgvLookup.Rows[$e.RowIndex].Cells["Store"].Value
        $e.CellStyle.Font = $fontLabel
        if ($val -match "Chrome") {
            $e.CellStyle.ForeColor = $chromeGold
        } elseif ($val -match "Edge") {
            $e.CellStyle.ForeColor = $edgeBlue
        }
    }
})

$lookupAction = {
    $id = $txtLookupId.Text.Trim()
    if ([string]::IsNullOrEmpty($id)) {
        Set-Status "Please enter an extension ID to look up." "Orange"
        return
    }
    
    if ($id -notmatch '^[a-p]{32}$') {
        Set-Status "Invalid extension ID format. Must be 32 characters using only letters a-p." "Orange"
        return
    }
    
    $btnLookup.Enabled = $false
    $dgvLookup.Rows.Clear()
    
    # Lookup Chrome
    Set-Status "Looking up ID in Chrome Web Store..." "Yellow"
    $chrome = Get-ChromeExtensionById -ExtensionId $id
    
    if ($chrome.Found) {
        [void]$dgvLookup.Rows.Add("Chrome", "Found", $chrome.Name, $chrome.Id, "Copy")
    } else {
        [void]$dgvLookup.Rows.Add("Chrome", "Not Found", "N/A", $id, "Copy")
    }
    
    # Lookup Edge
    Set-Status "Looking up ID in Edge Add-ons Store..." "Yellow"
    $edge = Get-EdgeExtensionById -ExtensionId $id
    
    if ($edge.Found) {
        [void]$dgvLookup.Rows.Add("Edge", "Found", $edge.Name, $edge.Id, "Copy")
    } else {
        [void]$dgvLookup.Rows.Add("Edge", "Not Found", "N/A", $id, "Copy")
    }
    
    # Summary
    $foundIn = @()
    if ($chrome.Found) { $foundIn += "Chrome (`"$($chrome.Name)`")" }
    if ($edge.Found)   { $foundIn += "Edge (`"$($edge.Name)`")" }
    
    if ($foundIn.Count -eq 0) {
        Set-Status "Extension ID '$id' was not found in either store." "Orange"
    } elseif ($foundIn.Count -eq 2) {
        Set-Status "Found in both stores: $($foundIn -join ' & ')" "Green"
    } else {
        Set-Status "Found in $($foundIn -join '')" "Green"
    }
    
    $btnLookup.Enabled = $true
}

$btnLookup.Add_Click($lookupAction)
$txtLookupId.Add_KeyDown({
    param($s, $e)
    if ($e.KeyCode -eq 'Return') {
        $e.SuppressKeyPress = $true
        & $lookupAction
    }
})

# ============================================================================
# TAB 3: BULK LOOKUP
# ============================================================================
$tabBulk = New-Object System.Windows.Forms.TabPage
$tabBulk.Text      = "Bulk Lookup"
$tabBulk.BackColor = $darkBg
$tabBulk.Padding   = New-Object System.Windows.Forms.Padding(16, 14, 16, 14)

# --- Bulk input panel ---
$bulkPanel = New-Object System.Windows.Forms.Panel
$bulkPanel.Dock      = 'Top'
$bulkPanel.Height    = 100
$bulkPanel.BackColor = $darkBg

$lblBulk = New-Object System.Windows.Forms.Label
$lblBulk.Text      = "Paste extension IDs (one per line):"
$lblBulk.Location  = New-Object System.Drawing.Point(5, 3)
$lblBulk.Size      = New-Object System.Drawing.Size(700, 20)
$lblBulk.ForeColor = $textLight
$lblBulk.Font      = $fontLabel
$bulkPanel.Controls.Add($lblBulk)

$txtBulkIds = New-Object System.Windows.Forms.TextBox
$txtBulkIds.Location    = New-Object System.Drawing.Point(5, 25)
$txtBulkIds.Size        = New-Object System.Drawing.Size(700, 70)
$txtBulkIds.Multiline   = $true
$txtBulkIds.ScrollBars  = 'Vertical'
$txtBulkIds.BackColor   = $darkBgInput
$txtBulkIds.ForeColor   = $textWhite
$txtBulkIds.Font        = $fontMono
$txtBulkIds.BorderStyle = 'FixedSingle'
$txtBulkIds.WordWrap    = $false
$bulkPanel.Controls.Add($txtBulkIds)

$btnBulk = New-Object System.Windows.Forms.Button
$btnBulk.Text      = "Lookup All"
$btnBulk.Location  = New-Object System.Drawing.Point(720, 25)
$btnBulk.Size      = New-Object System.Drawing.Size(140, 70)
$btnBulk.BackColor = $accentBlue
$btnBulk.ForeColor = $textWhite
$btnBulk.Font      = $fontBtn
$btnBulk.FlatStyle = 'Flat'
$btnBulk.FlatAppearance.BorderSize = 0
$btnBulk.Cursor    = 'Hand'
$bulkPanel.Controls.Add($btnBulk)
$tabBulk.Controls.Add($bulkPanel)

# --- Bulk DataGridView ---
$dgvBulk = New-DarkDataGridView

$colBulkId = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colBulkId.HeaderText = "Extension ID"
$colBulkId.Name       = "ExtId"
$colBulkId.FillWeight = 22
$colBulkId.DefaultCellStyle.Font = $fontMono

$colBulkChromeName = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colBulkChromeName.HeaderText = "Chrome Name"
$colBulkChromeName.Name       = "ChromeName"
$colBulkChromeName.FillWeight = 26

$colBulkChromeStatus = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colBulkChromeStatus.HeaderText = "Chrome"
$colBulkChromeStatus.Name       = "ChromeStatus"
$colBulkChromeStatus.FillWeight = 9

$colBulkEdgeName = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colBulkEdgeName.HeaderText = "Edge Name"
$colBulkEdgeName.Name       = "EdgeName"
$colBulkEdgeName.FillWeight = 26

$colBulkEdgeStatus = New-Object System.Windows.Forms.DataGridViewTextBoxColumn
$colBulkEdgeStatus.HeaderText = "Edge"
$colBulkEdgeStatus.Name       = "EdgeStatus"
$colBulkEdgeStatus.FillWeight = 9

$colBulkCopy = New-Object System.Windows.Forms.DataGridViewButtonColumn
$colBulkCopy.HeaderText = ""
$colBulkCopy.Name       = "CopyBtn"
$colBulkCopy.Text       = "Copy"
$colBulkCopy.UseColumnTextForButtonValue = $true
$colBulkCopy.FillWeight    = 8
$colBulkCopy.FlatStyle     = 'Flat'
$colBulkCopy.MinimumWidth  = 65

# Add columns individually
$dgvBulk.Columns.Add($colBulkId) | Out-Null
$dgvBulk.Columns.Add($colBulkChromeName) | Out-Null
$dgvBulk.Columns.Add($colBulkChromeStatus) | Out-Null
$dgvBulk.Columns.Add($colBulkEdgeName) | Out-Null
$dgvBulk.Columns.Add($colBulkEdgeStatus) | Out-Null
$dgvBulk.Columns.Add($colBulkCopy) | Out-Null

# Tint per-column headers by store: Chrome columns gold, Edge columns blue.
$colBulkChromeName.HeaderCell.Style.ForeColor   = $chromeGold
$colBulkChromeStatus.HeaderCell.Style.ForeColor = $chromeGold
$colBulkEdgeName.HeaderCell.Style.ForeColor     = $edgeBlue
$colBulkEdgeStatus.HeaderCell.Style.ForeColor   = $edgeBlue

# Dual gold|blue accent stripe sits between the input panel and the grid.
$stripBulk = New-Object System.Windows.Forms.Panel
$stripBulk.Dock      = 'Top'
$stripBulk.Height    = 4
$stripBulk.BackColor = $darkBg
$stripBulk.Add_Paint({
    param($sender, $e)
    $mid = [int]($sender.Width / 2)
    $lb = New-Object System.Drawing.SolidBrush($chromeGold)
    $rb = New-Object System.Drawing.SolidBrush($edgeBlue)
    $e.Graphics.FillRectangle($lb, 0, 0, $mid, $sender.Height)
    $e.Graphics.FillRectangle($rb, $mid, 0, ($sender.Width - $mid), $sender.Height)
    $lb.Dispose()
    $rb.Dispose()
})
$stripBulk.Add_Resize({ $this.Invalidate() })

$tabBulk.Controls.Add($dgvBulk)
$tabBulk.Controls.Add($stripBulk)
$dgvBulk.BringToFront()
$stripBulk.BringToFront()

# --- Bulk event handlers ---
$dgvBulk.Add_CellContentClick({
    param($s, $e)
    if ($dgvBulk.Columns[$e.ColumnIndex].Name -eq "CopyBtn" -and $e.RowIndex -ge 0) {
        $id = $dgvBulk.Rows[$e.RowIndex].Cells["ExtId"].Value
        if ($id) {
            [System.Windows.Forms.Clipboard]::SetText($id)
            Set-Status "Copied ID: $id" "Green"
        }
    }
})

$dgvBulk.Add_CellFormatting({
    param($s, $e)
    $colName = $dgvBulk.Columns[$e.ColumnIndex].Name
    if (($colName -eq "ChromeStatus" -or $colName -eq "EdgeStatus") -and $e.RowIndex -ge 0) {
        $val = $e.Value
        if ($val -eq "Found") {
            $e.CellStyle.ForeColor = $statusGreen
        } elseif ($val -eq "Not Found") {
            $e.CellStyle.ForeColor = [System.Drawing.Color]::FromArgb(255, 80, 80)
        } elseif ($val -match "Invalid") {
            $e.CellStyle.ForeColor = $statusOrange
        }
    }
})

$bulkAction = {
    $rawText = $txtBulkIds.Text.Trim()
    if ([string]::IsNullOrEmpty($rawText)) {
        Set-Status "Please paste one or more extension IDs (one per line)." "Orange"
        return
    }
    
    $lines = $rawText -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
    
    if ($lines.Count -eq 0) {
        Set-Status "No valid IDs found. Please enter extension IDs, one per line." "Orange"
        return
    }
    
    $btnBulk.Enabled = $false
    $dgvBulk.Rows.Clear()
    
    $total      = $lines.Count
    $current    = 0
    $foundCount = 0
    
    foreach ($id in $lines) {
        $current++
        
        if ($id -notmatch '^[a-p]{32}$') {
            [void]$dgvBulk.Rows.Add($id, "N/A", "Invalid", "N/A", "Invalid", "Copy")
            Set-Status "($current/$total) Skipped invalid ID: $id" "Orange"
            continue
        }
        
        Set-Status "($current/$total) Looking up $id..." "Yellow"
        
        # Chrome lookup
        $chrome = Get-ChromeExtensionById -ExtensionId $id
        $chromeName   = if ($chrome.Found) { $chrome.Name } else { "N/A" }
        $chromeStatus = if ($chrome.Found) { "Found" } else { "Not Found" }
        
        # Edge lookup
        $edge = Get-EdgeExtensionById -ExtensionId $id
        $edgeName   = if ($edge.Found) { $edge.Name } else { "N/A" }
        $edgeStatus = if ($edge.Found) { "Found" } else { "Not Found" }
        
        if ($chrome.Found -or $edge.Found) { $foundCount++ }
        
        [void]$dgvBulk.Rows.Add($id, $chromeName, $chromeStatus, $edgeName, $edgeStatus, "Copy")
    }
    
    Set-Status "Bulk lookup complete: $foundCount of $total extension(s) found in at least one store." "Green"
    $btnBulk.Enabled = $true
}

$btnBulk.Add_Click($bulkAction)

# ============================================================================
# ASSEMBLE TABS AND SHOW FORM
# ============================================================================
$tabControl.TabPages.Add($tabSearch) | Out-Null
$tabControl.TabPages.Add($tabLookup) | Out-Null
$tabControl.TabPages.Add($tabBulk) | Out-Null
$form.Controls.Add($tabControl)
$tabControl.BringToFront()

# Center the splitter and focus the search textbox on load
$form.Add_Shown({
    $splitContainer.SplitterDistance = [Math]::Floor($splitContainer.ClientSize.Width / 2)
    $txtSearchName.Focus()
})

[void]$form.ShowDialog()
$form.Dispose()
