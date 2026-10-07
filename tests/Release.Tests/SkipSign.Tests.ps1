[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Join-Path $repo 'release.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }

# Exercise the actual unsigned guard and publication commands with harmless command doubles.
$guard = @($ast.EndBlock.Statements | Where-Object {
    $_ -is [System.Management.Automation.Language.IfStatementAst] -and
    $_.Clauses[0].Item1.Extent.Text -eq '$SkipSign'
})[0]
$stop = @($ast.EndBlock.Statements | Where-Object {
    $_ -is [System.Management.Automation.Language.IfStatementAst] -and
    $_.Extent.Text.Contains('DryRun: stopping before tag and release')
})[0]
if (-not $guard -or -not $stop) { throw 'Release guard or dry-run exit is missing.' }
$firstStep = @($ast.EndBlock.Statements | Where-Object { $_.Extent.Text -match '^Step ' })[0]
if ($guard.Extent.StartOffset -ge $firstStep.Extent.StartOffset) {
    throw 'Unsigned mode must be set before any release work starts.'
}
$assignments = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -eq '$DryRun'
}, $true))
if ($assignments.Count -ne 1 -or $assignments[0].Extent.StartOffset -gt $guard.Extent.EndOffset) {
    throw 'Dry-run mode must not be reset after the unsigned guard.'
}
$publish = @($ast.EndBlock.Statements | Where-Object {
    $_.Extent.StartOffset -gt $stop.Extent.EndOffset -and
    $_.Extent.Text -match '^(git tag |git push origin \$Tag|gh release create |gh workflow run )'
})
if ($publish.Count -ne 4) { throw 'Expected all tag, push, release and site-refresh commands.' }

$folder = Join-Path $env:TEMP ('KillerScan-SkipSign-tests-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $folder
$driver = Join-Path $folder 'driver.ps1'
$prefix = @'
param([switch]$SkipSign, [switch]$DryRun, [switch]$Choco, [string]$Log)
$ErrorActionPreference = 'Stop'
function Step { }
function git { Add-Content -LiteralPath $Log -Value ('git ' + ($args -join ' ')); $global:LASTEXITCODE = 0 }
function gh { Add-Content -LiteralPath $Log -Value ('gh ' + ($args -join ' ')); $global:LASTEXITCODE = 0 }
$Tag = 'v1.8.0'
$exe = 'KillerScan.exe'
$srcZip = 'KillerScan-1.8.0-src.zip'
$sumsFile = 'SHA256SUMS.txt'
$notesFile = 'notes.md'
'@
$body = $prefix + "`n" + $guard.Extent.Text + "`n" + $stop.Extent.Text + "`n" +
    (($publish | ForEach-Object { $_.Extent.Text }) -join "`n")
[System.IO.File]::WriteAllText($driver, $body)
$hostExe = (Get-Process -Id $PID).Path
$cases = @(
    @{ Name = 'SkipSign'; Args = @('-SkipSign'); Publishes = $false },
    @{ Name = 'SkipSignWithChoco'; Args = @('-SkipSign', '-Choco'); Publishes = $false },
    @{ Name = 'SkipSignWithDryRun'; Args = @('-SkipSign', '-DryRun'); Publishes = $false },
    @{ Name = 'DryRun'; Args = @('-DryRun'); Publishes = $false },
    @{ Name = 'NormalRelease'; Args = @(); Publishes = $true }
)
foreach ($case in $cases) {
    $log = Join-Path $folder ($case.Name + '.log')
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $driver, '-Log', $log) + $case.Args
    $output = & $hostExe @arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ($output | Out-String) }
    $calls = if (Test-Path -LiteralPath $log) { @(Get-Content -LiteralPath $log) } else { @() }
    if ($case.Publishes) {
        if ($calls.Count -ne 4 -or $calls[2] -notmatch '^gh release create v1\.8\.0 ') {
            throw 'Normal signed release path no longer reaches publication.'
        }
    } elseif ($calls.Count -ne 0) {
        throw ($case.Name + ' reached a publication command: ' + ($calls -join ', '))
    }
    Write-Output ('PASS: ' + $case.Name)
}
