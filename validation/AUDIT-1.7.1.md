# KillerScan 1.7.1 maintenance audit

Audited September 8, 2026. Product version 1.7.1; file and assembly versions 1.7.1.0. The released baseline is 1.7.0. This is a development audit, not a signed release or a guarantee that every environment is defect-free.

## Scope

Reviewed installation, replacement, update download and checksum handling, uninstall, package scripts, startup arguments, clipboard operations, external process launches, scan target validation, scan cancellation, saved profiles/history, report exports, HTTP probing, and dependency vulnerabilities. No production installation, user settings, or real network targets were modified by the regression checks.

## Findings fixed

- Direct executable overwrites could fail against a running copy or leave incomplete output after a failed write. Installation now stages and flushes the payload before replacement. Updates stage and compare the payload before replacing the destination.
- Install failures were conflated with canceled elevation, and per-user failure could still relaunch the old executable. Actual errors now reach the caller; failure does not trigger relaunch.
- Quiet uninstall registration opened a confirmation dialog. Both installation scopes now register `/uninstall-silent`, and uninstall honors it.
- Unguarded device clipboard calls could crash when another application held the clipboard. MAC, IP, and hostname copying retries and reports persistent failure in all 15 locales.
- Failed browser, RDP, SSH, and website launches could escape UI handlers. These failures are now contained.
- Duplicate MAC identities caused history dictionary construction to throw. Comparison now groups identities; loading filters null records and normalizes missing collections. Saved profiles tolerate null records and fields.
- Device and service CSV exports failed to escape quotes. Shared field escaping now preserves quoted names, commas, and newlines.
- HTTP fingerprinting buffered unrestricted responses. It now caps each response at 256 KiB.
- Chocolatey downloaded the executable without invoking installation. Its scripts now run the silent installer/uninstaller, check exit codes, and exclude the downloaded setup executable from automatic shimming. See [Chocolatey shimming documentation](https://docs.chocolatey.org/en-us/features/shim/).

## Verification

Debug and Release builds passed with zero warnings and errors. All 15 locale dictionaries passed key and placeholder coverage. Dependency vulnerability scanning reported no known vulnerable packages from the configured sources.

An external .NET Framework harness invoked the built application's actual methods. It used disposable files, a process-local redirected registry, and a loopback HTTP server:

| Check | Result |
| --- | --- |
| Fresh install, upgrade, same-source/destination install | Passed |
| Replace an executable while its previous process remains running | Passed |
| Locked destination reports failure and preserves original bytes | Passed |
| Complete per-user install, shortcuts, version and uninstall registration | Passed in isolated registry |
| Silent uninstall removes isolated installation without a dialog | Passed |
| Real user PATH unchanged after install/uninstall tests | Passed |
| Actual updater replaces disposable host and launches exact new payload | Passed, SHA-256 matched |
| Persistent clipboard lock reproduces original exception; fixed handler survives | Passed |
| Null history and repeated MAC identities | Passed |
| Normal and oversized HTTP responses | Passed |
| CSV quotes, commas, and newlines | Passed |
| CLI version and help | Passed |
| Invalid CLI target | Rejected with exit code 2 |

Local harness: `C:\Users\steve\killerpdf-benchmark\killerscan-bugfix-check\Check.csproj`. Update payload harness: `C:\Users\steve\killerpdf-benchmark\killerscan-update-payload\Payload.csproj`.

## Remaining verification and publication

- An actual elevated all-users install and UAC cancellation were not exercised. The shared file replacement and per-user registration paths were tested; that does not certify elevation on every machine.
- The published WinGet 1.7.0 manifest still declares `InstallerType: portable`. A corrected local manifest passed `winget validate` at `C:\Users\steve\killerpdf-benchmark\killerscan-winget-audit`. It has not been submitted upstream. The existing release workflow inherits upstream metadata, so this correction must land before relying on automatic submission for 1.7.1.
- Chocolatey scripts passed syntax checks; a Chocolatey-managed install was not executed.
- Real-device SSH, SNMP, topology accuracy, remote-desktop clipboard behavior, and the existing hands-on UI queue remain environment-dependent checks in the shared backlog.
- Code signing, release date finalization, release publication, and website deployment have not been performed by this audit.
