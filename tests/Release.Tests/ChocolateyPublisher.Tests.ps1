[CmdletBinding()]
param()

# Chocolatey has one publisher: .github/workflows/chocolatey-release.yml, which runs when the
# release is published and checks the exe's checksum and signature first. If release.ps1 pushed
# as well, the second push of the same version would fail.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Join-Path $repo 'release.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }

$chocoCalls = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.CommandAst] -and
    $node.GetCommandName() -in 'choco', 'choco.exe'
}, $true))
if ($chocoCalls.Count) {
    throw ('release.ps1 runs choco itself: ' + (($chocoCalls | ForEach-Object { 'line ' + $_.Extent.StartLineNumber }) -join ', '))
}
$parameters = @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
foreach ($name in 'Choco', 'ChocoApiKey') {
    if ($parameters -contains $name) { throw "release.ps1 still takes -$name." }
}
Write-Output 'PASS: release.ps1 neither packs nor pushes the Chocolatey package.'

$workflow = Get-Content -Raw -LiteralPath (Join-Path $repo '.github\workflows\chocolatey-release.yml')
if ($workflow -notmatch '(?ms)^on:\s*\r?\n\s+release:\s*\r?\n\s+types:\s*\[published\]') {
    throw 'chocolatey-release.yml no longer runs when a release is published.'
}
if ($workflow -notmatch 'choco push \$package --source https://push\.chocolatey\.org') {
    throw 'chocolatey-release.yml no longer pushes the package.'
}
Write-Output 'PASS: the Chocolatey workflow publishes on release.'
