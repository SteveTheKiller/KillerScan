$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:ProgramFiles 'KillerScan'
$installExe = Join-Path $installDir 'KillerScan.exe'

$key = $null
try {
    $key = Get-ItemProperty -LiteralPath 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan' -ErrorAction Stop
}
catch [Management.Automation.ItemNotFoundException] { }

if ($null -ne $key) {
    if (-not (Test-Path -LiteralPath $installExe)) {
        throw 'KillerScan is registered but its installed image is unavailable. Repair the installation before retrying.'
    }
    $powershell = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
    $prefix = '"' + $powershell + '" -NoLogo -NoProfile -NonInteractive -EncodedCommand '
    $command = [string]$key.QuietUninstallString
    if (-not $command.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'KillerScan has no completion-owning uninstall registration. Repair the installation before retrying.'
    }
    $payload = $command.Substring($prefix.Length)
    if ($payload -notmatch '^[A-Za-z0-9+/]+={0,2}$') { throw 'The registered uninstall command is invalid.' }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $powershell
    $start.Arguments = '-NoLogo -NoProfile -NonInteractive -EncodedCommand ' + $payload
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $uninstaller = [Diagnostics.Process]::Start($start)
    if ($null -eq $uninstaller) { throw 'KillerScan uninstall did not start.' }
    try {
        $uninstaller.WaitForExit()
        if ($uninstaller.ExitCode -ne 0) { throw "KillerScan uninstall failed with exit code $($uninstaller.ExitCode)." }
        if (Test-Path -LiteralPath $installExe) { throw 'KillerScan uninstall returned success but its installed image remains.' }
    }
    finally { $uninstaller.Dispose() }
}
elseif (Test-Path -LiteralPath $installExe) {
    throw 'KillerScan has no completion-owning uninstall registration. Repair the installation before retrying.'
}
