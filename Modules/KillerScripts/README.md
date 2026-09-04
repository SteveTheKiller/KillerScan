# KillerScripts

Always-latest launcher for the [killer-scripts](https://github.com/SteveTheKiller/killer-scripts) repo. Install it once and every tool is available as a command in any PowerShell session (5.1 and 7). Each tool is pulled fresh from GitHub the moment you run it, so you are always on the current version. A local cache under `%LOCALAPPDATA%\KillerScripts` is kept only as an offline fallback.

## Install

```powershell
Install-Module KillerScripts -Scope CurrentUser
```

## Use

```powershell
Get-KillerScript            # list every tool with its description
urt                         # run the Universal Rename Tool (latest from the repo)
Invoke-URT                  # same thing, long form
beret                       # BitLocker Encryption, Recovery & Escrow
Invoke-KillerScript URT     # generic form, tab-completes the name
Update-KillerScripts        # pre-download everything for offline use
```

Because these install as module commands, PowerShell auto-loads them. Typing `urt` in a fresh session just works, with no `Import-Module` and nothing in your profile.

Pass `-Offline` to `Invoke-KillerScript` (after an `Update-KillerScripts`) to run the cached copy without touching the network.

## Notes

- Tools run with the call operator, so they execute exactly as they do standalone, interactive prompts and `exit` included.
- PRINT.ps1 uses the alias `KillerPrint`, since a plain `print` alias would shadow `print.exe`. Short aliases are also only created when the name is free, so nothing built in is ever shadowed and the same guard covers any future clash.
- When you add or rename a script in the killer-scripts repo, run `Build-KillerScripts.ps1` to refresh the shipped manifest and export list and bump the version, then `Publish-Module`.
