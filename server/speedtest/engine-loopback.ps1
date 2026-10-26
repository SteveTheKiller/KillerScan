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
# The speed test lives in KillerScan.Engine. A Debug build keeps that DLL beside the exe; a
# Release build embeds it, so point -AppPath at a Debug build or at the engine's own output.
$enginePath = if ([IO.Path]::GetFileName($assemblyPath) -eq 'KillerScan.Engine.dll') { $assemblyPath }
              else { Join-Path (Split-Path -Parent $assemblyPath) 'KillerScan.Engine.dll' }
if (-not (Test-Path -LiteralPath $enginePath)) {
    throw "KillerScan.Engine.dll was not found beside $assemblyPath. Use a Debug build or pass -AppPath to the engine's net48 KillerScan.Engine.dll."
}
[void][Reflection.Assembly]::LoadFrom($enginePath)
$options = New-Object KillerScan.Engine.SpeedTest.SpeedTestOptions
$options.Endpoint = $Endpoint
$options.ByteBudgetPerPhase = [long]$BudgetMiB * 1024 * 1024
$options.PhaseDuration = [TimeSpan]::FromSeconds($PhaseSeconds)
$options.WarmupDuration = [TimeSpan]::FromSeconds($WarmupSeconds)
$engine = New-Object KillerScan.Engine.SpeedTest.SpeedTestEngine
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
    Engine = $enginePath
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
