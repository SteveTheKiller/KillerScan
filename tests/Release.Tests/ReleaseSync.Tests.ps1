[CmdletBinding()]
param([Parameter(Mandatory)][string]$ScratchRoot)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$workflow = Get-Content -LiteralPath (Join-Path $repo '.github/workflows/release-sync.yml') -Raw
$run = $workflow.IndexOf('        run: |')
if ($run -lt 0) { throw 'The monitor inline script is missing.' }
$body = (($workflow.Substring($run) -split '\r?\n' | Select-Object -Skip 1 | Where-Object { $_ -match '^          ' }) -replace '^          ', '') -join "`n"
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($body, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$http = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-HttpStatus' }, $true))[0]
if (-not $http) { throw 'The monitor HTTP boundary is missing.' }
$stub = 'function Get-HttpStatus([string]$url) { if ($url -match "api.nuget.org") { return $case.Http }; return 200 }'
$body = $body.Remove($http.Extent.StartOffset, $http.Extent.EndOffset - $http.Extent.StartOffset).Insert($http.Extent.StartOffset, $stub)
$monitor = [scriptblock]::Create($body)
$folder = Join-Path ([IO.Path]::GetFullPath($ScratchRoot)) ('release-sync-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $folder
$appProjectSource = Get-Content -LiteralPath (Join-Path $repo 'KillerScan.csproj') -Raw
$fixtureVersion = ([xml]$appProjectSource).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$engineProjectSource = Get-Content -LiteralPath (Join-Path $repo 'Engine/KillerScan.Engine.csproj') -Raw
$names = @('APP_NAME', 'PROJECT_PATH', 'ENGINE_PROJECT_PATH', 'EXPECTED_ASSETS', 'CHOCOLATEY_ID', 'WINGET_ID', 'WINGET_MANIFEST_ROOT', 'NUGET_ID', 'GITHUB_REPOSITORY', 'REQUESTED_VERSION', 'GITHUB_STEP_SUMMARY')
$original = @{}
try {
    foreach ($name in $names) {
        $original[$name] = [Environment]::GetEnvironmentVariable($name)
        $match = [regex]::Match($workflow, '(?m)^      ' + $name + ':[ \t]*([^\r\n]*)')
        [Environment]::SetEnvironmentVariable($name, $match.Groups[1].Value.Trim())
    }
    $env:GITHUB_REPOSITORY = 'fixture/KillerScan'; $env:REQUESTED_VERSION = ''
    foreach ($case in @(
        @{ Name = 'Published'; Http = 200; Age = 8; Run = 'none'; Expected = 'Published'; Blocks = $false },
        @{ Name = 'MissingOld'; Http = 404; Age = 8; Run = 'none'; Expected = 'Missing'; Blocks = $true },
        @{ Name = 'ReviewWindow'; Http = 404; Age = 1; Run = 'none'; Expected = 'Pending'; Blocks = $false },
        @{ Name = 'FailedPublishing'; Http = 404; Age = 1; Run = 'failure'; Expected = 'Failed'; Blocks = $true },
        @{ Name = 'Publishing'; Http = 404; Age = 1; Run = 'running'; Expected = 'Pending'; Blocks = $false },
        @{ Name = 'CompletedButMissing'; Http = 404; Age = 8; Run = 'success'; Expected = 'Missing'; Blocks = $true }
    )) {
        $env:GITHUB_STEP_SUMMARY = Join-Path $folder ($case.Name + '.md')
        function git {
            $global:LASTEXITCODE = 0
            if ($args[0] -eq 'cat-file') { return }
            if ($args[0] -eq 'show' -and $args[1] -like '*:KillerScan.csproj') { return $appProjectSource }
            if ($args[0] -eq 'show' -and $args[1] -like '*:Engine/KillerScan.Engine.csproj') { return $engineProjectSource }
            throw ('Unexpected git command: ' + ($args -join ' '))
        }
        function gh {
            $global:LASTEXITCODE = 0
            if ($args[0] -eq 'api') {
                return (@{ tag_name = 'v' + $fixtureVersion; published_at = [DateTimeOffset]::UtcNow.AddDays(-$case.Age).ToString('o'); assets = @(@{name='KillerScan.exe'}, @{name='SHA256SUMS.txt'}) } | ConvertTo-Json -Depth 5 -Compress)
            }
            if ($args[0] -eq 'run' -and $args[1] -eq 'list') {
                if ($case.Run -eq 'none') { return '[]' }
                return (@(@{ status = $(if ($case.Run -eq 'running') { 'in_progress' } else { 'completed' }); conclusion = $case.Run; url = 'https://example.invalid/fixture-run' }) | ConvertTo-Json -Compress)
            }
            throw ('Unexpected gh command: ' + ($args -join ' '))
        }
        $blocked = $false; $reason = ''
        try { . $monitor | Out-Null } catch { $blocked = $true; $reason = $_.Exception.Message + ' ' + $_.ScriptStackTrace }
        if (-not (Test-Path -LiteralPath $env:GITHUB_STEP_SUMMARY)) { throw ($case.Name + ': summary was not generated: ' + $reason) }
        $summary = Get-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Raw
        if ($summary -notmatch ('\| NuGet \| ' + $case.Expected + ' \|')) { throw ($case.Name + ': required NuGet row is missing or incorrect.') }
        if ($blocked -ne $case.Blocks) { throw ($case.Name + ': incorrect blocking outcome: ' + $reason) }
        if ($summary -match '\| Engine source \| Mismatch') { throw 'The monitor compared the unevaluated engine version expression.' }
        Write-Output ('PASS: ' + $case.Name)
    }
} finally { foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $original[$name]) } }
Write-Output ('Evidence: ' + $folder)
