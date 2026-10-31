# Deployment and removal

KillerScan installs one application image. Per-user installations use %LOCALAPPDATA%\Programs\KillerScan; all-users installations use %ProgramFiles%\KillerScan. Cleanup removes only the owned image and KillerScan shortcuts. Unrelated files and nested folders are retained, and cleanup reports their directory. Linked installation paths are rejected. Settings remain available after removal or migration.

## Silent uninstall

Read QuietUninstallString from the installation's Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan key in HKCU or HKLM. Execute that command as registered and wait for its process to exit. Start it directly, not through `cmd /c`, to preserve its quoting. It uses the fixed Windows PowerShell executable with an immutable command, prepares a verified copy of the installed helper, waits for removal and deletes the stopped helper before returning success.

Chocolatey's uninstall script uses this registered command and checks that the installed image is gone. If the registration is missing or uses an older completion protocol, repair or update the installation before retrying.

A registered installation whose image is unavailable also fails and requires repair. Missing files or denied registry access cannot be treated as proof of completed removal.

Calling the loaded installed image with /uninstall-silent returns 1618 without changing files or registration. A loaded executable cannot wait for its own exit and truthfully report its later deletion. Deployment callers must use the registered completion command instead.

| Exit code | Meaning |
| --- | --- |
| 0 | Owned removal and completion-helper cleanup finished. Unrelated files may remain. |
| 1 | Removal or completion failed. Inspect the reported error and retained paths. |
| 5 | Required file or registry access was denied. |
| 1223 | The user canceled confirmation or elevation. |
| 1618 | Another deployment or running app blocks removal, or a required parent did not exit. Retry after it finishes. |
| 1625 | The trusted operation rejected an invalid security request. |

When owned-image removal or registration changes fail inside the removal transaction, the helper attempts to restore both the image and captured registration. Failed restoration reports the retained image paths. Do not delete those files until the installation has been recovered. A nonzero result never counts as completed removal.

The interactive registered command keeps the uninstall confirmation. The legacy /uninstall entry point confirms, starts the completion owner and exits so cleanup can continue. Automation must use QuietUninstallString to obtain the final outcome.

## Elevated updates

Automatic elevated updates run only through the protected all-users installation. The installed helper validates its directory ownership, parent process identity, retained SHA-256, product name, requested newer assembly version and signing certificate. It copies the download into protected staging and verifies those protected bytes before publication. The publication command contains immutable code and waits for the exact parent/helper instances to exit.

The incoming release must have a valid signature from the installed app's publisher; a renewed certificate from that publisher is accepted. An unsigned installation, an untrusted installation directory or a portable copy in a folder its user cannot write uses the manual download path instead. A per-user or portable copy that is running as administrator but can write its own folder updates through the per-user helper, not the protected one. A canceled or rejected handoff preserves the downloaded file until a later update sweeps downloads more than an hour old.

Until the handoff completes, the original download has a recovery report naming the protected staging and report locations. Once the protected helper has staged its verified copy, the download and that report are deleted. A completed handoff means the publication helper started; actual publication can still fail afterward. In that case the helper reopens the installed copy as the signed-in user at normal rights and keeps its report beside the staged file in the installation folder. It uses the desktop shell's token when available, with a one-time scheduled task as the fallback when the helper runs under another account. Retain that report when investigating an update.

## Constrained Language Mode

Every helper that completes an update or an uninstall is a Windows PowerShell command: the per-user update swap, the protected update publication, the registered UninstallString and QuietUninstallString, and the Chocolatey uninstall script that calls them. These commands create .NET objects such as streams, mutexes, registry keys and processes. Where AppLocker or Windows Defender Application Control enforces script rules, or `__PSLockdownPolicy` is set, Windows PowerShell runs in Constrained Language Mode and refuses those objects. On such a machine, automatic updates fail with a PowerShell error, and the registered uninstall fails from Settings, from Chocolatey and from deployment tools.

A publisher rule for the KillerScan signing certificate does not help, because these commands are PowerShell command lines, not signed script files. This limitation stays until completion moves into the signed executable. Installing with `KillerScan.exe /silent` does not use these helpers. To update on such a machine, run the new release's `KillerScan.exe /silent` as administrator. To remove an all-users installation by hand:

1. Close KillerScan for every signed-in user.
2. Delete `%ProgramFiles%\KillerScan`.
3. Delete the registry keys `HKLM\Software\KillerScan` and `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan`.
4. Delete the `KillerScan` folder from the all-users Start menu, `%ProgramData%\Microsoft\Windows\Start Menu\Programs`.
5. Remove `%ProgramFiles%\KillerScan` from the system `Path` if it is listed there.

## Validation boundary

Controlled filesystem, process-boundary and generated-command checks cover the implementation. Actual machine/user installation, UAC cancellation, another administrator account, denied registry access, read-only/disconnected storage, concurrent deployment and payload/input tampering require the coordinated disposable Windows VM. They are not established by a passing build or command parser.
