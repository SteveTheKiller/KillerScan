$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:ProgramFiles 'KillerScan'
$installExe = Join-Path $installDir 'KillerScan.exe'

if (Test-Path $installExe) {
    $uninstaller = Start-Process -FilePath $installExe -ArgumentList '/uninstall-silent' -Wait -PassThru -WindowStyle Hidden
    if ($uninstaller.ExitCode -ne 0) { throw "KillerScan uninstall failed with exit code $($uninstaller.ExitCode)." }
}
