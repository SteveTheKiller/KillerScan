param(
    [string]$UninstallScript = (Join-Path $PSScriptRoot '..\choco\tools\chocolateyUninstall.ps1')
)
$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('KillerScan-uninstall-caller-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$originalProgramFiles = $env:ProgramFiles
$global:KsDeploymentFixtureHasRegistration = $true
$global:KsDeploymentFixtureReads = 0
$global:KsDeploymentFixtureDenied = $false

# Replace only the actual registry boundary. The package script and filesystem test are real.
function Get-ItemProperty {
    [CmdletBinding()]
    param([string]$LiteralPath)
    if ($LiteralPath -ne 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan') {
        throw 'The fixture attempted an unexpected registry read.'
    }
    $global:KsDeploymentFixtureReads++
    if ($global:KsDeploymentFixtureDenied) {
        throw (New-Object UnauthorizedAccessException('The fixture registry access is denied.'))
    }
    if (-not $global:KsDeploymentFixtureHasRegistration) {
        throw (New-Object Management.Automation.ItemNotFoundException('The fixture registration is absent.'))
    }
    $registered = if ($global:KsDeploymentFixtureCommand) { $global:KsDeploymentFixtureCommand } else { 'A registered application with an unavailable image' }
    [pscustomobject]@{ QuietUninstallString = $registered }
}

try {
    $env:ProgramFiles = $fixtureRoot
    $failed = $false
    try { & $UninstallScript } catch { $failed = $true }
    if (-not $failed -or $global:KsDeploymentFixtureReads -ne 1) {
        throw 'A registered installation with an unavailable image falsely reported uninstall success.'
    }
    Write-Output 'PASS registered-but-unavailable image fails without launching or removing anything.'

    $global:KsDeploymentFixtureHasRegistration = $false
    & $UninstallScript
    if ($global:KsDeploymentFixtureReads -ne 2) { throw 'An absent application was not checked against registration.' }
    Write-Output 'PASS an absent image and absent registration need no removal.'

    $global:KsDeploymentFixtureDenied = $true
    $failed = $false
    try { & $UninstallScript } catch { $failed = $true }
    if (-not $failed -or $global:KsDeploymentFixtureReads -ne 3) { throw 'Denied registry access was treated as an absent installation.' }
    Write-Output 'PASS denied registry access is reported as failure.'

    $global:KsDeploymentFixtureDenied = $false
    $fixtureApp = Join-Path $fixtureRoot 'KillerScan'
    [IO.Directory]::CreateDirectory($fixtureApp) | Out-Null
    $fixtureImage = Join-Path $fixtureApp 'KillerScan.exe'
    [IO.File]::WriteAllText($fixtureImage, 'A fixture placeholder that must never be launched')
    $failed = $false
    try { & $UninstallScript } catch { $failed = $true }
    if (-not $failed -or $global:KsDeploymentFixtureReads -ne 4 -or -not [IO.File]::Exists($fixtureImage)) {
        throw 'An image without completion registration was removed or falsely reported success.'
    }
    Write-Output 'PASS an image without registration is preserved and requires repair.'

    # 1.9.0 and later register UninstallCompletion.Command(machine: true, silent: true): the System32
    # Windows PowerShell with -NoLogo -NoProfile -NonInteractive -Command and one double-quoted
    # bootstrap that holds no other double quote. The stand-in bootstrap removes the fixture image,
    # the way the real completion helper removes the installed one.
    $powershell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    # A literal fixture path: a child Windows PowerShell recomputes ProgramFiles, so the variable
    # would point at the real installation.
    $removal = "[IO.File]::Delete('" + $fixtureImage.Replace("'", "''") + "');exit 0"
    $global:KsDeploymentFixtureHasRegistration = $true
    $global:KsDeploymentFixtureCommand = '"' + $powershell + '" -NoLogo -NoProfile -NonInteractive -Command "' + $removal + '"'
    & $UninstallScript
    if ([IO.File]::Exists($fixtureImage)) { throw 'The registered -Command completion helper did not run.' }
    Write-Output 'PASS the -Command registration that 1.9.x writes runs its completion helper.'

    [IO.File]::WriteAllText($fixtureImage, 'A fixture placeholder that must never be launched')
    foreach ($bad in @(
        ('"' + $powershell + '" -NoLogo -NoProfile -Command "' + $removal + '"'),
        ('"C:\Elsewhere\powershell.exe" -NoLogo -NoProfile -NonInteractive -Command "' + $removal + '"'),
        ('"' + $powershell + '" -NoLogo -NoProfile -NonInteractive -Command "' + $removal + '" -File evil.ps1'),
        ('"' + $powershell + '" -NoLogo -NoProfile -NonInteractive -Command "' + $removal))) {
        $global:KsDeploymentFixtureCommand = $bad
        $failed = $false
        try { & $UninstallScript } catch { $failed = $true }
        if (-not $failed -or -not [IO.File]::Exists($fixtureImage)) { throw "A malformed registration ran: $bad" }
    }
    $global:KsDeploymentFixtureCommand = $null
    Write-Output 'PASS registrations with another host, other switches or extra arguments are refused.'
}
finally {
    $env:ProgramFiles = $originalProgramFiles
    if ($fixtureImage -and [IO.File]::Exists($fixtureImage)) { [IO.File]::Delete($fixtureImage) }
    if ($fixtureApp -and [IO.Directory]::Exists($fixtureApp)) { [IO.Directory]::Delete($fixtureApp, $false) }
    [IO.Directory]::Delete($fixtureRoot, $false)
}
