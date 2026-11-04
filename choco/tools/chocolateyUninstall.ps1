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
    # KillerScan registers its completion helper as the System32 Windows PowerShell with these
    # switches and one double-quoted -Command bootstrap that holds no other double quote
    # (UninstallCompletion.Command). Anything else is refused rather than run.
    $switches = '-NoLogo -NoProfile -NonInteractive -Command "'
    $prefix = '"' + $powershell + '" ' + $switches
    $command = [string]$key.QuietUninstallString
    if (-not $command.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'KillerScan has no completion-owning uninstall registration. Repair the installation before retrying.'
    }
    $bootstrap = $command.Substring($prefix.Length)
    if ($bootstrap.Length -lt 2 -or -not $bootstrap.EndsWith('"') -or $bootstrap.Substring(0, $bootstrap.Length - 1).Contains('"')) {
        throw 'The registered uninstall command is invalid.'
    }
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $powershell
    $start.Arguments = $switches + $bootstrap
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
