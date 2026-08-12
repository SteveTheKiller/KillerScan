# release.ps1 - KillerScan release workflow
# Builds, signs (Certum via SimplySign, family convention), refreshes the landing page,
# tags, and publishes a GitHub release.
# Compatible with Windows PowerShell 5.1 and PowerShell 7.
#
# Usage:
#   .\release.ps1              # full release for the version in the csproj
#   .\release.ps1 -DryRun      # everything except the site push, tag push and gh release
#   .\release.ps1 -SkipSign    # local test build only - never release unsigned
#   .\release.ps1 -Choco       # also pack/push the Chocolatey package after the release
#
# winget is NOT submitted from here. .github/workflows/winget-release.yml fires on
# "release: published" and runs komac itself, so doing it here too would double-submit.
#
# The site is NOT deployed from here either. killerscan.net is a manual Cloudflare Pages
# drop, so this script rewrites scan-landing/ with the real release facts and commits it;
# you drag the folder into Cloudflare when you are ready. Committing before the tag is what
# makes the tag match what the site claims.

[CmdletBinding()]
param(
    [switch]$DryRun,
    # SHA1 thumbprint of the code-signing cert (40 hex chars). Preferred over CertName.
    [string]$CertThumbprint = "",
    # Fallback: CN match in the Windows cert store, as in the other Killer release scripts.
    [string]$CertName = "Open Source Developer Stephen Riley",
    [switch]$SkipSign,
    [switch]$Choco,
    [string]$ChocoApiKey = $env:CHOCO_API_KEY
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

function Fail([string]$Message) {
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

function Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

# Landing-page find/replace that refuses to silently do nothing. If a page's markup changes,
# a plain -replace leaves the stale value behind and the release can still appear successful.
# Release facts use this helper so a stale site becomes a hard preflight failure instead.
function Edit-SiteFact {
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Replacement,
        [Parameter(Mandatory)][string]$What
    )
    if ($Text -notmatch $Pattern) {
        Fail "Landing page: could not find $What. The markup changed - update its pattern in release.ps1."
    }
    return ($Text -replace $Pattern, $Replacement)
}

# Resolve the repo's default branch instead of hardcoding it, so the same script works across
# the Killer family. origin/HEAD is the best hint but it can go stale - it keeps naming a
# branch that was renamed away - so a candidate is only accepted if it still exists on the
# remote. Order: whatever origin/HEAD claims, then main, then master.
function Get-DefaultBranch {
    $remoteHeads = @(git ls-remote --heads origin 2>$null) |
        ForEach-Object { ($_ -split '\s+')[-1] -replace '^refs/heads/', '' }
    if (-not $remoteHeads) { return $null }

    $candidates = @()
    $originHead = git symbolic-ref --quiet refs/remotes/origin/HEAD 2>$null
    if ($originHead) { $candidates += (($originHead -replace '^refs/remotes/origin/', '').Trim()) }
    foreach ($c in @('main', 'master')) { if ($candidates -notcontains $c) { $candidates += $c } }

    foreach ($c in $candidates) {
        if ($c -and $remoteHeads -contains $c) { return $c }
    }
    return $null
}

# --- 1. Read version from the csproj (single source of truth) ---
Step "Reading version from KillerScan.csproj"
$csproj = Get-Content -Path 'KillerScan.csproj' -Raw
if ($csproj -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') {
    Fail 'No <Version>x.y.z</Version> found in KillerScan.csproj'
}
$Version = $Matches[1]
$Tag = "v$Version"
Write-Host "Version: $Version (tag $Tag)"

# --- 2. Preflight: clean tree, on the default branch, up to date, tag free ---
Step "Preflight checks"
$defaultBranch = Get-DefaultBranch
if (-not $defaultBranch) { Fail 'Could not determine the default branch from origin' }
Write-Host "Default branch: $defaultBranch"

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne $defaultBranch) { Fail "On branch '$branch', expected $defaultBranch" }

$dirty = git status --porcelain
if ($dirty) { Fail "Working tree is not clean. Commit or stash first:`n$($dirty -join "`n")" }

git fetch origin $defaultBranch --quiet
$local = (git rev-parse HEAD).Trim()
$remote = (git rev-parse "origin/$defaultBranch").Trim()
if ($local -ne $remote) { Fail "Local $defaultBranch and origin/$defaultBranch differ. Push or pull first." }

$existing = git tag --list $Tag
if ($existing) { Fail "Tag $Tag already exists" }

$remoteTag = git ls-remote --tags origin $Tag
if ($remoteTag) { Fail "Tag $Tag already exists on origin" }

# CHANGELOG must have a dated section for this version
$changelog = Get-Content -Path 'CHANGELOG.md' -Raw
if ($changelog -match [regex]::Escape("## [$Version] - Unreleased")) {
    Fail "CHANGELOG.md section [$Version] is still marked Unreleased"
}
if ($changelog -notmatch [regex]::Escape("## [$Version]")) {
    Fail "CHANGELOG.md has no [$Version] section"
}

# The About card shows <ReleaseDate> beside the version so users can tell how old their
# build is. It is a hand-edited csproj field, so it silently goes stale unless something
# checks it - that something is here. It must equal the date on this version's CHANGELOG
# section, which is the date the release actually goes out.
if ($csproj -notmatch '<ReleaseDate>([0-9]{4}-[0-9]{2}-[0-9]{2})</ReleaseDate>') {
    Fail 'No <ReleaseDate>yyyy-MM-dd</ReleaseDate> found in KillerScan.csproj'
}
$csprojReleaseDate = $Matches[1]
if ($changelog -notmatch ('## \[' + [regex]::Escape($Version) + '\] - ([0-9]{4}-[0-9]{2}-[0-9]{2})')) {
    Fail "CHANGELOG.md section [$Version] has no yyyy-MM-dd date"
}
$changelogDate = $Matches[1]
if ($csprojReleaseDate -ne $changelogDate) {
    Fail "csproj <ReleaseDate> is $csprojReleaseDate but CHANGELOG [$Version] is dated $changelogDate. Bump the csproj."
}
Write-Host "Release date: $csprojReleaseDate"

Write-Host 'Preflight OK'

# --- 3. Vulnerable package scan (required at every release) ---
Step "Scanning for vulnerable packages"
dotnet restore | Out-Null
$scan = dotnet list package --vulnerable --include-transitive 2>&1 | Out-String
Write-Host $scan
if ($scan -match 'has the following vulnerable packages') {
    Fail 'Vulnerable packages found. Resolve before releasing.'
}

# --- 4. Clean Release publish (FolderProfile1: net48, win-x64, Costura single exe) ---
Step "Building Release (publish)"
if (Test-Path 'bin\Release') { Remove-Item 'bin\Release' -Recurse -Force }

$msbuild = $null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vsWhere) {
    $vsPath = & $vsWhere -latest -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    if ($vsPath) {
        $candidate = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
        if (Test-Path $candidate) { $msbuild = $candidate }
    }
}
if ($msbuild) {
    & $msbuild 'KillerScan.csproj' /t:Publish /p:PublishProfile=FolderProfile1 /p:Configuration=Release /m /nologo /v:m
} else {
    & dotnet publish 'KillerScan.csproj' /p:PublishProfile=FolderProfile1 -c Release
}
if ($LASTEXITCODE -ne 0) { Fail 'Build failed' }

$publishDir = 'bin\Release\net48\publish'
$exe = Join-Path $publishDir 'KillerScan.exe'
if (-not (Test-Path $exe)) { Fail "Expected output not found: $exe" }

# Sanity check: built file version matches the csproj version
$fileVersion = (Get-Item $exe).VersionInfo.FileVersion
Write-Host "Built KillerScan.exe FileVersion $fileVersion"
if ($fileVersion -notlike "$Version*") {
    Fail "Built FileVersion $fileVersion does not match csproj version $Version"
}

# --- 5. Single-exe check ---
# Costura embeds every managed dependency, so the exe alone is the release asset
# (the site links to releases/latest/download/KillerScan.exe).
# NOTE: this is the PRE-signature size, used only for the Costura sanity check. The figure
# published on the landing page is recomputed after signing (step 7b), because Authenticode
# adds ~10KB and the site would otherwise advertise a size the downloaded file does not have.
Step "Verifying single-exe packaging"
$exeSize = (Get-Item $exe).Length
$unsignedMB = '{0:N2} MB' -f ($exeSize / 1MB)
if ($exeSize -lt 1.5MB) {
    Fail "KillerScan.exe is only $unsignedMB - Costura does not appear to have embedded the dependencies. Check Fody/FodyWeavers.xml."
}
Write-Host "KillerScan.exe is $unsignedMB (unsigned)"

# --- 6. Sign (Certum via SimplySign, same flow as the other Killer release scripts) ---
if ($SkipSign) {
    Write-Host ""
    Write-Host 'SkipSign: KillerScan.exe will be UNSIGNED - do not release this build' -ForegroundColor Red
} else {
    Step "Signing KillerScan.exe"
    $ssProc = Get-Process -Name 'SimplySignDesktop' -ErrorAction SilentlyContinue
    if (-not $ssProc) {
        Write-Warning 'SimplySign Desktop does not appear to be running.'
        Write-Host 'Start it and wait for Connected, then press Enter to continue (Ctrl+C aborts).'
        $null = Read-Host
    }

    # PATH first (covers shells where ProgramFiles(x86) is not in the environment), then the SDK kit dir.
    $signtool = (Get-Command signtool -ErrorAction SilentlyContinue).Source
    if (-not $signtool) {
        $kitBase = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
        if (-not (Test-Path $kitBase)) { $kitBase = 'C:\Program Files (x86)\Windows Kits\10\bin' }
        if (Test-Path $kitBase) {
            $signtool = Get-ChildItem "$kitBase\*\x64\signtool.exe" -Recurse -ErrorAction SilentlyContinue |
                        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
        }
    }
    if (-not $signtool) { Fail 'signtool.exe not found. Install the Windows SDK.' }
    Write-Host "signtool: $signtool"

    $certArgs = if ($CertThumbprint) { @('/sha1', $CertThumbprint) } else { @('/n', $CertName) }

    # TSA endpoints - tried in order; first success wins.
    $tsaList = @(
        'http://timestamp.digicert.com',
        'http://timestamp.sectigo.com',
        'http://ts.ssl.com'
    )
    $signedOk = $false
    foreach ($tsa in $tsaList) {
        Write-Host "Trying TSA: $tsa"
        & $signtool sign /fd sha256 /tr $tsa /td sha256 @certArgs /d 'KillerScan' /du 'https://killerscan.net' /v $exe
        if ($LASTEXITCODE -eq 0) { $signedOk = $true; break }
        Write-Warning "TSA $tsa failed (exit $LASTEXITCODE). Trying next..."
        Start-Sleep -Seconds 3
    }
    if (-not $signedOk) { Fail 'Signing failed on all TSA endpoints. Is SimplySign Desktop connected?' }

    # Post-sign gate: abort if the chain does not validate to a trusted root.
    & $signtool verify /pa /v $exe
    if ($LASTEXITCODE -ne 0) { Fail 'signtool verify FAILED - the signed exe does not pass trust validation. DO NOT RELEASE.' }
    Write-Host 'Signed, timestamped, and chain-verified' -ForegroundColor Green
}

# --- 7. Source bundle (GPL3 family convention) ---
# The Publish target already runs bundle-source.ps1, but call it again as a safety net:
# it never overwrites an existing bundle, so this is a no-op when the zip is already there.
Step "Bundling source"
& (Join-Path $PSScriptRoot 'build\bundle-source.ps1') -ProjectDir $PSScriptRoot -Version $Version -AppName 'KillerScan' -PublishDir $publishDir
$srcZip = Join-Path $publishDir "KillerScan-$Version-src.zip"
if (-not (Test-Path $srcZip)) { Fail "Source bundle not produced: $srcZip (is git installed and is this a repo?)" }
$srcZipMB = '{0:N2} MB' -f ((Get-Item $srcZip).Length / 1MB)
Write-Host "Source bundle: $srcZip ($srcZipMB)"

# --- 7b. Checksums (SHA256SUMS.txt) ---
# The in-app updater (Services/UpdateService.cs DownloadAsync) downloads this asset from the release,
# next to the exe, and verifies the download against it. WITHOUT it the Update button falls
# back to just opening the releases page. The updater matches the line starting with
# KillerScan.exe and takes the LAST whitespace token as the hash, so the padded columns
# below are fine. Upload it with the exe - gh release create does that in step 11.
Step "Writing SHA256SUMS.txt"
# Size and hash both come from the exe in its FINAL, signed state - this is the file people
# actually download, so it is what the landing page must describe.
$exeMB    = '{0:N2} MB' -f ((Get-Item $exe).Length / 1MB)
Write-Host "Signed KillerScan.exe is $exeMB"
$exeHash  = (Get-FileHash $exe -Algorithm SHA256).Hash
$srcHash  = (Get-FileHash $srcZip -Algorithm SHA256).Hash
$sumsFile = Join-Path $publishDir 'SHA256SUMS.txt'
$sumsLines = @(
    ('{0,-26} {1}' -f 'KillerScan.exe', $exeHash),
    ('{0,-26} {1}' -f (Split-Path $srcZip -Leaf), $srcHash)
)
Set-Content -Path $sumsFile -Value $sumsLines -Encoding ascii
Write-Host ($sumsLines -join "`n")

# --- 7c. Landing page + README release info ---
# killerscan.net is a MANUAL Cloudflare Pages drop, so nothing here deploys. The hero block
# (version, released, size, sha256), the verEgg footer on every page, and the README's GPL3
# source-zip link all carry release facts the script already knows, so they are rewritten and
# committed BEFORE the tag - the tag always matches what the site and README claim. Drag
# scan-landing/ into Cloudflare when you are ready.
# ReadAllText/WriteAllText keep the files BOM-less UTF-8 (PS 5.1 Set-Content -Encoding UTF8 adds a BOM).
Step "Updating scan-landing and README release info"
# One source of truth: preflight already proved this project date matches the CHANGELOG.
# Using today's clock date here could make the site disagree with the app when preparation
# and publication happen on different days.
$releaseDate = $csprojReleaseDate
$hashLower   = $exeHash.ToLower()
$siteDir     = Join-Path (Get-Location).Path 'scan-landing'

$indexPath = Join-Path $siteDir 'index.html'
$indexRaw  = [System.IO.File]::ReadAllText($indexPath)
$indexNew  = Edit-SiteFact $indexRaw '(<span class="k">version</span>&nbsp;<span class="v">)KillerScan v[0-9]+\.[0-9]+\.[0-9]+' ('${1}' + "KillerScan v$Version") 'the hero version'
$indexNew  = Edit-SiteFact $indexNew '(<span class="k">released</span>&nbsp;<span class="v">)[0-9]{4}-[0-9]{2}-[0-9]{2}' ('${1}' + $releaseDate) 'the hero released date'
$indexNew  = Edit-SiteFact $indexNew '(<span class="k">size</span>&nbsp;<span class="v">)[^<]*' ('${1}' + $exeMB + ' exe') 'the hero size row'
$indexNew  = Edit-SiteFact $indexNew '(<span class="v hash">)[0-9A-Fa-f]{32}<br>[0-9A-Fa-f]{32}' ('${1}' + $hashLower.Substring(0, 32) + '<br>' + $hashLower.Substring(32, 32)) 'the hero sha256 block'

# README: the GPL3 corresponding-source link must point at THIS release's zip.
$readmePath = Join-Path (Get-Location).Path 'README.md'
$readmeRaw  = [System.IO.File]::ReadAllText($readmePath)
$readmeNew  = Edit-SiteFact $readmeRaw '/releases/download/v[0-9]+\.[0-9]+\.[0-9]+/KillerScan-[0-9]+\.[0-9]+\.[0-9]+-src\.zip' "/releases/download/$Tag/KillerScan-$Version-src.zip" 'the README corresponding-source link'

# Validate every footer even during DryRun. That makes DryRun a real markup compatibility
# check without writing to the working tree.
$footerUpdates = @{}
foreach ($page in 'index.html', 'about.html', 'technical.html') {
    $p   = Join-Path $siteDir $page
    $raw = if ($page -eq 'index.html') { $indexNew } else { [System.IO.File]::ReadAllText($p) }
    $footerUpdates[$p] = Edit-SiteFact $raw '(id="verEgg"[^>]*>)v[0-9]+\.[0-9]+\.[0-9]+' ('${1}' + "v$Version") "the verEgg footer version in $page"
}

# DryRun must not touch the working tree. Writing here would leave the tree dirty, and the
# preflight on the NEXT (real) run would then fail on the very files this run modified.
if ($DryRun) {
    Write-Host "DryRun: would write these release facts and commit them:" -ForegroundColor Yellow
    Write-Host "  version  : KillerScan v$Version"
    Write-Host "  released : $releaseDate"
    Write-Host "  size     : $exeMB exe"
    Write-Host "  sha256   : $hashLower"
    Write-Host "  verEgg   : v$Version on index, about, technical"
    Write-Host "  README   : source zip link -> $Tag$(if ($readmeNew -eq $readmeRaw) { ' (already current)' })"
    Write-Host "DryRun: working tree left untouched." -ForegroundColor Yellow
} else {
    foreach ($p in $footerUpdates.Keys) {
        $raw = [System.IO.File]::ReadAllText($p)
        $new = $footerUpdates[$p]
        if ($new -ne $raw) { [System.IO.File]::WriteAllText($p, $new) }
    }

    if ($readmeNew -ne $readmeRaw) { [System.IO.File]::WriteAllText($readmePath, $readmeNew) }
}

# Claim checks for counts written by hand in prose. These are non-fatal documentation warnings,
# but they cover both number orders ("six themes" and "Themes - six") across the README, pages,
# and translated site copy, matching the newer KillerShell release workflow.
$numberWords = @{
    1 = 'one'; 2 = 'two'; 3 = 'three'; 4 = 'four'; 5 = 'five'; 6 = 'six'; 7 = 'seven'
    8 = 'eight'; 9 = 'nine'; 10 = 'ten'; 11 = 'eleven'; 12 = 'twelve'; 13 = 'thirteen'
    14 = 'fourteen'; 15 = 'fifteen'; 16 = 'sixteen'
}

function Test-CountClaim {
    param([string]$Label, [int]$Actual, [string]$Noun, [string[]]$Paths)

    $word = $numberWords[$Actual]
    foreach ($p in $Paths) {
        if (-not (Test-Path $p)) { continue }
        $text = [System.IO.File]::ReadAllText($p)
        $name = Split-Path $p -Leaf
        $num = '([0-9]+|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|thirteen|fourteen|fifteen|sixteen)'
        $patterns = @(
            "(?i)\b$num\s+(?:killer\s+)?$Noun\b",
            "(?i)\b$Noun\b\s*(?:</b>)?\s*[-:,：]\s*$num\b"
        )
        foreach ($pat in $patterns) {
            foreach ($m in [regex]::Matches($text, $pat)) {
                $said = $m.Groups[1].Value
                $ok = if ($said -match '^[0-9]+$') { [int]$said -eq $Actual } else { $said.ToLower() -eq $word }
                if (-not $ok) {
                    Write-Warning "$name claims '$($m.Value.Trim())' but the repo ships $Actual ($Label). Fix it before releasing."
                }
            }
        }
    }
}

$siteFiles = @('index.html', 'about.html', 'technical.html', 'ks-i18n.js') |
             ForEach-Object { Join-Path $siteDir $_ }
$docFiles = @($readmePath) + $siteFiles

$localeCount = (Get-ChildItem (Join-Path $PSScriptRoot 'Strings') -Filter '*.xaml' -ErrorAction SilentlyContinue).Count
if ($localeCount -gt 0) { Test-CountClaim 'Strings\*.xaml' $localeCount 'languages' $docFiles }

$themeCount = (Get-ChildItem (Join-Path $PSScriptRoot 'Themes') -Filter '*.xaml' -ErrorAction SilentlyContinue |
               Where-Object { $_.BaseName -ne 'Defaults' }).Count
if ($themeCount -gt 0) { Test-CountClaim 'Themes\*.xaml' $themeCount 'themes' $docFiles }

if ($DryRun) {
    Write-Host "DryRun: would commit and push scan-landing + README for v$Version"
} else {
    $siteDirty = git status --porcelain scan-landing README.md
    if ($siteDirty) {
        git add scan-landing README.md
        git commit -m "v${Version}: site and README release info" --quiet
        git push origin $defaultBranch --quiet
        if ($LASTEXITCODE -ne 0) { Fail 'Landing page / README commit failed to push' }
        Write-Host "scan-landing and README updated to v$Version and pushed"
        Write-Host 'Remember: killerscan.net does NOT auto-deploy. Drag scan-landing/ into Cloudflare Pages.' -ForegroundColor Yellow
    } else {
        Write-Host 'scan-landing and README already current'
    }
}

# --- 8. Release notes from the CHANGELOG section ---
Step "Extracting release notes from CHANGELOG.md"
$lines = Get-Content -Path 'CHANGELOG.md'
$notes = New-Object System.Collections.Generic.List[string]
$inSection = $false
foreach ($line in $lines) {
    if ($line -match "^## \[$([regex]::Escape($Version))\]") { $inSection = $true; continue }
    if ($inSection -and $line -match '^## \[') { break }
    if ($inSection) { $notes.Add($line) }
}
if ($notes.Count -eq 0) { Fail "Could not extract [$Version] notes from CHANGELOG.md" }
$notesFile = Join-Path $env:TEMP "KillerScan-$Version-notes.md"
$notes -join "`r`n" | Set-Content -Path $notesFile -Encoding UTF8
Write-Host "Notes written to $notesFile ($($notes.Count) lines)"

if ($DryRun) {
    Step "DryRun: stopping before tag and release"
    Write-Host "Would create tag $Tag, push it, and publish a release with:"
    Write-Host "  KillerScan.exe ($exeMB)"
    Write-Host "  $(Split-Path $srcZip -Leaf) ($srcZipMB)"
    Write-Host "  SHA256SUMS.txt"
    exit 0
}

# --- 9. Tag and push ---
Step "Tagging $Tag"
git tag -a $Tag -m "KillerScan $Tag"
git push origin $Tag
if ($LASTEXITCODE -ne 0) { Fail 'Tag push failed' }

# --- 10. GitHub release ---
# Publishing this release is also what fires .github/workflows/winget-release.yml, which
# submits to winget-pkgs via komac. Do NOT add a komac call here - it would double-submit.
Step "Creating GitHub release"
gh release create $Tag $exe $srcZip $sumsFile --title "KillerScan $Tag" --notes-file $notesFile --verify-tag
if ($LASTEXITCODE -ne 0) { Fail 'gh release create failed' }

# --- 11. Chocolatey pack/push (opt-in) ---
# Runs AFTER the release is published so the package never points at a release that failed.
# Non-fatal: the GitHub release is already out, so a choco hiccup must not fail the run.
if ($Choco) {
    Step "Packing Chocolatey package"
    $chocoDir   = Join-Path $PSScriptRoot 'choco'
    $nuspec     = Join-Path $chocoDir 'killerscan.nuspec'
    $installPs1 = Join-Path $chocoDir 'tools\chocolateyInstall.ps1'

    $nuspecOrig  = Get-Content $nuspec -Raw
    $installOrig = Get-Content $installPs1 -Raw
    $nupkg = $null
    try {
        $nuspecOrig  -replace 'REPLACE_VERSION', $Version | Set-Content $nuspec -NoNewline
        $installOrig -replace 'REPLACE_HASH',    $exeHash | Set-Content $installPs1 -NoNewline

        Push-Location $chocoDir
        choco pack killerscan.nuspec
        if ($LASTEXITCODE -ne 0) { Write-Warning 'choco pack failed' } else {
            $nupkg = Join-Path $chocoDir "killerscan.$Version.nupkg"
            Write-Host "Packed: $nupkg" -ForegroundColor Green
        }
        Pop-Location
    } finally {
        # Always restore the templates so the placeholders survive in git.
        $nuspecOrig  | Set-Content $nuspec -NoNewline
        $installOrig | Set-Content $installPs1 -NoNewline
    }

    if ($nupkg -and $ChocoApiKey) {
        Step "Pushing to the Chocolatey community repo"
        choco push $nupkg --source https://push.chocolatey.org --api-key $ChocoApiKey
        if ($LASTEXITCODE -ne 0) { Write-Warning 'choco push failed - push it by hand.' }
        else { Write-Host 'Pushed OK' -ForegroundColor Green }
    } elseif ($nupkg) {
        Write-Host 'Skipping push - set CHOCO_API_KEY or pass -ChocoApiKey to push automatically.' -ForegroundColor Yellow
    }
}

Step "Done"
Write-Host "Release $Tag published:"
gh release view $Tag --json url --jq '.url'
Write-Host ""
Write-Host "  winget: submitted automatically by .github/workflows/winget-release.yml" -ForegroundColor Yellow
Write-Host "  site  : scan-landing/ is committed and current - drag it into Cloudflare Pages to deploy." -ForegroundColor Yellow
