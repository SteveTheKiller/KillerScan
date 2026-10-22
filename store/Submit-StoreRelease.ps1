#Requires -Version 5.1
<#
.SYNOPSIS
    Submits a new installer version to the Microsoft Store for an EXE app.

.DESCRIPTION
    Uses the Microsoft Store submission API for MSI or EXE apps. Points the draft's existing
    package at the new installer URL (every other package setting is kept), commits it, waits
    for the Store to fetch the installer, creates the submission and confirms certification
    started. Certification itself takes up to three business days and is not waited for.

    Credentials come from user environment variables: KILLER_STORE_TENANT_ID,
    KILLER_STORE_CLIENT_ID, KILLER_STORE_CLIENT_SECRET and KILLER_STORE_SELLER_ID.

.PARAMETER ProductId
    The Store ID of the product (for example XP8JR4WJK03BJP).

.PARAMETER PackageUrl
    Versioned HTTPS URL of the signed installer. The Store requires the file at this URL to
    never change, so it must include the version.

.PARAMETER DryRun
    Signs in and reads everything, then stops before the first change.

.PARAMETER PreflightOnly
    For release preflight, before the installer exists: signs in and checks that the draft has one
    package and no submission in progress, without loading the installer URL.
#>
param(
    [Parameter(Mandatory)][string]$ProductId,
    [Parameter(Mandatory)][string]$PackageUrl,
    [switch]$DryRun,
    [switch]$PreflightOnly,
    [int]$UploadTimeoutMinutes = 30
)
$ErrorActionPreference = 'Stop'
$api = "https://api.store.microsoft.com/submission/v1/product/$ProductId"

function Get-Setting([string]$Name) {
    $value = [Environment]::GetEnvironmentVariable($Name, 'Process')
    if (-not $value) { $value = [Environment]::GetEnvironmentVariable($Name, 'User') }
    if (-not $value) { throw "Store credential $Name is not set. See the release notes in the harness for setup." }
    return $value
}

function Invoke-Store([string]$Method, [string]$Path, $Body = $null) {
    $params = @{ Method = $Method; Uri = "$api$Path"; Headers = $script:headers; UseBasicParsing = $true }
    if ($null -ne $Body) { $params.Body = ($Body | ConvertTo-Json -Depth 10 -Compress); $params.ContentType = 'application/json' }
    for ($attempt = 1; ; $attempt++) {
        try {
            $response = Invoke-WebRequest @params
            $result = $response.Content | ConvertFrom-Json
        } catch {
            $status = [int]$_.Exception.Response.StatusCode
            $detail = if ($_.ErrorDetails) { $_.ErrorDetails.Message } else { $_.Exception.Message }
            if ($status -eq 429 -and $attempt -lt 5) { Start-Sleep -Seconds (15 * $attempt); continue }
            throw "Store API $Method $Path failed ($status): $detail"
        }
        foreach ($e in @($result.errors)) {
            if ($e) { Write-Host "  Store $($e.code): $($e.message) [$($e.target)]" -ForegroundColor Yellow }
        }
        if (-not $result.isSuccess) { throw "Store API $Method $Path was not accepted." }
        return $result.responseData
    }
}

if ($PackageUrl -notmatch '^https://') { throw "PackageUrl must be an https URL: $PackageUrl" }

Write-Host "==> Signing in to the Microsoft Store submission API"
$tenant = Get-Setting 'KILLER_STORE_TENANT_ID'
$token = (Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token" `
    -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type    = 'client_credentials'
        client_id     = Get-Setting 'KILLER_STORE_CLIENT_ID'
        client_secret = Get-Setting 'KILLER_STORE_CLIENT_SECRET'
        scope         = 'https://api.store.microsoft.com/.default'
    }).access_token
$script:headers = @{ Authorization = "Bearer $token"; 'X-Seller-Account-Id' = (Get-Setting 'KILLER_STORE_SELLER_ID') }

if (-not $PreflightOnly) {
    Write-Host "==> Checking the installer is reachable"
    try { $head = Invoke-WebRequest -Uri $PackageUrl -Method Head -UseBasicParsing }
    catch { throw "The installer URL does not load: $PackageUrl ($($_.Exception.Message))" }
    Write-Host "  $PackageUrl ($($head.Headers['Content-Length']) bytes)"
}

Write-Host "==> Reading the current draft"
$state = Invoke-Store GET '/status'
if ($state.ongoingSubmissionId -and $PreflightOnly) {
    Write-Host "WARNING: Store submission $($state.ongoingSubmissionId) is still in certification. The release can go ahead; submit this version to the Store once it finishes." -ForegroundColor Yellow
    return
}
if ($state.ongoingSubmissionId) {
    throw "Submission $($state.ongoingSubmissionId) is already in progress. Wait for it to finish or cancel it in Partner Center, then run this again."
}
$packages = @((Invoke-Store GET '/packages').packages)
if ($packages.Count -ne 1) { throw "Expected one package in the draft, found $($packages.Count). Update it in Partner Center." }
$package = $packages[0]
Write-Host "  Package $($package.packageId): $($package.packageUrl)"
if ($PreflightOnly) { Write-Host "  Store preflight OK."; return }

# Sync translated listings before submitting the installer, including newly added languages.
$listingScript = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'KillerHarness\scripts\StoreUpdate.ps1'
if (-not (Test-Path -LiteralPath $listingScript)) { throw "Store listing updater is missing: $listingScript" }
$listingArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $listingScript,
    '-App', 'KillerScan', '-ListingsOnly', '-StoreProductId', $ProductId, '-StoreDirectory', $PSScriptRoot)
if (-not $DryRun) { $listingArgs += '-DraftOnly' }
& powershell @listingArgs
if ($LASTEXITCODE -ne 0) { throw 'Store listing sync failed. The installer was not submitted.' }

if ($package.packageUrl -eq $PackageUrl) {
    Write-Host "  The draft already points at this installer."
} elseif ($DryRun) {
    Write-Host "DryRun: would change the package URL to $PackageUrl, commit it and submit." -ForegroundColor Cyan
    return
} else {
    Write-Host "==> Pointing the package at the new installer"
    $null = Invoke-Store PATCH "/packages/$($package.packageId)" @{ packageUrl = $PackageUrl }
    $null = Invoke-Store POST '/packages/commit'
}
if ($DryRun) { Write-Host "DryRun: would submit." -ForegroundColor Cyan; return }

Write-Host "==> Waiting for the Store to fetch the installer"
$deadline = (Get-Date).AddMinutes($UploadTimeoutMinutes)
do {
    Start-Sleep -Seconds 10
    $state = Invoke-Store GET '/status'
    if ((Get-Date) -gt $deadline) { throw "The Store did not finish fetching the installer within $UploadTimeoutMinutes minutes. Run this again to continue." }
} until ($state.isReady)

Write-Host "==> Submitting for certification"
$submission = Invoke-Store POST '/submit'
$id = $submission.submissionId
if (-not $id) { throw "The Store did not return a submission ID." }
$status = Invoke-Store GET "/submission/$id/status"
if ($status.hasFailed) { throw "Submission $id failed immediately with status $($status.publishingStatus). See Partner Center." }
Write-Host "Submitted $id for certification ($($status.publishingStatus)). Certification takes up to three business days." -ForegroundColor Green
