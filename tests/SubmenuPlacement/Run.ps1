[CmdletBinding()]
param([string]$OutputDirectory, [string]$SourcePath, [switch]$Smoke, [switch]$Windowless)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$name = Split-Path -Leaf $repo
$target = if ($name -in @('KillerNotes', 'KillerShell', 'KillerScan', 'Killendar', 'KillerUI')) { 'net48' } else { 'net10.0-windows' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('submenu-' + [Guid]::NewGuid().ToString('N')) }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh output directory.' }
$fixture = Join-Path $OutputDirectory 'fixture'
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$project = @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFrameworks>net48;net10.0-windows</TargetFrameworks><UseWPF>true</UseWPF><Nullable>enable</Nullable><LangVersion>latest</LangVersion></PropertyGroup>
  <ItemGroup Condition="'$(TargetFramework)' == 'net48'"><Reference Include="System.Web.Extensions" /></ItemGroup>
</Project>
'@
[IO.File]::WriteAllText((Join-Path $fixture 'Submenu.csproj'), $project)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Program.cs.txt') -Destination (Join-Path $fixture 'Program.cs')
try {
    dotnet build (Join-Path $fixture 'Submenu.csproj') --framework $target --nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'Submenu fixture build failed.' }
    $arguments = @($repo, $OutputDirectory)
    if ($SourcePath) { $arguments += $SourcePath }
    elseif ($Smoke -or $Windowless) {
        $source = if ($name.StartsWith('KillerPDF')) { 'MainWindow.xaml' } elseif ($name -eq 'KillerScan') { 'App.xaml' } else { 'Controls\Controls.xaml' }
        $arguments += (Join-Path $repo $source)
    }
    if ($Smoke) { $arguments += '--smoke' }
    if ($Windowless) { $arguments += '--windowless' }
    if ($target -eq 'net48') { & (Join-Path $fixture 'bin\Debug\net48\Submenu.exe') @arguments }
    else { dotnet (Join-Path $fixture 'bin\Debug\net10.0-windows\Submenu.dll') @arguments }
    if ($LASTEXITCODE -ne 0) { throw ('Submenu regression failed. See ' + (Join-Path $OutputDirectory 'result.json')) }
} finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $boundary = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup boundary failed.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
