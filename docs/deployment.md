# Deployment and removal

KillerScan installs one application image. Per-user installations use %LOCALAPPDATA%\Programs\KillerScan; all-users installations use %ProgramFiles%\KillerScan. Cleanup removes only the owned image and KillerScan shortcuts. Unrelated files and nested folders are retained, and cleanup reports their directory. Linked installation paths are rejected. Settings remain available after removal or migration.

## Silent uninstall

Read QuietUninstallString from the installation's Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan key in HKCU or HKLM. Execute that command as registered and wait for its process to exit. It uses the fixed Windows PowerShell executable with an immutable command, prepares a verified copy of the installed helper, waits for removal and deletes the stopped helper before returning success.

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

## Validation boundary

Controlled filesystem, process-boundary and generated-command checks cover the implementation. Actual machine/user installation, UAC cancellation, another administrator account, denied registry access, read-only/disconnected storage, concurrent deployment and payload/input tampering require the coordinated disposable Windows VM. They are not established by a passing build or command parser.
