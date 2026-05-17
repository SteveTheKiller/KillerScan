# Changelog

All notable changes to KillerScan are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.4.0] - 2026-05-16

### Added
- Portable mode detection: app now launches directly without a dialog; if running outside the install location a **PORTABLE** badge and **Install KillerScan...** button appear in the status bar.
- Custom themed install confirmation dialog replaces the system MessageBox.
- Film grain overlay on the results area background.
- `OutlineButton` style (green border, green text, transparent background) matching killertools.net aesthetics.

### Changed
- Version bumped to 1.4.0.
- Startup flow simplified: removed the launch dialog entirely; main window opens immediately.
- Scan button switched to outlined style to match killertools.net.
- TextBox background darkened to `#1c1c1c` with a visible `#3a3a3a` border so inputs are distinct from card surfaces.
- Network info bar now labels each value: `local:`, `gw:`, `dns:` prefixes in dim green.
- Column headers: reduced height, transparent background, dimmer text - cleaner minimal look.
- Config bar padding tightened; gap between config and results area reduced.
- DataGrid row selection color corrected from system blue to green (`#1a3a25` / `#1ea54c`).
- Scrollbar style updated to thin green accent matching killertools.net.

## [1.3.0] - 2026-04-25

### Added
- Self-installer: on first launch from outside the install location, a launcher dialog offers **Install** or **Run without installing**. Install copies the EXE to `%LOCALAPPDATA%\Programs\KillerScan\`, creates a Start Menu shortcut, and optionally a desktop shortcut.
- Registers in `HKCU\...\Uninstall\KillerScan` so the app appears in Windows Add/Remove Programs with a working uninstall entry.
- `KillerScan.exe /uninstall` flag for removal via Add/Remove Programs; self-deletes the install directory via a deferred batch script after exit.
- Re-running the EXE when already installed shows an **Update** prompt instead of Install.
- Hostname keyword short-circuits for `iphone`, `ipad`, and `android` in the device classifier.
- Expanded Android/Mobile OUI vendor list: Google, BBK Electronics (Vivo/OnePlus parent), Realme, Nothing Technology, Fairphone.

### Changed
- Version bumped to 1.3.0.
- Second ARP cache read added immediately after the ping sweep, so devices that block ICMP but respond to ARP (phones, tablets, some IoT) are caught without a separate scan pass.
- Apple device classification renamed from "Apple Device" to "iPhone"; port 62078 now scores toward iPhone regardless of OUI (catches randomized-MAC iDevices when USB-tethered).
- Android/Mobile vendor match threshold relaxed from `ports == 0` to `ports <= 3`.
- Randomized/locally-administered MAC fallback relaxed from `ports == 0` to `ports <= 3`.
- HTTP User-Agent updated to `KillerScan/1.3`.

## [1.2.1] - 2026-04-18

### Fixed
- Maximize no longer covers the Windows taskbar. Added a `WM_GETMINMAXINFO` hook so the frameless window clamps to the monitor's work area (multi-monitor aware).

## [1.2.0] - 2026-04-16

### Changed
- Retargeted from .NET 8 to .NET Framework 4.8 so end users no longer need to install a separate .NET runtime.
- Forced 64-bit build via `PlatformTarget=x64`.
- Added PolySharp polyfills for modern C# language features on net48.
- Rewrote `Dictionary.TryAdd` call to the net48-compatible `ContainsKey`/`Add` pattern.
- Rewrote `string.Split(char, ...)` calls to net48-compatible overloads.
- Replaced `SslClientAuthenticationOptions` with the legacy `AuthenticateAsClientAsync` overload.
- Replaced `string.Contains(string, StringComparison)` with `IndexOf(string, StringComparison) >= 0`.

### Added
- Post-publish MSBuild target that automatically bundles a GPL3-compliant source zip alongside the published EXE.
- CHANGELOG.md.

## [1.1.3]

_Historical entries to be backfilled._

[Unreleased]: https://github.com/SteveTheKiller/KillerScan/compare/v1.4.0...HEAD
[1.4.0]: https://github.com/SteveTheKiller/KillerScan/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/SteveTheKiller/KillerScan/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/SteveTheKiller/KillerScan/releases/tag/v1.2.1
[1.2.0]: https://github.com/SteveTheKiller/KillerScan/releases/tag/v1.2.0
[1.1.3]: https://github.com/SteveTheKiller/KillerScan/releases/tag/v1.1.3
