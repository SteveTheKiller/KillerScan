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
    [pscustomobject]@{ QuietUninstallString = 'A registered application with an unavailable image' }
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
}
finally {
    $env:ProgramFiles = $originalProgramFiles
    if ($fixtureImage -and [IO.File]::Exists($fixtureImage)) { [IO.File]::Delete($fixtureImage) }
    if ($fixtureApp -and [IO.Directory]::Exists($fixtureApp)) { [IO.Directory]::Delete($fixtureApp, $false) }
    [IO.Directory]::Delete($fixtureRoot, $false)
}
