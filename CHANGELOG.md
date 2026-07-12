# Changelog

All notable changes to KillerScan are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.5.2] - 2026-07-12

### Added
- Japanese (ja-JP) interface translation, selectable from the language picker.
- Status-bar messages now follow the selected language: scan progress (discovering/resolving/probing), scan complete/cancelled, export confirmations, device-type overrides, and the PORTABLE badge with its install button were hardcoded English and are now translated across all nine locales.
- Column widths in the results table are user-resizable again (the custom header style had dropped the resize grips).
- The window can be dragged from anywhere on the title bar or the scan toolbar's empty space, and the wordmark no longer opens the website on click (that link lives in About).
- The window remembers its size, position, and maximized state, and the results table remembers your column order and any widths you resized, restoring both on the next launch.

### Changed
- Footer/status bar matched to the family standard set by KillerPDF and killerpdf.net: 24px row, 11.5 status text, 10.5 corner version/copyright. The devices pane sits flush against the toolbar and footer (its shadow overlays them), spacing around the subnet box and the Discovered Devices header is an even 8px, and the table keeps an even 8px to both pane edges with the scrollbar hugging the pane edge, clear of the header, scrolling by pixel so no dead band is left after the last row.
- Bundled MAC vendor database refreshed from the current Wireshark OUI list: 57,591 entries (was 57,430). The app now loads whichever list is larger - the bundle or your last in-app refresh - so upgrading to a version with a fresher bundled database takes effect immediately instead of being shadowed by an older local copy.
- Window, dialog and flyout animations are now consistent. The theme flyout, language menu and install dialog previously appeared instantly; they now fade in over 150ms with an ease-out curve, matching the main window (retimed from 180ms) and the About overlay (retimed from 120ms). The shared timing lives in `Anim.cs`.
- Results columns now size to their content: IP Address, Hostname (capped at 220px), MAC Address, and Type fit their widest value, while Vendor and Open Ports flex to fill the remaining space. Fresh launches start at the settled widths so the table no longer jumps as the first results land.
- Default window size trimmed from 1100x700 to 1025x700 to match the tighter columns.
- About card: the description now reads "A GPLv3 Killer Tools utility", the card's own copyright footer is gone (the app footer already carries it), and the card padding is an even 18px with the width trimmed so the SHA-256 line fits exactly.

### Fixed
- The window no longer renders black over console-session screen-sharing tools (ScreenConnect, Kaseya LiveConnect, VNC, TeamViewer). WPF composited through the GPU, whose surfaces those tools can't capture, and because they aren't Terminal Services sessions WPF never fell back on its own. Rendering is now forced to software mode (`RenderOptions.ProcessRenderMode`) at startup.

## [1.5.1] - 2026-07-02

### Fixed
- Scanning over a routed link or VPN (e.g. FortiClient SSL VPN) no longer collapses every device to the same MAC and vendor. ARP can't resolve hosts across the tunnel, so `SendARP` returns the next-hop (VPN adapter) MAC for every remote IP; a MAC returned for more than two distinct IPs is now treated as that artifact and discarded, letting those hosts classify by their port and fingerprint signals instead of being mislabelled as the VPN's vendor.
- Host discovery no longer misses devices that block ICMP. A host that doesn't answer ping now gets a quick TCP-connect probe on a few common ports (445, 3389, 80, 443, 22) and counts as alive if any responds, closing the large undercount seen when scanning over a VPN where ARP can't help.
- Install confirmation dialog now matches the KillerDialog style: the title bar is transparent so it blends into the window with the shared film grain, and it carries the KillerScan wordmark instead of a flat solid bar with plain text.

## [1.5.0] - 2026-06-28

### Added
- **mDNS (Bonjour) and SSDP (UPnP) discovery** (`MulticastDiscovery.cs`): a network-wide multicast pass runs alongside the ping sweep, mapping service types and SSDP SERVER strings back to hosts for much stronger device identification.
- **MA-M (28-bit) and MA-S (36-bit) OUI support**: vendor lookup now matches longest-prefix first (MA-S -> MA-M -> MA-L) so small vendors sharing a 24-bit block resolve correctly.
- **In-app vendor database updater**: the About screen shows the current OUI entry count and last-refresh date, with a one-click refresh that pulls the latest data (Wireshark `manuf`, with an Nmap fallback), is gzip-aware, and applies a never-shrink guard so a blocked or partial download can never wipe or downgrade the list.
- New probe ports: 139 (NetBIOS), 554 (RTSP), 1883 / 8883 (MQTT), 5357 (WSD), 32400 (Plex).
- Hostname fallback to the mDNS `.local` name and the NetBIOS name when reverse DNS returns nothing.
- In-window **About** overlay matching KillerPDF: dims the window behind it, shows the app icon and typewriter wordmark, a merged description / tagline, and a lifted info panel (version, publisher, certificate thumbprint, SHA-256), with a GitHub release link and update checker, and a blended close-X that turns red on hover.
- Typewriter **KillerScan wordmark** in the title bar (bundled font), paired with the new app icon, "Scan" in the accent colour with a drop shadow (off in light mode).
- New transparent app icon and multi-resolution `.ico`.
- Per-theme **device-type colour palettes** (15 type colours tuned per theme).
- RJ-45 / Wi-Fi **interface badge** in the network info bar.
- Govee brand overrides for five MAC prefixes that IEEE registers as "Private", and a `(Randomized)` vendor label for locally-administered (privacy) MACs.
- Column-header sort-direction chevron plus hover and press states, and rounded top corners on the header strip.

### Changed
- **Gateway / DNS-aware classification**: the gateway is labelled Router, DNS Server, or Router/DNS, with Router/DNS used only when the gateway *is* the configured DNS server.
- Classifier gained mDNS service-type signals (Chromecast, printers, Sonos/Spotify, AirPlay, HomeKit/Hue), SSDP SERVER signals (Roku, Synology/QNAP, Plex, DLNA, Samsung TV), and new port signals.
- Known PC / workstation vendors (Dell, HP, Lenovo, MSI, Asus, and similar) with no open ports now classify as Windows instead of falling through to the IoT catch-all during standby.
- Vendor resolution centralised through `ResolveVendor` (OUI lookup + brand overrides + randomized-MAC labelling), applied on both the quick and full scan paths.
- Install button adopts the same OutlineButton hover / press behaviour as the Scan button.
- RGB-theme button outlines, footer / status text, scrollbar hover, and input hover colours corrected; scrollbars thinned and lightened on hover.
- Collection initializers across `NetworkScanner.cs` simplified to C# 12 collection expressions.

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

[Unreleased]: https://github.com/SteveTheKiller/KillerScan/compare/v1.5.2...HEAD
[1.5.2]: https://github.com/SteveTheKiller/KillerScan/compare/v1.5.1...v1.5.2
[1.5.1]: https://github.com/SteveTheKiller/KillerScan/compare/v1.5.0...v1.5.1
[1.5.0]: https://github.com/SteveTheKiller/KillerScan/compare/v1.4.0...v1.5.0
[1.4.0]: https://github.com/SteveTheKiller/KillerScan/compare/v1.3.0...v1.4.0
[1.3.0]: https://github.com/SteveTheKiller/KillerScan/compare/v1.2.1...v1.3.0
[1.2.1]: https://github.com/SteveTheKiller/KillerScan/releases/tag/v1.2.1
[1.2.0]: https://github.com/SteveTheKiller/KillerScan/releases/tag/v1.2.0
[1.1.3]: https://github.com/SteveTheKiller/KillerScan/releases/tag/v1.1.3
