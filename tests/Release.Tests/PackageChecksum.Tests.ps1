[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$expected = '03BC1239B777E97D76A1C884E2B2B4DE98D08EFF3FBCDE960064570A3D966355'
$base = 'https://example.invalid/release'
$body = "KillerScan.exe              $expected`r`n"
foreach ($workflow in 'chocolatey-release.yml', 'winget-release.yml') {
    $source = Get-Content -LiteralPath (Join-Path $repo ".github/workflows/$workflow") -Raw
    $start = $source.IndexOf('          $sums = ')
    $end = $source.IndexOf('          if ($listed -ne $hash)', $start)
    if ($start -lt 0 -or $end -le $start) { throw "Checksum boundary missing: $workflow" }
    $check = [scriptblock]::Create(($source.Substring($start, $end - $start) -replace '(?m)^          ', ''))
    foreach ($content in @($body, [Text.Encoding]::UTF8.GetBytes($body))) {
        function Invoke-WebRequest { [pscustomobject]@{ Content = $content } }
        $listed = $null
        . $check
        if ($listed -ne $expected) { throw "$workflow failed for $($content.GetType().Name) response content." }
    }
    Write-Output "PASS: $workflow reads textual and binary checksum responses."
}
