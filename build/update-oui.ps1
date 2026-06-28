<#
  update-oui.ps1 - Refresh Resources\oui.txt with MAC vendor data.

  Tries full-coverage sources in order and keeps the largest result:
    1. Wireshark "manuf"  - MA-L + MA-M + MA-S (~54k), GitHub/Wireshark CDN, NOT bot-walled.
    2. IEEE registries    - MA-L + MA-M + MA-S, but sits behind an F5 bot wall that blocks many
                            networks (you may get 0 here - that's why manuf is tried first).
    3. Nmap mac-prefixes  - MA-L only (~35k), last-resort, always reachable on GitHub.

  Output: tab-separated  ASSIGNMENT<TAB>Vendor  with ASSIGNMENT as raw uppercase hex (6/7/9).
  OuiLookup normalises keys and matches longest-first (MA-S -> MA-M -> MA-L).

  SAFETY - this script will NEVER shrink your list. It counts the entries already in oui.txt and
  refuses to overwrite it with a smaller result, so a blocked or partial download can't downgrade
  or wipe your data. Compatible with PowerShell 5.1 and 7.
#>
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$out        = Join-Path $PSScriptRoot '..\Resources\oui.txt'
$ua         = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36'
$minHealthy = 1000   # below this a source is considered blocked/failed

$manufSource = 'https://www.wireshark.org/download/automated/data/manuf'
$ieeeSources = @(
    'https://standards-oui.ieee.org/oui/oui.csv',     # MA-L
    'https://standards-oui.ieee.org/oui28/mam.csv',   # MA-M
    'https://standards-oui.ieee.org/oui36/oui36.csv'  # MA-S
)
$nmapSource  = 'https://raw.githubusercontent.com/nmap/nmap/master/nmap-mac-prefixes'

function Get-Url($url) {
    # Download raw bytes (manuf is served gzip-compressed and PS 5.1 won't auto-decompress it),
    # then transparently gunzip if the gzip magic (1F 8B) is present. Works on PS 5.1 and 7.
    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        Invoke-WebRequest -Uri $url -UseBasicParsing -UserAgent $ua -OutFile $tmp
        $bytes = [System.IO.File]::ReadAllBytes($tmp)
    } catch {
        Write-Warning "  download failed: $($_.Exception.Message)"; return $null
    } finally {
        Remove-Item $tmp -ErrorAction SilentlyContinue
    }
    if (-not $bytes -or $bytes.Length -eq 0) { return $null }
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0x1f -and $bytes[1] -eq 0x8b) {
        try {
            $ms = New-Object System.IO.MemoryStream (,$bytes)
            $gz = New-Object System.IO.Compression.GZipStream($ms, [System.IO.Compression.CompressionMode]::Decompress)
            $sr = New-Object System.IO.StreamReader($gz, [System.Text.Encoding]::UTF8)
            $text = $sr.ReadToEnd()
            $sr.Close(); $gz.Close(); $ms.Close()
            return $text
        } catch {
            Write-Warning "  gzip decompress failed: $($_.Exception.Message)"; return $null
        }
    }
    return [System.Text.Encoding]::UTF8.GetString($bytes)
}

# Wireshark manuf: "<prefix[/mask]>`t<short>`t<long>"  (mask /28 = MA-M, /36 = MA-S, none = MA-L)
function Add-Manuf($content, $sb) {
    if (-not $content) { return 0 }
    $n = 0
    foreach ($line in ($content -split "`r?`n")) {
        if (-not $line -or $line[0] -eq '#') { continue }
        $f = $line -split "`t"
        if ($f.Count -lt 2) { continue }
        $prefix = $f[0].Trim()
        if (-not $prefix) { continue }
        $vendor = if ($f.Count -ge 3 -and $f[2].Trim()) { $f[2].Trim() } else { $f[1].Trim() }
        if (-not $vendor) { continue }
        $bits = 24
        if ($prefix -match '/(\d+)') { $bits = [int]$matches[1] }
        $nib = [int]($bits / 4)
        $hex = ($prefix -replace '/.*$','') -replace '[^0-9A-Fa-f]',''
        if ($hex.Length -lt $nib) { continue }
        [void]$sb.AppendLine("$($hex.Substring(0,$nib).ToUpper())`t$vendor")
        $n++
    }
    return $n
}

# IEEE registry CSV: Registry,Assignment,Organization Name,Organization Address
function Add-IeeeCsv($content, $sb) {
    if (-not $content) { return 0 }
    if ($content -notmatch '(?i)Assignment' -or $content -notmatch '(?i)Organization') {
        Write-Warning "  not CSV (IEEE likely blocked)."; return 0
    }
    $rows = $content | ConvertFrom-Csv
    if (-not $rows) { return 0 }
    $cols = $rows[0].PSObject.Properties.Name
    $aCol = $cols | Where-Object { $_ -match '(?i)assignment' } | Select-Object -First 1
    $oCol = $cols | Where-Object { $_ -match '(?i)organization' -and $_ -notmatch '(?i)address' } | Select-Object -First 1
    if (-not $aCol -or -not $oCol) { return 0 }
    $n = 0
    foreach ($row in $rows) {
        $a = "$($row.$aCol)".Trim().ToUpper(); $org = "$($row.$oCol)".Trim()
        if ($a.Length -ge 6 -and $org) { [void]$sb.AppendLine("$a`t$org"); $n++ }
    }
    return $n
}

# Nmap mac-prefixes: "<6 hex> <vendor>", comments start with '#'
function Add-NmapList($content, $sb) {
    if (-not $content) { return 0 }
    $n = 0
    foreach ($line in ($content -split "`r?`n")) {
        if ($line -match '^\s*#' -or -not $line.Trim()) { continue }
        if ($line -match '^\s*([0-9A-Fa-f]{6})\s+(.+?)\s*$') {
            [void]$sb.AppendLine("$($matches[1].ToUpper())`t$($matches[2])"); $n++
        }
    }
    return $n
}

# How many entries does the current oui.txt already have? We never write fewer than this.
$currentCount = 0
if (Test-Path $out) {
    try { $currentCount = ([System.IO.File]::ReadAllLines($out) | Where-Object { $_ -match "`t" }).Count } catch { }
}
Write-Host "Current oui.txt has $currentCount entries."

$ordered = @(
    @{ Name = 'Wireshark manuf (MA-L+MA-M+MA-S)'; Parser = 'manuf'; Urls = @($manufSource) },
    @{ Name = 'IEEE (MA-L+MA-M+MA-S)';            Parser = 'ieee';  Urls = $ieeeSources },
    @{ Name = 'Nmap mac-prefixes (MA-L)';          Parser = 'nmap';  Urls = @($nmapSource) }
)

$bestCount = 0; $bestName = ''; $bestData = $null
foreach ($src in $ordered) {
    $sb = [System.Text.StringBuilder]::new(); $n = 0
    foreach ($u in $src.Urls) {
        Write-Host "Downloading $u ..."
        switch ($src.Parser) {
            'manuf' { $n += Add-Manuf    (Get-Url $u) $sb }
            'ieee'  { $n += Add-IeeeCsv  (Get-Url $u) $sb }
            'nmap'  { $n += Add-NmapList (Get-Url $u) $sb }
        }
    }
    Write-Host "  $($src.Name): $n entries."
    if ($n -gt $bestCount) { $bestCount = $n; $bestName = $src.Name; $bestData = $sb.ToString() }
    if ($n -ge $minHealthy -and $n -ge $currentCount) { break }   # got a full-size list, stop early
}

if ($bestCount -lt $minHealthy) {
    Write-Error "All sources failed or were blocked (best was $bestCount entries). Your existing oui.txt is left unchanged."
    exit 1
}
if ($bestCount -lt $currentCount) {
    Write-Warning ("Best source '$bestName' returned $bestCount entries, fewer than your current " +
                   "$currentCount. Keeping your existing list - NOT downgrading.")
    exit 0
}

[System.IO.File]::WriteAllText($out, $bestData, (New-Object System.Text.UTF8Encoding($false)))
Write-Host "Wrote $bestCount OUI entries from $bestName to $out"
