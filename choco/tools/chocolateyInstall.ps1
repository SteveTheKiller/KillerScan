$ErrorActionPreference = 'Stop'
$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$version  = $env:ChocolateyPackageVersion

$packageArgs = @{
    packageName    = $env:ChocolateyPackageName
    fileFullPath   = Join-Path $toolsDir 'KillerScan.exe'
    url64bit       = "https://github.com/SteveTheKiller/KillerScan/releases/download/v$version/KillerScan.exe"
    checksum64     = 'REPLACE_HASH'
    checksumType64 = 'sha256'
}

Get-ChocolateyWebFile @packageArgs

$installer = Start-Process -FilePath $packageArgs.fileFullPath -ArgumentList '/silent' -Wait -PassThru -WindowStyle Hidden
if ($installer.ExitCode -ne 0) { throw "KillerScan installation failed with exit code $($installer.ExitCode)." }
New-Item -ItemType File -Path ($packageArgs.fileFullPath + '.ignore') -Force | Out-Null
