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

# --- 1. Read version from the csproj (single source of truth) ---
Step "Reading version from KillerScan.csproj"
$csproj = Get-Content -Path 'KillerScan.csproj' -Raw
if ($csproj -notmatch '<Version>([0-9]+\.[0-9]+\.[0-9]+)</Version>') {
    Fail 'No <Version>x.y.z</Version> found in KillerScan.csproj'
}
$Version = $Matches[1]
$Tag = "v$Version"
Write-Host "Version: $Version (tag $Tag)"

# --- 2. Preflight: clean tree, on main, up to date, tag free ---
Step "Preflight checks"
$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { Fail "On branch '$branch', expected main" }

$dirty = git status --porcelain
if ($dirty) { Fail "Working tree is not clean. Commit or stash first:`n$($dirty -join "`n")" }

git fetch origin main --quiet
$local = (git rev-parse HEAD).Trim()
$remote = (git rev-parse origin/main).Trim()
if ($local -ne $remote) { Fail 'Local main and origin/main differ. Push or pull first.' }

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
Step "Verifying single-exe packaging"
$exeSize = (Get-Item $exe).Length
$exeMB = '{0:N2} MB' -f ($exeSize / 1MB)
if ($exeSize -lt 1.5MB) {
    Fail "KillerScan.exe is only $exeMB - Costura does not appear to have embedded the dependencies. Check Fody/FodyWeavers.xml."
}
Write-Host "KillerScan.exe is $exeMB"

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
# The in-app updater (About.cs DoSelfUpdateAsync) downloads this asset from the release,
# next to the exe, and verifies the download against it. WITHOUT it the Update button falls
# back to just opening the releases page. The updater matches the line starting with
# KillerScan.exe and takes the LAST whitespace token as the hash, so the padded columns
# below are fine. Upload it with the exe - gh release create does that in step 11.
Step "Writing SHA256SUMS.txt"
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
$releaseDate = Get-Date -Format 'yyyy-MM-dd'
$hashLower   = $exeHash.ToLower()
$siteDir     = Join-Path (Get-Location).Path 'scan-landing'

$indexPath = Join-Path $siteDir 'index.html'
$indexRaw  = [System.IO.File]::ReadAllText($indexPath)
$indexNew  = $indexRaw
$indexNew  = $indexNew -replace '(<span class="k">version</span>&nbsp;<span class="v">)KillerScan v[0-9]+\.[0-9]+\.[0-9]+', ('${1}' + "KillerScan v$Version")
$indexNew  = $indexNew -replace '(<span class="k">released</span>&nbsp;<span class="v">)[0-9]{4}-[0-9]{2}-[0-9]{2}', ('${1}' + $releaseDate)
$indexNew  = $indexNew -replace '(<span class="k">size</span>&nbsp;<span class="v">)[^<]*', ('${1}' + $exeMB + ' exe')
$indexNew  = $indexNew -replace '(<span class="v hash">)[0-9a-f]{32}<br>[0-9a-f]{32}', ('${1}' + $hashLower.Substring(0, 32) + '<br>' + $hashLower.Substring(32, 32))
if ($indexNew -eq $indexRaw) {
    Write-Warning 'index.html hero block did not change - check the release-info markup still matches the patterns in this script.'
} else {
    [System.IO.File]::WriteAllText($indexPath, $indexNew)
}

foreach ($page in 'index.html', 'about.html', 'technical.html') {
    $p   = Join-Path $siteDir $page
    $raw = [System.IO.File]::ReadAllText($p)
    $new = $raw -replace '(id="verEgg"[^>]*>)v[0-9]+\.[0-9]+\.[0-9]+', ('${1}' + "v$Version")
    if ($new -ne $raw) { [System.IO.File]::WriteAllText($p, $new) }
}

# README: the GPL3 corresponding-source link must point at THIS release's zip.
$readmePath = Join-Path (Get-Location).Path 'README.md'
$readmeRaw  = [System.IO.File]::ReadAllText($readmePath)
$readmeNew  = $readmeRaw -replace '/releases/download/v[0-9]+\.[0-9]+\.[0-9]+/KillerScan-[0-9]+\.[0-9]+\.[0-9]+-src\.zip', "/releases/download/$Tag/KillerScan-$Version-src.zip"
if ($readmeNew -ne $readmeRaw) { [System.IO.File]::WriteAllText($readmePath, $readmeNew) }

# Claim check: the README language count is written by hand, so it silently goes stale the
# moment a locale is added. Compare it against the shipping Strings/*.xaml count and warn.
# Non-fatal - it is a docs claim, not a build input - but it should never be wrong at a tag.
$localeCount = (Get-ChildItem (Join-Path $PSScriptRoot 'Strings') -Filter '*.xaml' -ErrorAction SilentlyContinue).Count
if ($localeCount -gt 0 -and $readmeNew -match 'localized in ([0-9]+) languages') {
    $claimed = [int]$Matches[1]
    if ($claimed -ne $localeCount) {
        Write-Warning "README says 'localized in $claimed languages' but Strings/ has $localeCount locale files. Fix the README (and the language list next to it) before releasing."
    }
}

if ($DryRun) {
    Write-Host "DryRun: would commit and push scan-landing + README for v$Version"
    git --no-pager diff --stat -- scan-landing README.md
} else {
    $siteDirty = git status --porcelain scan-landing README.md
    if ($siteDirty) {
        git add scan-landing README.md
        git commit -m "v${Version}: site and README release info" --quiet
        git push origin main --quiet
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
