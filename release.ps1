#Requires -Version 5.1
<#
.SYNOPSIS
    KillerScan release script: build -> sign -> SHA256 -> print summary.
.DESCRIPTION
    1. Publishes using FolderProfile1 (net48, win-x64) -- also runs bundle-source.ps1 to zip the source.
    2. Signs KillerScan.exe with your Certum cert via signtool.
    3. Computes and prints the SHA256 for pasting into the landing pages.

.PARAMETER CertName
    CN (Subject) of your Certum certificate as it appears in the Windows cert store.
    Run: Get-ChildItem Cert:\CurrentUser\My | Select Subject
    to find it. Defaults to the placeholder below.

.PARAMETER SkipSign
    Skip signing (useful for a test build).

.EXAMPLE
    .\release.ps1 -CertName "Open Source Developer, Stephen ..."
#>
param(
    [string]$CertName   = "Open Source Developer Stephen Riley",
    [switch]$SkipSign,
    [switch]$Choco,
    [string]$ChocoApiKey = $env:CHOCO_API_KEY
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$proj       = Join-Path $PSScriptRoot "KillerScan.csproj"
$publishDir = Join-Path $PSScriptRoot "bin\Release\net48\publish"
$exe        = Join-Path $publishDir "KillerScan.exe"

# Get version from csproj
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Select-Object -First 1

# -- 1. Build / Publish --------------------------------------------------------
Write-Host "`n==> Building (Release, net48, win-x64)..." -ForegroundColor Cyan

# Find MSBuild
$msbuild = $null
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vsWhere) {
    $vsPath = & $vsWhere -latest -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    if ($vsPath) {
        $candidate = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
        if (Test-Path $candidate) { $msbuild = $candidate }
    }
}
if (-not $msbuild) { $msbuild = "dotnet" }

if ($msbuild -eq "dotnet") {
    & dotnet publish $proj /p:PublishProfile=FolderProfile1 -c Release
} else {
    & $msbuild $proj /t:Publish /p:PublishProfile=FolderProfile1 /p:Configuration=Release /m /nologo /v:m
}

if ($LASTEXITCODE -ne 0) { throw "Build failed." }
if (-not (Test-Path $exe)) { throw "EXE not found at: $exe" }
Write-Host "    EXE: $exe" -ForegroundColor Green

# -- 2. Sign -------------------------------------------------------------------
if (-not $SkipSign) {
    Write-Host "`n==> Signing with Certum cert: $CertName..." -ForegroundColor Cyan

    $signtool = $null
    $kitBase  = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kitBase) {
        $signtool = Get-ChildItem "$kitBase\*\x64\signtool.exe" -Recurse -ErrorAction SilentlyContinue |
                    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw "signtool.exe not found. Install Windows SDK." }
    Write-Host "    signtool: $signtool"

    & $signtool sign `
        /fd  sha256 `
        /tr  "http://timestamp.digicert.com" `
        /td  sha256 `
        /n   $CertName `
        /d   "KillerScan" `
        /du  "https://killerscan.net" `
        /v   $exe

    if ($LASTEXITCODE -ne 0) { throw "Signing failed. Is Certum SimplySign Desktop running?" }
    Write-Host "    Signed OK" -ForegroundColor Green
} else {
    Write-Host "`n==> Skipping signing (-SkipSign)" -ForegroundColor Yellow
}

# -- 3. SHA256 -----------------------------------------------------------------
Write-Host "`n==> Computing SHA256..." -ForegroundColor Cyan
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host "    SHA256: $hash" -ForegroundColor Green

# -- 4. Source zip --------------------------------------------------------------
Write-Host "`n==> Bundling source zip..." -ForegroundColor Cyan
# Create the source zip for THIS version only if it's missing, then pick it by
# exact name. bundle-source.ps1 never overwrites or deletes an existing source
# bundle, and old-version zips in the folder are left untouched - only the build
# artifacts (exe / nupkg) get overwritten on a re-run.
& (Join-Path $PSScriptRoot "build\bundle-source.ps1") -ProjectDir $PSScriptRoot -Version $version -AppName "KillerScan" -PublishDir $publishDir
$srcZip = Get-ChildItem $publishDir -Filter "KillerScan-$version-src.zip" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($srcZip) {
    Write-Host "    Source zip: $($srcZip.FullName)" -ForegroundColor Green
} else {
    Write-Host "    (Source bundle failed -- is git installed and is this a repo?)" -ForegroundColor Yellow
}

# -- 4b. SHA256SUMS.txt (consumed by the in-app self-updater) ------------------
# The About-screen updater downloads the target release's KillerScan.exe and verifies it
# against this file. The updater reads SHA256SUMS.txt from the RELEASE ASSETS (right next to
# the exe), so just UPLOAD this file to the GitHub release alongside KillerScan.exe. No need
# to commit it or worry about tag/commit order - the two files ride together on the release.
Write-Host "`n==> Writing SHA256SUMS.txt..." -ForegroundColor Cyan
# Written into the publish folder next to KillerScan.exe and the -src.zip, so every file you
# upload to the GitHub release is in one place. The updater reads this from the release assets.
$sumsPath  = Join-Path $publishDir "SHA256SUMS.txt"
$sumsLines = @()
$sumsLines += ("{0,-26} {1}" -f "KillerScan.exe", $hash)
if ($srcZip) {
    $srcHash = (Get-FileHash $srcZip.FullName -Algorithm SHA256).Hash
    $sumsLines += ("{0,-26} {1}" -f $srcZip.Name, $srcHash)
}
Set-Content -Path $sumsPath -Value $sumsLines -Encoding ascii
Write-Host "    Wrote: $sumsPath" -ForegroundColor Green

# -- 5. Chocolatey pack/push ---------------------------------------------------
$nupkg = $null
if ($Choco) {   # Chocolatey is opt-in: pass -Choco to pack/push. Default release skips it.
    Write-Host "`n==> Packing Chocolatey package..." -ForegroundColor Cyan
    $chocoDir    = Join-Path $PSScriptRoot "choco"
    $nuspec      = Join-Path $chocoDir "killerscan.nuspec"
    $installPs1  = Join-Path $chocoDir "tools\chocolateyInstall.ps1"

    $nuspecOrig  = Get-Content $nuspec -Raw
    $installOrig = Get-Content $installPs1 -Raw

    try {
        $nuspecOrig  -replace 'REPLACE_VERSION', $version | Set-Content $nuspec -NoNewline
        $installOrig -replace 'REPLACE_HASH',    $hash    | Set-Content $installPs1 -NoNewline

        Push-Location $chocoDir
        choco pack killerscan.nuspec
        if ($LASTEXITCODE -ne 0) { throw "choco pack failed." }
        $nupkg = Join-Path $chocoDir "killerscan.$version.nupkg"
        Write-Host "    Packed: $nupkg" -ForegroundColor Green
        Pop-Location
    } finally {
        $nuspecOrig  | Set-Content $nuspec -NoNewline
        $installOrig | Set-Content $installPs1 -NoNewline
    }

    if ($ChocoApiKey) {
        Write-Host "`n==> Pushing to Chocolatey community repo..." -ForegroundColor Cyan
        choco push $nupkg --source https://push.chocolatey.org --api-key $ChocoApiKey
        if ($LASTEXITCODE -ne 0) { throw "choco push failed." }
        Write-Host "    Pushed OK" -ForegroundColor Green
    } else {
        Write-Host "`n    Skipping push -- set CHOCO_API_KEY env var or pass -ChocoApiKey to push automatically." -ForegroundColor Yellow
    }
}

# -- 6. Summary ----------------------------------------------------------------
Write-Host "`n+================================================================+" -ForegroundColor Cyan
Write-Host   "  KillerScan v$version release artifacts" -ForegroundColor White
Write-Host   "  EXE   : $exe"
if ($srcZip) { Write-Host "  SRC   : $($srcZip.FullName)" }
if ($nupkg)  { Write-Host "  NUPKG : $nupkg" }
Write-Host   "  SHA256: $hash" -ForegroundColor Green
Write-Host   "  SUMS  : $sumsPath" -ForegroundColor Green
Write-Host   ""
Write-Host   "  >> UPLOAD SHA256SUMS.txt to the GitHub release alongside KillerScan.exe." -ForegroundColor Yellow
Write-Host   "     The in-app updater reads it from the release assets; no commit/tag order to get right." -ForegroundColor Yellow
Write-Host   ""
Write-Host   "  Paste SHA256 into:"
Write-Host   "    KillerScan\scan-landing\index.html (line ~181)"
Write-Host   "    killer-tools-site\src\tools\killer-scan\killer-scan.vue (line ~74)"
Write-Host "+================================================================+" -ForegroundColor Cyan
