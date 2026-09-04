# ============================================================================
#  KillerShell prompt
#  https://killershell.net
#
#  This is YOUR copy. KillerShell unpacked it once and will never overwrite it,
#  so edit it freely - a later version drops its own beside it as
#  KillerPrompt.default.ps1 rather than replacing this file.
#
#  It is dot-sourced by KillerShell AFTER your $PROFILE has run, only in shells
#  KillerShell starts. Your $PROFILE file is never modified, and no other
#  terminal sees any of this.
#
#  Escape hatches:
#    Restore-Prompt        put your own prompt back, right now, no restart
#    $env:KS_PROMPT = 0    set before launching KillerShell to skip this
#                          entirely
#
#  Written for PowerShell 5.1 as well as 7, so no backtick-e escapes and no
#  $PSStyle - neither exists on 5.1.
# ============================================================================

# Keep whatever prompt was in force when this loaded: normally your profile's,
# or PowerShell's own if you have not set one. Restore-Prompt hands it back.
if (-not $script:KFSavedPrompt) {
    $script:KFSavedPrompt = $function:prompt
}

function Restore-Prompt {
    <#  .SYNOPSIS  Put back the prompt that was in force before KillerShell's.  #>
    if ($script:KFSavedPrompt) {
        Set-Item -Path function:prompt -Value $script:KFSavedPrompt
        Write-Host 'Your prompt is back. Open a new terminal for the KillerShell one.' -ForegroundColor DarkGray
    }
}

$script:KFEsc = [char]27

# Cleared by the first prompt. See the blank-line note in the prompt function.
$script:KFFirstPrompt = $true

# The mark you type at, and the separator between segments. Single glyphs, so
# they are easy to swap - a Nerd Font gives you far more choice here than the
# stock console fonts do.
$script:KFMark = [char]0x276F      # heavy right angle
$script:KFSep  = [char]0xE0B0      # powerline right arrow (needs a Nerd Font)

# ── Theme ───────────────────────────────────────────────────────────────────
# KillerShell writes its live palette to the file named by $env:KS_STATE and
# rewrites it whenever you change theme or accent. Re-reading it each prompt is
# what lets an ALREADY OPEN shell recolor the moment the window does - the
# KS_THEME environment variable cannot do that, because a child process only
# ever gets a copy of the environment as it was at launch.
#
# Cached on the file's timestamp so the common case is a stat, not a read.
$script:KFPalette = $null
$script:KFStamp   = [datetime]::MinValue

function script:KFState {
    $path = $env:KS_STATE
    # A palette file can be briefly unavailable while KillerShell refreshes or cleans up
    # session state. Keep the last complete palette rather than flashing the red fallback.
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $script:KFPalette }

    try {
        $stamp = (Get-Item -LiteralPath $path).LastWriteTimeUtc
        if ($script:KFPalette -and $stamp -eq $script:KFStamp) { return $script:KFPalette }

        $map = @{}
        foreach ($line in [System.IO.File]::ReadAllLines($path)) {
            $eq = $line.IndexOf('=')
            if ($eq -gt 0) { $map[$line.Substring(0, $eq)] = $line.Substring($eq + 1) }
        }
        $script:KFPalette = $map
        $script:KFStamp   = $stamp
        return $map
    } catch {
        # A half-written file or a locked one is not worth an error in a prompt.
        return $script:KFPalette
    }
}

# Truecolor SGR from a #rrggbb string. KillerShell's renderer handles 38;2, so
# the prompt can use the window's exact colors rather than approximating them
# with one of sixteen ANSI slots.
function script:KFColor([string]$hex, [switch]$Background) {
    if (-not $hex -or $hex.Length -lt 7) { return '' }
    try {
        $r = [convert]::ToInt32($hex.Substring(1, 2), 16)
        $g = [convert]::ToInt32($hex.Substring(3, 2), 16)
        $b = [convert]::ToInt32($hex.Substring(5, 2), 16)
    } catch { return '' }

    $lead = '38'
    if ($Background) { $lead = '48' }
    return "$($script:KFEsc)[$lead;2;$r;$g;$($b)m"
}

function script:KFRole([string]$role, [string]$fallback, [switch]$Background) {
    $state = script:KFState
    $hex = $fallback
    if ($state -and $state[$role]) { $hex = $state[$role] }
    return (script:KFColor $hex -Background:$Background)
}

# ── Path ────────────────────────────────────────────────────────────────────
# Home becomes ~, and anything deeper than KFPathKeep segments loses its middle
# to single letters: C:\U\s\code\KillerShell\shell-landing. The tail is what you
# are actually looking at, and a full path long enough to wrap costs you the
# whole line. 3, not 2 (2026-08-03): 2 abbreviated "code" down to "c" in
# ~\code\KillerShell\shell-landing, which read as one segment too aggressive.
$script:KFPathKeep = 3

function script:KFPath {
    $path = (Get-Location).Path
    $home_ = $HOME
    if ($home_ -and $path.StartsWith($home_, [StringComparison]::OrdinalIgnoreCase)) {
        $path = '~' + $path.Substring($home_.Length)
    }

    $parts = $path.Split([char]92)          # backslash
    if ($parts.Count -le ($script:KFPathKeep + 1)) { return $path }

    $out = @()
    for ($i = 0; $i -lt $parts.Count; $i++) {
        $seg = $parts[$i]
        if ($i -ge $parts.Count - $script:KFPathKeep -or $seg.Length -le 1 -or $seg.EndsWith(':')) {
            $out += $seg
        } else {
            $out += $seg.Substring(0, 1)
        }
    }
    return ($out -join [char]92)
}

# ── Git ─────────────────────────────────────────────────────────────────────
# One `git status` call gives branch, ahead/behind and dirtiness together, so a
# repo costs a single process rather than three. Outside a repo it costs
# nothing: the walk up the tree for a .git is a few file checks and the result
# is cached per directory, so `git` is never even launched.
$script:KFRepoCache = @{}

function script:KFInRepo {
    $dir = (Get-Location).Path
    if ($script:KFRepoCache.ContainsKey($dir)) { return $script:KFRepoCache[$dir] }

    $found = $false
    $probe = $dir
    while ($probe) {
        if (Test-Path -LiteralPath (Join-Path $probe '.git')) { $found = $true; break }
        $parent = Split-Path -Path $probe -Parent
        if ($parent -eq $probe) { break }
        $probe = $parent
    }
    $script:KFRepoCache[$dir] = $found
    return $found
}

function script:KFGit {
    if (-not (script:KFInRepo)) { return $null }

    try {
        $lines = @(git status --porcelain=v1 --branch --untracked-files=normal 2>$null)
    } catch { return $null }
    if (-not $lines -or $lines.Count -eq 0) { return $null }

    # First line is "## branch...remote [ahead 1, behind 2]"; the rest are changes.
    $head = $lines[0]
    $branch = $head -replace '^## ', '' -replace '\.\.\..*$', ''
    $branch = $branch -replace ' \[.*$', ''
    if ($branch -like 'HEAD*') { $branch = 'detached' }

    $ahead = 0; $behind = 0
    if ($head -match 'ahead (\d+)')  { $ahead  = [int]$matches[1] }
    if ($head -match 'behind (\d+)') { $behind = [int]$matches[1] }

    return @{
        Branch = $branch
        Dirty  = ($lines.Count -gt 1)
        Ahead  = $ahead
        Behind = $behind
    }
}

# ── Duration ────────────────────────────────────────────────────────────────
# Only past a threshold. A time next to every `cd` is noise; a time next to the
# build you just sat through is the number you wanted.
$script:KFSlowMs = 2000

function script:KFDuration {
    $last = Get-History -Count 1 -ErrorAction SilentlyContinue
    if (-not $last -or -not $last.EndExecutionTime) { return $null }

    $ms = ($last.EndExecutionTime - $last.StartExecutionTime).TotalMilliseconds
    if ($ms -lt $script:KFSlowMs) { return $null }

    if ($ms -lt 60000) { return ('{0:0.0}s' -f ($ms / 1000)) }
    return ('{0:0}m{1:00}s' -f [math]::Floor($ms / 60000), [math]::Floor(($ms % 60000) / 1000))
}

# ── The prompt ──────────────────────────────────────────────────────────────
function prompt {
    # Read BOTH before anything else runs: any command in this function resets
    # them, and then the mark could never go red.
    $ok   = $?
    $code = $LASTEXITCODE

    $accent = script:KFRole 'ACCENT' '#e8485a'
    $fg     = script:KFRole 'FG'     '#fffde8'
    $muted  = script:KFRole 'MUTED'  '#f8c99e'
    $dim    = script:KFRole 'DIM'    '#e2b58a'
    $ok_    = script:KFRole 'OK'     '#5cb85c'
    $warn   = script:KFRole 'WARN'   '#e8b45c'
    $onAcc  = script:KFRole 'ACCENT' '#e8485a' -Background
    $reset  = "$($script:KFEsc)[0m"

    # The accent block reads as the wordmark: bone type on blood, closed off by
    # the arrow. The arrow is drawn in the accent as FOREGROUND on the normal
    # background, which is what makes it look like the block tapering off.
    $line = $onAcc + $fg + ' ' + (script:KFPath) + ' ' + $reset + $accent + $script:KFSep + $reset

    $git = script:KFGit
    if ($git) {
        $branchColor = $ok_
        if ($git.Dirty) { $branchColor = $warn }

        $line += ' ' + $branchColor + [char]0xE0A0 + ' ' + $git.Branch + $reset
        if ($git.Dirty)      { $line += $warn + ' ' + [char]0x00B1 + $reset }
        if ($git.Ahead -gt 0)  { $line += $dim + ' ' + [char]0x2191 + $git.Ahead + $reset }
        if ($git.Behind -gt 0) { $line += $dim + ' ' + [char]0x2193 + $git.Behind + $reset }
    }

    $took = script:KFDuration
    if ($took) { $line += $dim + '  ' + $took + $reset }

    if ($env:KS_ADMIN -eq '1') { $line += $accent + '  [ADMIN]' + $reset }

    # Two lines, so what you type always starts at the same column no matter how
    # deep the folder or how long the branch name.
    #
    # The blank one separates this block from the output above it - except on the
    # FIRST prompt, where there is no output above it and the gap is just a wasted
    # row at the top of a fresh shell. It used to be hidden by the banner sitting
    # in it; with the banner gone it was the first thing you saw.
    if ($script:KFFirstPrompt) { $script:KFFirstPrompt = $false } else { Write-Host '' }
    Write-Host $line

    # The mark carries the exit code, which is the fastest possible read of
    # "did that work". $? covers native failures too, where $LASTEXITCODE alone
    # would still be 0 from some earlier command.
    $markColor = $accent
    if (-not $ok -or ($code -ne $null -and $code -ne 0)) { $markColor = $warn }

    # $LASTEXITCODE has to be put back: reading it above did not change it, but
    # Get-History and git did, and a prompt that eats the exit code of your last
    # command is a prompt that breaks `if ($LASTEXITCODE)` in every script.
    $global:LASTEXITCODE = $code

    return $markColor + $script:KFMark + ' ' + $reset
}

# ── Banner ──────────────────────────────────────────────────────────────────
# Only when it says something you cannot already see.
#
# It used to open every shell with the app name, its version and the theme. All
# three are on screen already - the wordmark is in the title bar, the version is
# on the About card, and the theme is the colors you are looking at - so the line
# was spending the first row of every terminal restating them.
#
# Elevation is the exception and the reason this is not simply deleted: getting
# it wrong is the one mistake a shell can make that costs you something, and the
# tab's shield glyph is small and easy to miss halfway down a build log.
#
# KS_BANNER=0 switches even that off.
function script:KFBanner {
    if ($env:KS_ADMIN -ne '1') { return }

    $accent = script:KFRole 'ACCENT' '#e8485a'
    $reset  = "$($script:KFEsc)[0m"

    Write-Host ($accent + 'escalated privileges' + $reset)
}

if ($env:KS_BANNER -ne '0') { script:KFBanner }
