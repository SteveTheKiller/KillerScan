# Called automatically by the csproj after Publish.
# Produces <AppName>-<Version>-src.zip inside the publish folder.
# PS 5.1 / PS 7 compatible. Uses git to list tracked files so bin/obj/.vs never ship.
param(
    [Parameter(Mandatory)][string]$ProjectDir,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$AppName,
    [Parameter(Mandatory)][string]$PublishDir
)

$ErrorActionPreference = 'Stop'
if ($AppName -match '[\\/:*?"<>|]' -or $Version -match '[\\/:*?"<>|]') { throw 'Bundle name and version must be filename components.' }

$projectDirFull = (Resolve-Path -LiteralPath $ProjectDir).Path
$publishDirFull = if ([System.IO.Path]::IsPathRooted($PublishDir)) {
    $PublishDir
} else {
    Join-Path $projectDirFull $PublishDir
}
$publishDirFull = [IO.Path]::GetFullPath($publishDirFull)

if (-not (Test-Path $publishDirFull)) {
    New-Item -ItemType Directory -Force -Path $publishDirFull | Out-Null
}

$zip = Join-Path $publishDirFull "$AppName-$Version-src.zip"
$identity = [guid]::NewGuid().ToString('N')
$temporaryZip = Join-Path $publishDirFull "$AppName-$Version-src.$identity.tmp.zip"
$backup = $temporaryZip + '.original'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$staging = [IO.Path]::GetFullPath((Join-Path $temporaryRoot "$AppName-src-$identity"))
try {
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    $git = [Diagnostics.Process]::new()
    $git.StartInfo.FileName = 'git'
    $git.StartInfo.Arguments = '-c core.quotePath=false ls-files -z'
    $git.StartInfo.WorkingDirectory = $projectDirFull
    $git.StartInfo.UseShellExecute = $false
    $git.StartInfo.CreateNoWindow = $true
    $git.StartInfo.RedirectStandardOutput = $true
    $git.StartInfo.RedirectStandardError = $true
    $git.StartInfo.StandardOutputEncoding = [Text.Encoding]::UTF8
    $git.StartInfo.StandardErrorEncoding = [Text.Encoding]::UTF8
    try {
        $null = $git.Start()
        $output = $git.StandardOutput.ReadToEndAsync()
        $errorOutput = $git.StandardError.ReadToEndAsync()
        $git.WaitForExit()
        $gitExit = $git.ExitCode
        $fileList = $output.GetAwaiter().GetResult()
        $diagnostic = $errorOutput.GetAwaiter().GetResult()
        if ($gitExit -ne 0) { throw "Source discovery failed with exit ${gitExit}: $diagnostic" }
    } finally { $git.Dispose() }
    # The landing site is a separate deliverable and carries the release's executable hash.
    $files = @($fileList.Split([char]0) | Where-Object { $_.Length -gt 0 -and $_ -notlike 'scan-landing/*' })
    if ($files.Count -eq 0) { throw 'Source discovery returned no tracked files.' }
    foreach ($f in $files) {
        $source = Join-Path $projectDirFull $f
        # Tracked files deleted in the working tree are not inputs to this publish.
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
        $dst = Join-Path $staging $f
        $parent = Split-Path $dst -Parent
        if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
        Copy-Item -LiteralPath $source -Destination $dst -Force
    }
    $rootLicense = Join-Path $projectDirFull 'LICENSE'
    if (Test-Path -LiteralPath $rootLicense) { Copy-Item -LiteralPath $rootLicense -Destination (Join-Path $staging 'LICENSE') -Force }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $temporaryZip)
    $written = [IO.File]::Open($temporaryZip, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $written.Flush($true) } finally { $written.Dispose() }
    if ([IO.File]::Exists($zip)) {
        try { [IO.File]::Replace($temporaryZip, $zip, $backup) }
        catch {
            if (-not [IO.File]::Exists($zip) -and [IO.File]::Exists($backup)) { [IO.File]::Move($backup, $zip) }
            throw
        }
        if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
    } else { [IO.File]::Move($temporaryZip, $zip) }
    Write-Host "Source bundle: $zip" -ForegroundColor Green
} finally {
    if ([IO.File]::Exists($temporaryZip)) { [IO.File]::Delete($temporaryZip) }
    $allowedRoot = $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $staging.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source staging cleanup escaped the temporary root.' }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction Continue }
}
