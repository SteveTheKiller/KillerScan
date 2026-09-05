# Changelog

All notable changes to KillerScan are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.7.0] - 2026-09-05

1.7.0 brings permanent Devices, Services, Topology, Keep Alive and Terminal views, alongside scan history, trusted-device baselines, scan profiles and an embedded terminal.

### Added
- Permanent Devices, Services, Topology, Keep Alive and Terminal views on the right of the toolbar. Each keeps its own state, a right-click menu sets icon size and caption placement, and buttons that do not fit overflow into a menu rather than squeezing the input bar.
- An embedded terminal running PowerShell, ping and SSH, preferring PowerShell 7. It uses KillerShell's font, palette and prompt, the prompt unpacks to a file you can edit and keeps your version across upgrades, and the KillerScripts module travels inside the exe so it is there on a machine you cannot install anything on. Device ping is colored by result and says that Escape stops it.
- Keep Alive (F9) watches any number of selected devices at once, each as a status card with a latency sparkline, packet loss, its own checks on banded rows and its own event log. Right-click a card to copy the address, rerun its checks, reset its counters or drop it from the run.
- Device diagnostics (F3) checks reverse and forward DNS, ICMP, the local route, common and previously seen TCP ports, and traceroute.
- Topology (F8) draws an inferred view of the network with movable, multi-select device boxes and four arrangements, cycled with Ctrl+G or picked with Ctrl+1 through Ctrl+4. The arranged view exports as a full-resolution PNG. (#3)
- A service-centric view (F7) organized by discovered service, port and device.
- Scan history (Ctrl+H) compares each scan against the previous run of the same target for added, missing and changed devices, and adds trusted-device baselines with unknown-device alerts. A selected entry reads either way: the comparison, or the full device list as it was recorded, with the choice remembered.
- Scan profiles (Ctrl+Shift+P) load or run remembered targets and can follow with Deep Scan. History and profiles share a sliding sidebar that opens from the icon rail and resizes by dragging.
- Deep Scan performs an exhaustive, cancellable rescan of every discovered host.
- A speed test on the icon rail, which runs in the terminal. It uses the Ookla CLI when it is installed, offers to download it if it is not, and otherwise falls back to a built-in HTTP throughput test that installs nothing.
- The footer shows the active adapter and its link speed beside the address, with a wired or wireless glyph.
- Export follows the active view: devices as CSV or HTML anywhere, the arranged topology as a PNG in Topology, and the service list as a CSV in Services.
- Manual device names now persist alongside manual classifications.
- F1 switches between the shortcut list and a keyboard map. The list is grouped into two columns under colored category headings, and the map paints each key in its category color.
- A trust marker in the gutter beside each address: green for a device in your trusted list, amber for one that is not, using the same two colors as the status light.
- Hovering the status bar explains the current state. On the unknown-device alert it names the devices, in Keep Alive it reports the run, in scan history it identifies the selected entry.
- The shortcuts overlay links to the online help page.
- On first launch the interface follows the Windows display language when it is supported.
- `/uiscan [targets]` opens the full interface and begins scanning the supplied targets immediately. (#1)

### Changed
- The scan view is now called Devices, so it reads as the counterpart to Services. Scan remains the name of the button that starts a scan.
- Export moved from the toolbar to the icon rail, with the same flyout and the same Ctrl+E.
- Demo mode now fills every view, including topology, history, trusted devices, profiles and a scripted terminal session, and keeps all of it in memory so it never touches your real history, device or profile files.
- CSV and HTML exports use the family file picker, including complete filename tooltips.
- Checkable menus reserve a left indicator gutter so labels and shortcuts stay aligned.
- The footer marks a portable copy in place of the old install button.
- On the 98SE theme the About panel is a white client area with a sunken edge instead of the button-face gray it shared with the card behind it.
- The Services view colors its Type column the way the device list does, from the same shared definition.
- The Type column sorts by the name shown on screen rather than the English value behind it, so the order is alphabetical in the language being read.
- The vendor database notice clears itself after a few seconds instead of standing in the status bar for the session, and the device count appears only once there is a count to report.
- The bundled vendor database is refreshed to 58,107 entries.

### Fixed
- The taskbar button had no icon. A window with no caption never hands one to the shell, so it is now set on the window directly.
- The status line was capped at a fixed width and trimmed to an ellipsis on a window with room to spare. It now measures the space up to the portable badge.
- Device detection now distinguishes physical printers from Windows servers that share print queues, including domain controllers.
- Scans clear the local DNS cache and reject reverse-DNS names that do not resolve back to the same IP address.
- Command-line output now reaches applications that launch KillerScan with redirected streams but no parent console.
- On the Sepulchre and Mourning themes the selected theme picker row no longer loses its ring, dot and label while hovered.
- On 98SE, hovered table text was a pale tint of the accent on a light gray row. It is now a dark tone of the accent in all six accents.
- On 98SE the Keep Alive event log used the menu gray behind it and a VGA green too light to read there. It is now a white client area with a darker green.
- Selected items were nearly invisible on Malaise and Sepulchre, whose selection color sat within 1.3:1 of the surface behind it, and unreadable on Black with the orange accent, which selected with the full accent color instead of a dark tone of it.
- Text fields matched the surface around them on Black, Blood, Cyanotic and Greed, so the subnet box did not read as a field.

## [1.6.1] - 2026-08-29

1.6.1 makes the command line actually work and grows it into a full scripting tool, brings the app and killerscan.net to fifteen languages, and cleans up install, uninstall and theme rough edges.

### Added
- Italian, Hungarian, Kazakh and Russian localizations for the complete app interface and killerscan.net, bringing both to fifteen languages - the same set as KillerPDF.
- The command line now includes `/probe`, `/network`, and offline `/vendor` commands; table, CSV, JSON, and themed HTML output; text, device-type, vendor, and port filters; sorting, descending order, result limits, custom port checks, progress reporting, timeouts, header control, extension-based export formats, and an optional empty-result exit code for automation.
- `/scan /demo` produces the same fabricated network as the GUI's demo mode, for screenshots that show no real environment.
- On an interactive console the command line shows a spinner while scanning and colors its output - each results column in its own color, errors red, and a green "KillerScan complete." when done. When `/export`'s file extension picks the format, the console still shows the readable table while the file gets that format. Redirected output, pipes, exports and explicit `/csv` or `/json` stay plain.

### Changed
- The bundled OUI vendor database was refreshed to the current Wireshark manuf list (58,049 entries).
- F12 now opens the About window, matching the rest of the family, and the shortcut list in the app and on killerscan.net now covers every shortcut, including the device actions.
- The language menu now uses the family's radio-row list with the locale code right-aligned, matching KillerPDF.
- The interface is now translated where it was not. Device types read in your own language throughout the results grid, the "Set device type" menu and the exported report; so do the install, update and uninstall prompts, the vendor-database messages, the About and Keyboard shortcuts titles, the export and scan errors, and the save dialog's file types. The stored device-type value stays English so overrides, colors, report styling and the command line's `/type` filter are unaffected by the interface language.
- The exported HTML report follows the app's language, reusing the same column and theme names rather than its own.
- Polish was missing over a third of the interface and nine other languages were short a handful of strings; every language is now complete.
- The too-many-addresses message no longer suggests splitting the scan across tabs, which this release removes.
- Removed the unused tab-session scaffolding. A single scan already accepts several subnets, hosts, and ranges together, so the app now keeps one results session without carrying unreachable tab handlers or claiming a tabbed interface in package copy.
- killerscan.net now explains in every language that per-device SSH preferences store only a username; passwords, private keys, the connection, and authentication remain entirely with the Windows SSH client.
- Internal cancellation resource keys now use the same American spelling as their displayed text.
- Themes are now complete, app-owned resources with no private template overlay or external build dependency.
- The About card now takes its outer edge from the app-frame color and its information panel directly from the context-menu surface, with the pane-border color around that panel. The old About-only color override is gone, so every theme uses the same semantic keys as the rest of its interface.

### Fixed
- The About card's info panel has its film grain back instead of being the one flat surface on the card.
- Command-line scans no longer hang forever after printing the "Scanning..." line, so `/export` actually writes its file. The CLI had deadlocked this way since it shipped in 1.6.0.
- Machine-wide uninstall now requests administrator access and removes the Program Files copy, Common Start Menu shortcut, machine PATH entry and HKLM registration instead of targeting the per-user installation.
- The confirmation dialog's close-button corner now follows the active theme, so 98SE no longer leaves one rounded corner in otherwise square dialog chrome.
- Black theme card borders now match the rest of the dark-theme family instead of drawing a bright ring around every card and dialog.
- KillerScan now detects when both a per-user and an all-users installation exist and offers to remove the copy that is not running, and self-update keeps the Add/Remove Programs version current instead of leaving it describing the replaced build.

## [1.6.0] - 2026-08-12

1.6.0 adds a command line, stops SSH guessing your username, and moves theme and language onto a rail like the other Killer Tools apps.

### Added
- **A command line**: `/scan [targets]`, `/export <path>` to CSV or HTML, `/quick`, `/quiet`, `/help` and `/version`. Headless, works while the app is open, with real exit codes. Installed copies are added to PATH, so `KillerScan` works from any directory in a new terminal. Requested in issue #1.
- **SSH asks which account to sign in as** the first time you reach a device and remembers it against that device's MAC. New **SSH as...** on the right-click menu always asks. Leaving it blank restores plain `ssh <ip>`.
- Seven more themes, ported from KillerShell: 98SE, Ectoplasm, Decay, Malaise, Sepulchre, Delirium and Mourning. Thirteen in total. 98SE squares every corner and carries its own six Windows 98 accents.
- Dark, Light, and Black now open with KillerScan orange as their default accent instead of inheriting green; each can still remember a different user-selected accent.

### Changed
- Theme, language and the shortcuts button moved from the title bar to an icon rail down the left side, matching the rest of the Killer Tools apps. The theme picker is a named list now instead of color dots.
- Project reorganized into the family folder layout: `Shell/`, `Services/`, `Features/`, `Controls/`, `Models/`.

### Fixed
- Text on accent-filled buttons was unreadable on the Dark themes. It is near-black now instead of white; Light keeps white.
- The window kept its rounded corners when snapped to a screen edge. It squares off now, as it already did when maximized.
- The Black theme has its film grain back. It was at half the strength the rest of the Killer Tools apps use, so the app read as flat.
- The Black theme matches the rest of the Killer Tools apps key for key again. Its surfaces, row hover, alternate rows, text and input borders had each drifted, and its menus drew a light gray edge instead of the family's dark one.
- The app-size readout no longer parks itself on the status bar. It clears a few seconds after you stop zooming, and never overwrites a status written in the meantime.

## [1.5.4] - 2026-07-25

### Added
- **Scan several networks at once**: the subnet box now takes a comma-separated list, so `192.168.9.0/24, 192.168.10.0/24` sweeps both in one pass, with overlapping targets counted once. Alongside CIDR blocks each entry can be a single host (`192.168.1.7`) or a range, written either in full (`192.168.1.10-192.168.1.50`) or with just the last octet on the right (`192.168.1.10-50`). Spacing is forgiving: whitespace inside an entry is ignored and empty entries are skipped, so `192.168.9.0 /24 , , 192.168.10.10 - 50` reads the same as the tight form, and semicolons work as separators too. A malformed entry now names itself in the status bar instead of throwing a scan error, and a range larger than 65,536 addresses is refused with its actual size rather than locking the app up. The tab caption shows the first target plus a count.
- **App-wide size control**: roll the mouse wheel over the KillerScan wordmark in the title bar to scale the whole interface from 70% to 250%, in fine 2% steps. The scan toolbar and the devices pane grow together while the title bar and footer stay put, so the wordmark you are scrolling over never moves under the pointer. Text reflows and re-renders at the new size rather than being stretched, and the chosen size is remembered for the next launch. Matches the accessibility zoom already in KillerNotes and KillerPDF.
- Czech (cs-CZ) interface translation, selectable from the language picker, bringing KillerScan to ten languages.
- Czech translation of killerscan.net as well, across the landing, about, and technical pages.
- **Keyboard shortcuts**, with a `?` button in the title bar next to the language picker and F1 to open the list: F5 scan/stop, Esc cancel, Ctrl+R deep rescan the selection, Ctrl+F jump to the subnet box, Ctrl+A select all devices, Ctrl+E export, and Ctrl+Shift+plus/minus/0 for the app size. Kept to a plain list rather than the drawn keyboard KillerNotes and KillerPDF use, since KillerScan has ten shortcuts rather than eighty. Typing in the subnet or filter box keeps that box's own Ctrl+A behavior.
- Right-clicking the title bar (and Alt+Space) now opens a themed window menu instead of Windows' stock white one. The custom chrome makes the title bar a real native caption, which is what gives KillerScan free snap and drag, but it also meant Windows answered with its own unstyleable Win32 menu. Those two messages are now intercepted and answered with a normal themed menu that sends back the identical system commands, so Move, Size, snap and everything else behave exactly as before.
- **Install for all users** checkbox on the install confirmation. Ticking it puts KillerScan in Program Files with a Start Menu entry and an Add/Remove Programs entry for every account on the PC, rather than just yours. It routes through the same machine-wide install winget and Chocolatey already use, and Windows asks for permission only when the box is ticked, so the default per-user install still needs no admin rights. Declining that prompt leaves the app running as it was. Installing for all users removes an existing per-user copy so there is only ever one install, one Start Menu entry, and one uninstall entry; your theme, accent, language and window placement are kept.

### Changed
- Context menus now carry icons in a left gutter: the export menu, every action on the right-click device menu, all 21 device types under Set Device Type, and the new window menu. Menu items without an icon are unaffected - the gutter collapses - so nothing else shifted.
- Set Device Type now ticks the type the selected device currently has, so the menu shows where it stands before you change it. The tick follows the device's effective type, whether that came from the classifier or a manual override, and is recomputed each time the submenu opens so a rescan or Clear Override is reflected.
- Bundled MAC vendor database refreshed from the current Wireshark OUI list: 57,700 entries (was 57,644).
- `TRANSLATING.md` now lists all ten shipping languages; it had never been updated for Japanese, which shipped back in 1.5.2.
- killerscan.net: the Technical page gained a **Keyboard & app size** section covering all ten shortcuts and the wordmark scroll zoom, and the Phase 0 and Install sections were rewritten for multiple scan targets and the all-users install. The landing page's install card says the same. All of it is translated in the nine site languages.
- Status bar: "Scan cancelled" and "Install cancelled" now read "canceled" (American spelling, matching the rest of the app). A handful of stray British spellings on killerscan.net were corrected too.

### Fixed
- The top of the Scan button and the subnet box could not be clicked. WindowChrome measures its 36px caption from below the 8px resize border, so the caption region actually reaches 44px down the window - past the 36px title row and 9px into the scan toolbar. Windows swallows anything in that region as caption, so the upper third of both controls was dead and only their lower part responded. The toolbar now opts into chrome hit-testing, which hands those pixels back; dragging the bar is unaffected.
- Submenus could never open. The shared MenuItem style overrides the default template, which discards WPF's own submenu popup along with it, and nothing replaced it - so a parent item highlighted on hover and did nothing. This is why Set Device Type appeared dead. The template now supplies its own popup, using the same chrome as the context menu, plus a chevron on items that have children.
- The PORTABLE badge and the Install button appeared on machine-wide installs. `IsPortable()` compared the running exe against the per-user install path only, so a copy installed to Program Files by winget, Chocolatey, or an RMM never matched and was reported as portable. It now recognizes both install locations.
- killerscan.net: the Deep rescan section on the Technical page, added with the feature in 1.5.3, had never been added to the site's translation file, so it fell back to English in every language. It is now translated in all nine, and every key on all three pages resolves in every language.
- The self-update prompt now uses the themed KillerScan dialog instead of the plain Windows message box.

## [1.5.3] - 2026-07-13

### Added
- **Rescan selected hosts** (right-click -> "Rescan IP" / "Rescan IPs"): the results table is now multi-select (shift for a range, ctrl to pick individually), and rescanning re-probes just the chosen hosts and swaps each refreshed result back into its row in place. Runs independently of the subnet scan and is disabled while that tab is mid-scan.
- **Deep single-host probe** behind the rescan: far more thorough than the subnet sweep's per-host pass. It sweeps every well-known TCP port (1-1024) plus the curated high service ports, with longer connect timeouts and a retry so slow or loaded hosts still answer, then refreshes MAC, hostname, and TTL and runs the full fingerprint pass. Deep mode widens the HTTP and TLS fingerprinters to every open port (not just the standard web/TLS ones), so a panel or certificate on a non-standard port is still identified.
- Open Ports now show the full list in a hover tooltip, so a long port list no longer has to be read by widening the column.

### Changed
- Footer: the scan progress bar sits in its own zone and no longer crowds the PORTABLE install button, and the corner version/copyright text is 0.5pt smaller.
- Bundled MAC vendor database refreshed from the current Wireshark OUI list: 57,644 entries (was 57,591).

## [1.5.2] - 2026-07-12

### Added
- Japanese (ja-JP) interface translation, selectable from the language picker.
- Status-bar messages now follow the selected language: scan progress (discovering/resolving/probing), scan complete/canceled, export confirmations, device-type overrides, and the PORTABLE badge with its install button were hardcoded English and are now translated across all nine locales.
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
- Scanning over a routed link or VPN (e.g. FortiClient SSL VPN) no longer collapses every device to the same MAC and vendor. ARP can't resolve hosts across the tunnel, so `SendARP` returns the next-hop (VPN adapter) MAC for every remote IP; a MAC returned for more than two distinct IPs is now treated as that artifact and discarded, letting those hosts classify by their port and fingerprint signals instead of being mislabeled as the VPN's vendor.
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
- Typewriter **KillerScan wordmark** in the title bar (bundled font), paired with the new app icon, "Scan" in the accent color with a drop shadow (off in light mode).
- New transparent app icon and multi-resolution `.ico`.
- Per-theme **device-type color palettes** (15 type colors tuned per theme).
- RJ-45 / Wi-Fi **interface badge** in the network info bar.
- Govee brand overrides for five MAC prefixes that IEEE registers as "Private", and a `(Randomized)` vendor label for locally-administered (privacy) MACs.
- Column-header sort-direction chevron plus hover and press states, and rounded top corners on the header strip.

### Changed
- **Gateway / DNS-aware classification**: the gateway is labeled Router, DNS Server, or Router/DNS, with Router/DNS used only when the gateway *is* the configured DNS server.
- Classifier gained mDNS service-type signals (Chromecast, printers, Sonos/Spotify, AirPlay, HomeKit/Hue), SSDP SERVER signals (Roku, Synology/QNAP, Plex, DLNA, Samsung TV), and new port signals.
- Known PC / workstation vendors (Dell, HP, Lenovo, MSI, Asus, and similar) with no open ports now classify as Windows instead of falling through to the IoT catch-all during standby.
- Vendor resolution centralized through `ResolveVendor` (OUI lookup + brand overrides + randomized-MAC labeling), applied on both the quick and full scan paths.
- Install button adopts the same OutlineButton hover / press behavior as the Scan button.
- RGB-theme button outlines, footer / status text, scrollbar hover, and input hover colors corrected; scrollbars thinned and lightened on hover.
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
