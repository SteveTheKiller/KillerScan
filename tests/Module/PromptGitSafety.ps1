#Requires -Version 5.1
<#
    The terminal prompt shows git status for the folder it is in. A repository that is only
    being looked at (an extracted download, a cloned sample) must not get to run code it brings
    with it: hooks, an fsmonitor command, or a clean or process filter driver in its own
    .git/config. git runs a clean filter while refreshing the index whenever a tracked file's
    timestamps no longer match, which an extracted archive guarantees.

        powershell -NoProfile -File tests\Module\PromptGitSafety.ps1
        pwsh -NoProfile -File tests\Module\PromptGitSafety.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$prompt = Join-Path $root 'Terminal\KillerPrompt.ps1'
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('ks-prompt-git-' + [guid]::NewGuid().ToString('N'))
$repo = Join-Path $work 'repo'
$marker = Join-Path $work 'filter-ran.txt'
$passed = 0

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:passed++
}

# Runs the prompt's git probe in its own shell, the way the terminal loads it, and returns
# what it reported as JSON.
function Invoke-PromptGit([string]$Folder) {
    $host_ = if ($PSVersionTable.PSEdition -eq 'Core') { 'pwsh' } else { 'powershell' }
    $script = "`$env:KS_BANNER = '0'; Set-Location -LiteralPath '$Folder'; . '$prompt'; script:KFGit | ConvertTo-Json -Compress"
    & $host_ -NoProfile -NonInteractive -Command $script
}

try {
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    git -C $repo init -q
    git -C $repo config user.name 'Prompt test'
    git -C $repo config user.email 'prompt@example.invalid'
    Set-Content -LiteralPath (Join-Path $repo 'notes.txt') -Value 'one' -Encoding Ascii
    git -C $repo add notes.txt
    git -C $repo commit -q -m 'first'

    # A plain repository still shows its branch.
    $plain = Invoke-PromptGit $repo | ConvertFrom-Json
    Require ($null -ne $plain -and $plain.Branch -in @('master', 'main')) 'A repository without filter drivers shows its branch.'

    # Now the repository brings a clean filter that leaves a mark when it runs.
    $probe = Join-Path $work 'probe.cmd'
    Set-Content -LiteralPath $probe -Encoding Ascii -Value @('@echo ran> "' + $marker + '"', '@findstr "^"')
    Set-Content -LiteralPath (Join-Path $repo '.gitattributes') -Value '*.txt filter=probe' -Encoding Ascii
    git -C $repo config filter.probe.clean ('"' + ($probe -replace '\\', '/') + '"')
    # Same size, new timestamp: git has to re-hash the file, through the filter.
    (Get-Item -LiteralPath (Join-Path $repo 'notes.txt')).LastWriteTime = (Get-Date).AddMinutes(5)

    # The setup itself must be able to trip the filter, or the check below proves nothing.
    git -C $repo status --porcelain | Out-Null
    Require (Test-Path -LiteralPath $marker) 'The fixture filter runs under a plain git status.'
    Remove-Item -LiteralPath $marker
    (Get-Item -LiteralPath (Join-Path $repo 'notes.txt')).LastWriteTime = (Get-Date).AddMinutes(10)

    Invoke-PromptGit $repo | Out-Null
    Require (-not (Test-Path -LiteralPath $marker)) "The prompt does not run a repository's own clean filter."

    # The same filter, hidden where a plain look at .git/config does not see it: in a file the
    # repository's config includes, and in per-worktree config. git status reads both.
    git -C $repo config --unset filter.probe.clean
    $included = Join-Path $repo '.git\extra.config'
    Set-Content -LiteralPath $included -Encoding Ascii -Value @('[filter "probe"]', ("`tclean = `"" + ($probe -replace '\\', '/') + "`""))
    git -C $repo config include.path extra.config
    (Get-Item -LiteralPath (Join-Path $repo 'notes.txt')).LastWriteTime = (Get-Date).AddMinutes(15)
    Invoke-PromptGit $repo | Out-Null
    Require (-not (Test-Path -LiteralPath $marker)) "The prompt does not run a clean filter from an included config file."

    git -C $repo config --unset include.path
    git -C $repo config extensions.worktreeConfig true
    git -C $repo config --worktree filter.probe.clean ('"' + ($probe -replace '\\', '/') + '"')
    (Get-Item -LiteralPath (Join-Path $repo 'notes.txt')).LastWriteTime = (Get-Date).AddMinutes(20)
    Invoke-PromptGit $repo | Out-Null
    Require (-not (Test-Path -LiteralPath $marker)) "The prompt does not run a clean filter from worktree config."

    Write-Output "PASS: $passed prompt git safety checks on PowerShell $($PSVersionTable.PSVersion)."
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
