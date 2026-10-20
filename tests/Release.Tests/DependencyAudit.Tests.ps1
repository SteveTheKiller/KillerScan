[CmdletBinding()]
param([Parameter(Mandatory)][string]$ScratchRoot)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Get-Content -LiteralPath (Join-Path $repo 'release.ps1') -Raw
$start = $source.IndexOf('Step "Scanning for vulnerable packages"')
$end = $source.IndexOf('Step "Testing the native speed-test engine and endpoint"', $start)
if ($start -lt 0 -or $end -le $start) { throw 'The dependency gate boundaries are missing.' }
$gate = [scriptblock]::Create($source.Substring($start, $end - $start))
$folder = Join-Path ([IO.Path]::GetFullPath($ScratchRoot)) ('dependency-audit-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $folder
$nativeFailure = Join-Path $folder 'native-failure.cmd'
[IO.File]::WriteAllText($nativeFailure, "@echo off`r`necho fixture native stderr 1>&2`r`nexit /b 44`r`n")
$clean = '{"version":1,"sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"KillerScan.csproj"},{"path":"Engine/KillerScan.Engine.csproj"}]}'
$vulnerable = '{"version":1,"sources":["https://api.nuget.org/v3/index.json"],"projects":[{"path":"KillerScan.csproj","frameworks":[{"framework":"net48","topLevelPackages":[{"id":"Fixture.Package","vulnerabilities":[{"severity":"High","advisoryurl":"https://example.invalid/advisory"}]}]}]},{"path":"Engine/KillerScan.Engine.csproj"}]}'
$cases = @(
    @{ Name = 'RestoreFailure'; Restore = 42; Audit = 0; Json = $clean; Reject = $true; Reason = 'restore failed with exit 42' },
    @{ Name = 'NativeRestoreStderr'; Restore = 44; Audit = 0; Json = $clean; Reject = $true; Reason = 'restore failed with exit 44'; Native = $true },
    @{ Name = 'AuditFailure'; Restore = 0; Audit = 43; Json = 'audit unavailable'; Reject = $true; Reason = 'audit failed with exit 43' },
    @{ Name = 'NativeAuditStderr'; Restore = 0; Audit = 44; Json = $clean; Reject = $true; Reason = 'audit failed with exit 44'; NativeAudit = $true },
    @{ Name = 'Malformed'; Restore = 0; Audit = 0; Json = '{'; Reject = $true; Reason = 'malformed JSON' },
    @{ Name = 'MissingProjects'; Restore = 0; Audit = 0; Json = '{"version":1,"projects":[]}'; Reject = $true; Reason = 'incomplete' },
    @{ Name = 'MissingEngine'; Restore = 0; Audit = 0; Json = '{"version":1,"sources":["fixture"],"projects":[{"path":"KillerScan.csproj"}]}'; Reject = $true; Reason = 'omitted Engine' },
    @{ Name = 'AuditProblem'; Restore = 0; Audit = 0; Json = $clean.Replace('"sources":', '"problems":[{"text":"feed unavailable"}],"sources":'); Reject = $true; Reason = 'audit problem' },
    @{ Name = 'Vulnerable'; Restore = 0; Audit = 0; Json = $vulnerable; Reject = $true; Reason = 'Vulnerable package Fixture.Package' },
    @{ Name = 'TransitiveVulnerable'; Restore = 0; Audit = 0; Json = $vulnerable.Replace('topLevelPackages', 'transitivePackages'); Reject = $true; Reason = 'Vulnerable package Fixture.Package' },
    @{ Name = 'UnsupportedSchema'; Restore = 0; Audit = 0; Json = $clean.Replace('"version":1', '"version":2'); Reject = $true; Reason = 'incomplete' },
    @{ Name = 'IncompletePackage'; Restore = 0; Audit = 0; Json = $clean.Replace('"path":"KillerScan.csproj"', '"path":"KillerScan.csproj","frameworks":[{"framework":"net48","topLevelPackages":[{"id":"Incomplete"}]}]'); Reject = $true; Reason = 'incomplete package data' },
    @{ Name = 'Clean'; Restore = 0; Audit = 0; Json = $clean; Reject = $false }
)
foreach ($case in $cases) {
    $caseFolder = Join-Path $folder $case.Name
    $null = New-Item -ItemType Directory -Path $caseFolder
    Push-Location $caseFolder
    try {
        $calls = [Collections.Generic.List[string]]::new()
        function Step { }
        function Fail([string]$message) { throw $message }
        function dotnet {
            $calls.Add(($args -join ' '))
            if ($case.Native -or ($case.NativeAudit -and $args[0] -ne 'restore')) { & $nativeFailure; return }
            if ($args[0] -eq 'restore') { $global:LASTEXITCODE = $case.Restore; return 'fixture restore diagnostic' }
            $global:LASTEXITCODE = $case.Audit
            return $case.Json
        }
        $rejected = $false
        $reason = ''
        try { & $gate | Out-Null } catch { $rejected = $true; $reason = $_.Exception.Message }
        if ($rejected -ne $case.Reject) { throw ($case.Name + ': dependency gate returned the wrong verdict.') }
        if ($ErrorActionPreference -ne 'Stop') { throw 'The gate did not restore the caller error preference.' }
        if ($case.Reject -and $reason -notmatch $case.Reason) { throw ($case.Name + ': wrong rejection reason: ' + $reason) }
        if ($case.Restore -ne 0 -and $calls.Count -ne 1) { throw 'Restore failure reached the audit command.' }
        if ($case.Restore -eq 0 -and $calls.Count -ne 2) { throw 'The gate did not execute restore and audit exactly once.' }
        if ($source -match '--format json' -and -not (Test-Path -LiteralPath 'obj/release-validation/restore.txt')) { throw 'Restore diagnostics were not retained.' }
        if ($case.Restore -eq 0 -and $source -match '--format json' -and -not (Test-Path -LiteralPath 'obj/release-validation/dependency-audit.json')) { throw 'Audit output was not retained.' }
        if ($case.Native -and (Get-Content -LiteralPath 'obj/release-validation/restore.txt' -Raw) -notmatch 'fixture native stderr') { throw 'Native restore stderr was not retained.' }
        if ($case.NativeAudit -and (Get-Content -LiteralPath 'obj/release-validation/dependency-audit.json' -Raw) -notmatch 'fixture native stderr') { throw 'Native audit stderr was not retained.' }
        Write-Output ('PASS: ' + $case.Name)
    } finally { Pop-Location }
}
Write-Output ('Evidence: ' + $folder)
