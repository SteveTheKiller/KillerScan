# Read-only integration check against an already running loopback adapter.
# Loads the compiled app engine without starting WPF or changing app settings.
param(
    [string]$AppPath = (Join-Path $PSScriptRoot '../../bin/Debug/net48/KillerScan.exe'),
    [uri]$Endpoint = 'http://127.0.0.1:18765/',
    [ValidateRange(1, 3072)][int]$BudgetMiB = 32,
    [ValidateRange(1, 60)][int]$PhaseSeconds = 2,
    [ValidateRange(0, 10)][double]$WarmupSeconds = 0.25
)
$ErrorActionPreference = 'Stop'
if (-not $Endpoint.IsAbsoluteUri -or -not $Endpoint.IsLoopback -or $Endpoint.Scheme -notin @('http', 'https')) {
    throw 'This integration check only accepts a loopback HTTP endpoint.'
}
$assemblyPath = (Resolve-Path -LiteralPath $AppPath).ProviderPath
[void][Reflection.Assembly]::LoadFrom($assemblyPath)
$options = New-Object KillerScan.Services.SpeedTest.SpeedTestOptions
$options.Endpoint = $Endpoint
$options.ByteBudgetPerPhase = [long]$BudgetMiB * 1024 * 1024
$options.PhaseDuration = [TimeSpan]::FromSeconds($PhaseSeconds)
$options.WarmupDuration = [TimeSpan]::FromSeconds($WarmupSeconds)
$engine = New-Object KillerScan.Services.SpeedTest.SpeedTestEngine
$pending = $engine.RunAsync($options, $null, [Threading.CancellationToken]::None)
$result = $pending.GetAwaiter().GetResult()
if ($result.IdleLatencySamples.Count -ne $options.IdleLatencySampleCount) { throw 'Missing idle latency samples.' }
foreach ($phase in @($result.Download, $result.Upload)) {
    if ($phase.BytesTransferred -le 0 -or $phase.Mbps -le 0) { throw 'The engine did not measure a positive transfer.' }
    if ($phase.BytesScheduled -gt $options.ByteBudgetPerPhase) { throw 'The engine exceeded its byte budget.' }
    if ($phase.BytesTransferred + $phase.WarmupBytes -gt $phase.BytesScheduled) { throw 'Acknowledged bytes exceed scheduled bytes.' }
}
[ordered]@{
    Endpoint = $result.Endpoint.AbsoluteUri
    App = $assemblyPath
    ElapsedSeconds = $result.Elapsed.TotalSeconds
    IdleSamples = $result.IdleLatencySamples.Count
    IdleLatencyMs = $result.IdleLatencyMs
    DownloadBytes = $result.Download.BytesTransferred
    DownloadWarmupBytes = $result.Download.WarmupBytes
    DownloadScheduledBytes = $result.Download.BytesScheduled
    DownloadMbps = $result.Download.Mbps
    DownloadLoadedSamples = $result.Download.LatencySamples.Count
    UploadBytes = $result.Upload.BytesTransferred
    UploadWarmupBytes = $result.Upload.WarmupBytes
    UploadScheduledBytes = $result.Upload.BytesScheduled
    UploadMbps = $result.Upload.Mbps
    UploadLoadedSamples = $result.Upload.LatencySamples.Count
} | ConvertTo-Json
