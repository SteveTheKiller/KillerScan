[CmdletBinding()]
param()

# Runs the PR workflow's suite-inventory step the way GitHub runs a pwsh step: its script lines in a
# fresh process with no profile. A step that throws after every check passed fails every PR.
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$source = Get-Content -LiteralPath (Join-Path $repo '.github/workflows/pr-build-test.yml') -Raw
$start = $source.IndexOf('      - name: Check the suite inventory and the PowerShell module contract')
if ($start -lt 0) { throw 'The suite inventory step is missing from pr-build-test.yml.' }
$runAt = $source.IndexOf('        run: |', $start)
$moduleAt = $source.IndexOf('          $exe = ', $runAt)
if ($runAt -lt 0 -or $moduleAt -le $runAt) { throw 'The suite inventory step boundary changed.' }
# Only the lines before the module contract run here; that part needs a built Release exe.
$body = $source.Substring($runAt + '        run: |'.Length, $moduleAt - $runAt - '        run: |'.Length) -replace '(?m)^          ', ''
$step = Join-Path ([IO.Path]::GetTempPath()) ('KillerScan-pr-step-' + [guid]::NewGuid().ToString('N') + '.ps1')
try {
    Set-Content -LiteralPath $step -Value $body -Encoding utf8
    Push-Location $repo
    try { & pwsh -NoProfile -NonInteractive -Command ". '$step'" *> $null; $code = $LASTEXITCODE }
    finally { Pop-Location }
    if ($code -ne 0) { throw "The suite inventory step failed with exit $code although its checks are meant to pass." }
}
finally { Remove-Item -LiteralPath $step -ErrorAction SilentlyContinue }
Write-Output 'PASS: the PR suite inventory step exits 0 when its checks pass.'
exit 0
