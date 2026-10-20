[CmdletBinding()]
param([Parameter(Mandatory)][string]$ScratchRoot)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$root = Join-Path ([IO.Path]::GetFullPath($ScratchRoot)) ('source-bundle-' + [guid]::NewGuid().ToString('N') + ' ' + [char]0x00e9)
$null = New-Item -ItemType Directory -Path (Join-Path $root 'build') -Force
[xml]$appProject = Get-Content -LiteralPath (Join-Path $repo 'KillerScan.csproj') -Raw
$target = $appProject.SelectSingleNode('/Project/Target[@Name="BundleSource"]').OuterXml
if (-not $target) { throw 'The actual source-bundle Publish target is missing.' }
$template = @'
<Project>
  <PropertyGroup>
    <ProjectDir>$(MSBuildThisFileDirectory)</ProjectDir>
    <Version>9.9.9</Version><AssemblyName>BundleFixture</AssemblyName>
    <PublishDir>$(MSBuildThisFileDirectory)published\</PublishDir>
  </PropertyGroup>
  <Target Name="Publish">
    <MakeDir Directories="$(PublishDir)" />
    <Copy SourceFiles="$(ProjectDir)source.txt" DestinationFiles="$(PublishDir)app.bin" />
  </Target>
  __BUNDLE_TARGET__
</Project>
'@
[IO.File]::WriteAllText((Join-Path $root 'Fixture.proj'), $template.Replace('__BUNDLE_TARGET__', $target))
Copy-Item -LiteralPath (Join-Path $repo 'build/bundle-source.ps1') -Destination (Join-Path $root 'build/bundle-source.ps1')
[IO.File]::WriteAllText((Join-Path $root '.gitignore'), "published/`r`nobj/`r`n")
$source = Join-Path $root 'source.txt'
[IO.File]::WriteAllText($source, 'first source')
[IO.File]::WriteAllText((Join-Path $root 'deleted.txt'), 'deleted input')
$null = New-Item -ItemType Directory -Path (Join-Path $root 'scan-landing')
[IO.File]::WriteAllText((Join-Path $root 'scan-landing/index.html'), 'separate landing site')
$unicodeName = 'caf' + [char]0x00e9 + ' source.txt'
Push-Location $root
try {
    git init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Fixture repository initialization failed.' }
    git add -- .
    if ($LASTEXITCODE -ne 0) { throw 'Fixture tracking failed.' }
    Remove-Item -LiteralPath (Join-Path $root 'deleted.txt')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $root 'published/BundleFixture-9.9.9-src.zip'
    function Publish-Fixture {
        $output = dotnet msbuild Fixture.proj -t:Publish -nologo 2>&1 | Out-String
        $exit = $LASTEXITCODE
        return @{ Exit = $exit; Output = $output }
    }
    function Read-Source {
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $entry = $archive.GetEntry('source.txt')
            if (-not $entry) { throw 'The bundle omitted its source input.' }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
        } finally { $archive.Dispose() }
    }
    $first = Publish-Fixture
    if ($first.Exit -ne 0 -or -not (Test-Path -LiteralPath $zip)) { throw ('First publish failed: ' + $first.Output) }
    if ((Read-Source) -ne 'first source') { throw 'First publish bundled the wrong source.' }
    if ([IO.File]::ReadAllText($source) -ne 'first source') { throw 'Fixture source changed unexpectedly.' }
    [IO.File]::WriteAllText($source, 'second source')
    $second = Publish-Fixture
    if ($second.Exit -ne 0 -or (Read-Source) -ne 'second source' -or [IO.File]::ReadAllText((Join-Path $root 'published/app.bin')) -ne 'second source') {
        throw ('Same-version Publish must pair the new build with current tracked source. ' + $second.Output)
    }
    [IO.File]::WriteAllText((Join-Path $root $unicodeName), 'Unicode source')
    git add -- $unicodeName
    if ($LASTEXITCODE -ne 0) { throw 'Unicode fixture tracking failed.' }
    $unicode = Publish-Fixture
    if ($unicode.Exit -ne 0) { throw ('Unicode source publish failed: ' + $unicode.Output) }
    & (Join-Path $root 'build/bundle-source.ps1') -ProjectDir $root -Version 9.9.9 -AppName BundleFixture -PublishDir published | Out-Null
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        if (-not $archive.GetEntry($unicodeName)) { throw 'The source bundle omitted a tracked Unicode filename.' }
        if ($archive.GetEntry('deleted.txt') -or $archive.GetEntry('scan-landing/index.html')) { throw 'Deleted inputs or the separate landing site entered the source bundle.' }
    }
    finally { $archive.Dispose() }
    $before = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    $leftover = Join-Path $root 'published/unrelated.tmp.zip'
    [IO.File]::WriteAllText($leftover, 'preserve neighboring artifact')
    $locked = [IO.File]::Open($zip, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ([IO.File]::ReadAllText($source) -ne 'second source') { throw 'Fixture source changed unexpectedly.' }
        [IO.File]::WriteAllText($source, 'third source')
        $denied = Publish-Fixture
        if ($denied.Exit -eq 0) { throw 'Publish must fail when the matching source archive cannot be replaced.' }
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $before) { throw 'Denied replacement changed the prior complete source bundle.' }
    } finally { $locked.Dispose() }
    $retry = Publish-Fixture
    if ($retry.Exit -ne 0 -or (Read-Source) -ne 'third source') { throw ('Source-bundle replacement could not be retried: ' + $retry.Output) }
    if ([IO.File]::ReadAllText($leftover) -ne 'preserve neighboring artifact') { throw 'The bundler deleted an unrelated output artifact.' }
    $untracked = Join-Path $root 'no-repository'
    $null = New-Item -ItemType Directory -Path $untracked
    $discoveryRejected = $false
    try { & (Join-Path $root 'build/bundle-source.ps1') -ProjectDir $untracked -Version 9.9.9 -AppName BundleFixture -PublishDir published 2>&1 | Out-Null }
    catch { $discoveryRejected = $true }
    if (-not $discoveryRejected) { throw 'Expected source discovery to reject a directory with no tracked source.' }
    Write-Output 'PASS: source discovery failure is explicit.'
} finally { Pop-Location }
Write-Output 'PASS: repeated same-version Publish refreshes source, rejects locked output and preserves prior artifacts.'
Write-Output ('Evidence: ' + $root)
