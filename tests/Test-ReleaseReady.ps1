#Requires -Version 7
<#
    Answers "is KillerScan ready to release?" in one run, so release.ps1 does not have to test
    anything and its dry run and real run stay fast.

    Runs every check CI runs, plus the live speed-test checks that used to sit in release.ps1:
      - Release builds of the app and both test runners
      - the engine runner's full pass on net48 and net10.0
      - the speed-test runner's full pass, its --worker --internet pass, and the Worker's Node tests
      - every focused suite (engine on both targets, app) and the suite inventory check
      - the release script contract tests in tests\Release.Tests
      - the PowerShell module contract and prompt git safety on PowerShell 7 and 5.1

    Every step runs even after a failure, so one run lists everything that is wrong. The last line
    is GREEN or RED, and the exit code is 0 only when every step passed.

    Usage, from the repository root or anywhere:
        pwsh -NoProfile -File tests\Test-ReleaseReady.ps1

    Needs Node.js 22 or newer and an internet connection for the speed-test checks.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repo
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('KillerScanReleaseReady-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch -Force
$results = [System.Collections.Generic.List[object]]::new()
$started = Get-Date

function Invoke-Check([string]$Name, [scriptblock]$Body) {
    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $global:LASTEXITCODE = 0
    $ok = $true
    try { & $Body; if ($LASTEXITCODE -ne 0) { $ok = $false } }
    catch { Write-Host $_ -ForegroundColor Red; $ok = $false }
    $clock.Stop()
    $results.Add([pscustomobject]@{ Check = $Name; Passed = $ok; Seconds = [math]::Round($clock.Elapsed.TotalSeconds) })
    if (-not $ok) { Write-Host "FAILED: $Name" -ForegroundColor Red }
}

if (!$env:WINDIR) { $env:WINDIR = $env:SYSTEMROOT }

try {
    Invoke-Check 'Build the app (Release)' { dotnet build KillerScan.csproj -c Release -nologo -v:minimal }
    Invoke-Check 'Build the engine runner (Release)' { dotnet build tests\KillerScan.Engine.Tests\KillerScan.Engine.Tests.csproj -c Release -nologo -v:minimal }
    Invoke-Check 'Build the speed-test runner (Release)' { dotnet build tests\SpeedTest.Tests\SpeedTest.Tests.csproj -c Release -nologo -v:minimal }

    foreach ($tfm in 'net48', 'net10.0') {
        Invoke-Check "Engine runner, full pass on $tfm" {
            dotnet run --project tests\KillerScan.Engine.Tests\KillerScan.Engine.Tests.csproj -c Release -f $tfm --no-build --no-restore
        }
    }

    $speedTests = '.\tests\SpeedTest.Tests\bin\Release\net48\SpeedTest.Tests.exe'
    Invoke-Check 'Speed-test runner, full pass' { & $speedTests }
    Invoke-Check 'Speed-test runner, live Worker and internet' { & $speedTests --worker --internet }
    Invoke-Check 'Speed-test Worker endpoint (Node)' {
        if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js 22 or newer is required for the Worker tests.' }
        node --test server/speedtest/worker.test.mjs
    }

    Invoke-Check 'Suite inventory matches the runners' { & .\tests\Run-FocusedSuites.ps1 -CheckOnly }
    foreach ($tfm in 'net48', 'net10.0') {
        Invoke-Check "Focused engine suites on $tfm" { & .\tests\Run-FocusedSuites.ps1 -Suite engine -Framework $tfm -Configuration Release }
    }
    Invoke-Check 'Focused app suites' { & .\tests\Run-FocusedSuites.ps1 -Suite app -Configuration Release }

    foreach ($test in Get-ChildItem tests\Release.Tests -Filter *.Tests.ps1 | Sort-Object Name) {
        $needsScratch = (Get-Content -LiteralPath $test.FullName -TotalCount 3) -match 'ScratchRoot'
        Invoke-Check "Release contract: $($test.BaseName)" {
            if ($needsScratch) { pwsh -NoProfile -File $test.FullName -ScratchRoot $scratch }
            else { pwsh -NoProfile -File $test.FullName }
        }
    }

    $exe = 'bin\Release\net48\KillerScan.exe'
    Invoke-Check 'Module contract on PowerShell 7' { pwsh -NoProfile -File tests\Module\ModuleContract.ps1 -Exe $exe }
    Invoke-Check 'Module contract on Windows PowerShell 5.1' { powershell -NoProfile -ExecutionPolicy Bypass -File tests\Module\ModuleContract.ps1 -Exe $exe }
    Invoke-Check 'Prompt git safety on PowerShell 7' { pwsh -NoProfile -File tests\Module\PromptGitSafety.ps1 }
    Invoke-Check 'Prompt git safety on Windows PowerShell 5.1' { powershell -NoProfile -ExecutionPolicy Bypass -File tests\Module\PromptGitSafety.ps1 }
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
$results | Format-Table -AutoSize | Out-String | Write-Host
$failed = @($results | Where-Object { -not $_.Passed })
$minutes = [math]::Round(((Get-Date) - $started).TotalMinutes, 1)
if ($failed.Count) {
    Write-Host "RED: $($failed.Count) of $($results.Count) checks failed in $minutes minutes. Do not release." -ForegroundColor Red
    exit 1
}
Write-Host "GREEN: all $($results.Count) checks passed in $minutes minutes. release.ps1 is clear to run." -ForegroundColor Green
exit 0
