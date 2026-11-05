[CmdletBinding()]
param()

# The release publishes numbers: the vendor count it commits to CHANGELOG.md and the exe size it
# writes to the landing page. They must read the same whatever region the build machine uses.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Join-Path $repo 'release.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }

# No number is formatted with -f, which follows the current culture.
$cultureFormats = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.BinaryExpressionAst] -and
    $node.Operator -eq 'Format' -and
    $node.Left.Extent.Text -match '\{\d+(,-?\d+)?:[NnFfDd]'
}, $true))
if ($cultureFormats.Count) {
    throw ('release.ps1 formats numbers with the build machine''s culture: ' +
        (($cultureFormats | ForEach-Object { 'line ' + $_.Extent.StartLineNumber }) -join ', '))
}
Write-Output 'PASS: release.ps1 formats no number with -f.'

# Run the script's own helpers under a German culture, where 1,234.56 would print as 1.234,56.
$functions = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -in 'Fail', 'Format-Invariant', 'Get-OuiChangelogText'
}, $false))
if ($functions.Count -ne 3) { throw 'release.ps1 is missing Fail, Format-Invariant or Get-OuiChangelogText.' }
. ([scriptblock]::Create((($functions | ForEach-Object { $_.Extent.Text }) -join "`n")))

$culture = [System.Threading.Thread]::CurrentThread.CurrentCulture
try {
    [System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::GetCultureInfo('de-DE')
    $size = Format-Invariant '{0:N2} MB' (1234.567)
    if ($size -ne '1,234.57 MB') { throw "A size formats as '$size' under de-DE." }
    $log = "## [9.9.9] - Unreleased`n`n### Changed`n`n- Something.`n"
    $text = Get-OuiChangelogText -Text $log -ReleaseVersion '9.9.9' -EntryCount 39123
    if ($text -notmatch '(?m)^- Refreshed the bundled MAC vendor database to 39,123 assignments\.$') {
        throw "The vendor count line reads differently under de-DE:`n$text"
    }
} finally {
    [System.Threading.Thread]::CurrentThread.CurrentCulture = $culture
}
Write-Output 'PASS: sizes and the vendor count read the same under de-DE.'
