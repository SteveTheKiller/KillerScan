#Requires -Version 7
<#
    Runs the focused regression groups that the console runners only run when asked by flag.
    The list lives in tests/suites.json. Before running anything, every --flag in each runner's
    Program.cs must be listed there, either to run or as excluded with a reason, so a new suite
    cannot be added and then never run.

        pwsh tests/Run-FocusedSuites.ps1 -Suite engine -Framework net48 -Configuration Release
        pwsh tests/Run-FocusedSuites.ps1 -Suite app -Framework net48 -Configuration Release
        pwsh tests/Run-FocusedSuites.ps1 -CheckOnly
#>
param(
    [ValidateSet('engine', 'app')][string]$Suite,
    [string]$Framework = 'net48',
    [string]$Configuration = 'Release',
    [int]$TimeoutSeconds = 300,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$inventory = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'suites.json') | ConvertFrom-Json

# Every flag a runner accepts must be listed, to run or excluded with a reason.
$problems = @()
foreach ($name in 'engine', 'app') {
    $entry = $inventory.$name
    $source = Get-Content -Raw -LiteralPath (Join-Path $root $entry.program)
    $flags = [regex]::Matches($source, '"(--[a-z0-9-]+)"') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
    $listed = @($entry.ci) + @($entry.excluded.PSObject.Properties.Name)
    foreach ($flag in $flags) { if ($listed -notcontains $flag) { $problems += "$name runner flag $flag is not in tests/suites.json." } }
    foreach ($flag in $entry.ci) { if ($flags -notcontains $flag) { $problems += "$name suite $flag is listed but the runner has no such flag." } }
}
if ($problems) { $problems | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
Write-Output 'Suite inventory matches both runners.'
if ($CheckOnly) { exit 0 }
if (-not $Suite) { throw 'Pass -Suite engine or -Suite app, or -CheckOnly.' }

$entry = $inventory.$Suite
$project = Join-Path $root $entry.project
$failed = @()
foreach ($flag in $entry.ci) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $log = [System.IO.Path]::GetTempFileName()
    # Start-Process joins the list with plain spaces, so a checkout path with a space is quoted here.
    $arguments = @('run', '--project', ('"' + $project + '"'), '--framework', $Framework, '--configuration', $Configuration,
        '--no-build', '--no-restore', '--', $flag)
    $process = Start-Process dotnet -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        # A stalled suite is a failure, and its whole process tree is stopped so later suites run.
        & taskkill /T /F /PID $process.Id | Out-Null
        $failed += "$flag (timed out after $TimeoutSeconds s)"
        Write-Output "TIMEOUT $flag"
        continue
    }
    $output = (Get-Content -Raw -LiteralPath $log) + (Get-Content -Raw -LiteralPath "$log.err" -ErrorAction SilentlyContinue)
    Remove-Item -LiteralPath $log, "$log.err" -ErrorAction SilentlyContinue
    $seconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    if ($process.ExitCode -ne 0) {
        $failed += "$flag (exit $($process.ExitCode))"
        Write-Output "FAIL $flag ($seconds s)"
        Write-Output $output
    }
    else { Write-Output "ok   $flag ($seconds s)" }
}
if ($failed) { Write-Error ("Focused suites failed: " + ($failed -join ', ')) -ErrorAction Continue; exit 1 }
Write-Output "PASS: $($entry.ci.Count) focused $Suite suites."
