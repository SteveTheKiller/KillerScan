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

# Run silent installer -- installs to Program Files, All Users start menu, HKLM registry
$exe = Join-Path $toolsDir 'KillerScan.exe'
Start-Process -FilePath $exe -ArgumentList '/silent' -Wait -NoNewWindow
