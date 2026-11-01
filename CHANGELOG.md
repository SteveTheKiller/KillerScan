# KillerScan Changelog

All notable changes to KillerScan are documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.9.0] - 2026-11-01

Locations keep each customer site separate, scan settings choose which checks run, Slovenian joins the languages, and updates, installs and network probes are hardened throughout.

### Fixed
- Concurrent Windows sessions sharing a profile keep each other's successful changes to settings, Locations and scan history.
- Rescan selected and Deep Scan All honor the chosen adapter again after it disconnects or changes, and refuse off-network results before probing.
- Picking a saved scan keeps its Deep Scan setting for each run and clears it when another saved scan or Location is selected.
- Command-line CSV exports include recognition confidence, evidence and access details, matching the app's exports.
- An all-users update reopens KillerScan for the signed-in user at normal rights, whether it finishes or stops early. Windows was refusing that relaunch, so the app stayed closed. Files left behind by earlier failed updates are cleared when the next update is staged.
- Toolbar controls, tabs, links and tables show keyboard focus. Contrast refreshes avoid duplicate work and restore frame colors. Picker selections keep text and icons shadow-free.
- Demo mode shows sample Locations in the sidebar and picker instead of an empty panel and the old profile-only menu.
- 98SE uses the original caption proportions and classic symbols, with scan controls below the caption. The topology background menu opens at the clicked point without using the zoomed canvas as its anchor.
- All-users uninstall commands quote their nested helper correctly in both silent and interactive modes.
- A command-line value of `uiscan`, such as a filter or Location name, no longer opens the app and starts a different scan.
- A scan that finds one device says "1 device", in the footer, the status bar, the terminal and the command line.
- When another window deletes a Location, the tab's message no longer calls the results a Temporary scan, which was not always true.
- Save results to Location moves a scan only once all of it is on disk in the destination. A run the destination's history limit would delete, or a Location deleted in another window, is refused with the reason, a stopped scan's results are saved as a canceled run and can be saved from the menu, a Temporary scan's names and device types all go with it, a save that fails leaves the results and the tab where they were, and saving again finishes the move without doubling anything.
- Retry after a failed rename or delete in History repeats that change, a failed one no longer empties the list, and two KillerScans open on the same Location each alert on devices the other never saw.
- A Location appears in the list only once its imported files are in place, even if KillerScan is killed mid-import. Deleting a Location finishes or changes nothing, so a locked file or a blocked recovery folder can no longer crash it or leave files no list points to. Imported history with empty fields can no longer crash History's copy menu.
- An mDNS answer relayed by a reflector puts each service on the device its records name, so one packet can no longer give a printer a speaker's services or lose the speaker.
- A Location learns only the router of the adapter a saved scan is actually on, so a VPN, a second adapter or a remote target can no longer teach it someone else's router.
- History compares a scan only with earlier scans of the same network, identified by its router or adapter, so two sites sharing 192.168.1.0/24 in one Location no longer show each other's devices as added or removed.
- A finished Temporary scan says it is not saved and points to Save scan, because closing KillerScan loses it, and leaving one asks first even when the table is empty. A footer status too long to fit reads in full in its tooltip and is announced to screen readers.
- Renaming a run in History keeps that run selected, so a Delete right after it cannot remove the wrong one.
- An uninstall refused because KillerScan was running no longer leaves a copy of the app in the install folder after the next uninstall finishes.
- Dialog title bars match the rest of the dialog in every theme except 98SE. They had their own band, a lighter strip in Light and a gradient in several dark themes, and the title bar painted its film grain twice.
- Update and uninstall helpers load PowerShell modules only from Windows' own folder, so a module planted in your Documents cannot run inside them as administrator; a failed uninstall reports the real reason and retries when a copy is still running; a stalled update says so; and the window no longer freezes while an update is handed off.
- The terminal keeps 10,000 lines of history, so a ping -t or a long SSH session no longer grows memory without end; a flood of combining marks cannot stall it; resizing during vim or less no longer loses the latest output; and pasting several lines asks first when the shell would run them at once.
- Ping and SSH from a device ask before replacing a command still running in the terminal, and the terminal keeps Ctrl+E, Ctrl+T and the other readline keys instead of the app taking them.
- In History, Enter on a button or the Locations list works again, deleting a run clears it from the pane, and screen readers now get the view tabs as tabs.
- A web page title from a device can no longer stall a scan, unresolved ARP entries no longer appear as phantom printers, a router from another network is no longer labelled on the current one, and quick scans use the mDNS and SSDP names they already collected.
- The prompt's git status runs without the repository's fsmonitor or hooks, so changing into a downloaded repository cannot run its code.
- Clearing a device's custom name brings back the name the scan found, and a blank rename clears it.
- The command line escapes control characters in device names when it prints CSV to a live console.
- HTML reports exported with `/theme 98se` open in the selected theme, and command-line help distinguishes saved scans from probes that only read Location annotations.
- An update that cannot finish because KillerScan did not close says so in its recovery report, the Update button stays off once an update has started, and a translation file can no longer give a setting the wrong kind of value.
- Topology builds once per refresh when card sizes have not changed, and a card resizes with its border when the theme changes.
- A device can add at most 64 mDNS service names, each bounded in length, however many it announces.
- A copy of KillerScan saved under another name, such as KillerScan (1).exe, keeps its terminal colors while another copy is open.
- Save results to Location from Unassigned moves the run on screen, never another window's newer run, and moves nothing when the table is empty.
- Exporting and importing a Location keep the window responsive, and an import changes the Location list only on the window's own thread.
- Results on screen in Unassigned are no longer cleared when the network changes to one a Location recognizes, and a canceled scan in a Location deleted elsewhere leaves it like a finished one.
- Moving earlier versions' saved scans into Locations never replaces one scan with another of the same name, keeps each scan's Deep Scan choice, and never takes over a Location you happened to name Saved scans.
- The elevated part of an update compiles its helper code in a folder only administrators can open, so another program running as you cannot slip code into it, and an update that finished is no longer reported as failed when KillerScan cannot be reopened afterward.
- Renaming a saved scan keeps it, keeps its Deep Scan setting, and refuses a name another saved scan already uses instead of replacing that scan.
- A scan run whose save failed is no longer written to history twice, and deleting it stays deleted. A renamed run keeps its name when it is saved to a Location.
- SSH in the built-in terminal starts Windows' own ssh.exe by its full path, never a file of that name sitting beside a portable copy.
- An adapter reporting a 0.0.0.0 mask no longer becomes the network to scan; the next adapter is used.
- The Locations list answers Enter, F2, Delete and the menu key itself instead of passing them to the device table.
- Topology cards with long names no longer overlap their neighbors or hang off the edge of the map; every row and ring is spaced for its widest card.
- The F1 keyboard map shows the arrow keys, so Alt+Shift+Up and Alt+Shift+Down (tabs at the top or bottom) appear on it.
- The tray icon's menu opens from the pointer upward, out of the notification area, the same way in every Killer app. A short menu no longer covers the tray icons and a taller one no longer floats about 40 pixels above them.
- Self-update checks the downloaded and staged copies for the publisher's signature inside the helper, keeps every parent folder pinned while it publishes, refuses redirected paths and malformed requests, and offers only stable releases.
- The close button on dialogs now works from the keyboard and screen readers, the window menu opens at the right spot while the window is maximized, and the file dialog's folder tree no longer jumps back to an older folder after a quick change of folder.
- A Location only recognizes a network you saved a scan into, so the switch suggestion no longer names a Location that merely had a scan run in it. A Location is recognized by its router's hardware address, never by a common subnet.
- On Cyanotic, Greed and Blood, selected rows and buttons use the theme's cream with dark text, and an unapproved device stays visible on a selected row.
- New Location, rename and notes prompts show the app icon, and New Location no longer shows the current Location as if it were the name.
- Dialogs have their padding back, and their title bars, icons and buttons match the rest of the family.
- A window on a network a Location knows now opens in that Location, matched by gateway or by subnet.
- A speed test blocked by a Cloudflare security challenge now says so, and a rejected upload reply shows its status, content type and the start of its body instead of only "invalid response" (#4).
- Website carousel and slide descriptions are translated in every supported language.
- In 98SE, long paths in the file dialog no longer run under the recent-locations arrow.
- Black table headers match the content surface, and softer grain keeps dark surfaces from looking gray.
- Machine update and uninstall helpers reject unsafe inherited permissions before creating elevated staging.
- Per-user and portable updates no longer run their helper from a script in the temp folder.
- Installation cleanup preserves unrelated files and folders; registered silent uninstall waits for owned removal and reports failures.
- Elevated updates use the signed installed helper and verify the release identity, digest and publisher in protected staging.
- Scans, Remote Desktop, and SSH or ping in their own window start arp, mstsc, ping and Windows' ssh from System32, so a program with the same name beside a portable copy can never run in their place. A stalled arp no longer holds up a scan, and reading the device table no longer pauses the window as a scan starts.
- Per-user and portable updates now require the same publisher signature, product and newer version as all-users updates, instead of only a matching checksum. An all-users update checks the download before copying it into the installation folder, removes a staged copy that fails verification, and accepts the download only from an ordinary file on a local drive.
- Preference and history saves preserve existing files on failure, keep unsaved edits for retry, and report when changes could not be saved.
- Concurrent app instances preserve saved scans and retention changes instead of overwriting newer history.
- On a shared computer, another account can no longer block your settings, history and Locations from saving by holding the locks that protect them.
- File dialogs stay responsive on slow or crowded folders, cancel stale loads and identify incomplete listings.
- Vendor database updates preserve the prior complete file on failure and keep lookups available throughout reloads.
- Vendor database updates stop at a size limit, including a small compressed download that would expand without end, and refuse a list redirected away from HTTPS.
- Host names, page titles, server headers, banners and descriptions a device reports are limited in length, so one reply cannot swell a scan, its history or a report.
- Executable installation and self-updates verify complete copies and preserve recovery files after replacement failures. Update reports support non-English folders.
- Reports keep the scanned target, timestamp and captured results when pending input or live results change during export.
- Demo device types and SSH usernames stay in memory without reading or changing saved real-device values.
- Scan history includes empty results and final automatic deep-profile results, with canceled or partial runs labeled.
- HTTP fingerprints follow bounded redirects only on the scanned IP, preserving local panels without contacting or labeling another device.
- SNMP fingerprints preserve long descriptions and reject malformed or mismatched response identities.
- Deep scans never send HTTP or TLS data to raw print ports (9100 to 9103 and 515), so a rescan cannot print junk pages, including when a web page redirects to one of those ports.
- HTTP fingerprints connect to the device directly, so a system or PAC proxy can no longer answer and have its page reported as the device.
- NetBIOS names are accepted only from validated replies sent by the queried host.
- Bonjour (mDNS) names and services land only on the device they describe. Another device's question no longer names the device that asked, a relayed answer from a mesh router or Bonjour gateway goes to the device it names instead of the router, and a scan bound to one adapter ignores answers from other networks.
- A vendor database update skips manufacturer blocks of unusual sizes instead of filing them under a wider block, so they no longer replace the real owner of that wider block.
- The broadcast and multicast rows Windows keeps in its ARP table are no longer reported as devices when a scan range covers those addresses.
- A virtual adapter that reports no subnet mask, or that Windows fails to describe, no longer leaves the subnet box empty. KillerScan moves on to the next adapter, so the gateway and DNS server are still recognized.
- A normal scan reads the certificate from a Synology NAS that only answers on DSM's HTTPS port 5001. Before, only a deep rescan did.
- A bad latency reply that arrives just as a speed-test phase ends no longer throws away the finished download or upload measurement.
- Port checks and discovery listeners that time out no longer leave thousands of unhandled socket errors behind for .NET to report later. One deep probe of a single host used to leave about 2,000.
- Uninstalling, or moving from a per-user to an all-users install, works on a shared PC while another user has KillerScan open. Their copy can't be inspected, and before this the whole uninstall gave up because of it.
- An all-users install no longer reports failure and skips the relaunch when the per-user copy it replaces has lost its registry entries. That copy's files are still removed, and anything that can't be removed is offered again at the next start.
- A per-user or portable copy run as administrator can update itself. Before, it downloaded the update and then quietly refused to install it.
- The per-user update helper waits for KillerScan to close for at most a minute and checks the process start time, so a recycled process ID can no longer leave it waiting forever.
- Command-line errors show in the console when only the output is redirected, as in `killerscan /scan /json > devices.json`. Before, a mistyped option left an empty file and no message.
- Diagnose (F3) on a Network Tools window that isn't watching anything yet starts watching and opens the device's card. Before, it only added the address to the target box.
- The terminal no longer prints stray text such as `2::255:0:0m` from tools that send colors and underline styles in the colon form (bat, delta, neovim). Those particular styles are skipped for now.
- Clearing the terminal or a line after making the pane narrower also clears the text beyond the new width, so old output no longer turns up when you scroll sideways.
- The terminal's prompt palette files no longer pile up in `%LOCALAPPDATA%\KillerScan\prompt`, one per launch. Each start removes the files left by copies that have closed.
- With a screen reader running, a terminal holding a long scrollback no longer stutters. Text positions are worked out once per change instead of from the first line on every update and query.
- Starting or finishing a scan while a Location import is still running no longer loses changes to the Locations list, and can no longer crash the app.
- When another window or another copy of KillerScan deletes the Location a tab is in, the tab turns into a Temporary scan and says why. It keeps its results, which you can still save to a Location. Before, renames, approvals and finished scans kept writing into a hidden copy of the deleted Location that no list showed.
- When a saved names, types, logins or scans file can't be read, the "Could not save changes" prompt now names that file. KillerScan still won't overwrite it, so you can repair, restore or remove it yourself.
- Resizing the window or sliding the sidebar with a large topology diagram open no longer stutters. The diagram is redrawn a few times during the resize instead of on every frame.
- On killerscan.net, Escape closes the language menu, and closing it or picking a language returns keyboard focus to the language button.
- History settings tell a screen reader why OK did nothing, such as a retention value out of range, and put focus on the field to fix.
- An upgrade installed while KillerScan was running (winget, Chocolatey or an RMM tool) no longer leaves a full-size `.bak` copy of the old exe behind for good. The next install, update or uninstall removes it.
- Uninstalling an all-users copy from Settings asks whether you're sure before the administrator prompt, not after it, and the question no longer runs as administrator.
- When an all-users update can't be installed, KillerScan opens again on the version you had instead of staying closed with nothing said.
- Update downloads no longer pile up in the temp folder. A successful all-users update removes its download right away, and leftovers from refused or interrupted attempts are cleared by the next update.
- The published source zip now matches the tagged release. It used to be built before the README picked up the new version, so it carried the previous release's source link.
- A metadata repair of the KillerScan.Engine NuGet package can change only the package description, tags and links. A change to the engine project that affects what gets compiled, or which dependency versions ship, now stops the repair.
- The PowerShell `scan` and `probe` commands return device objects instead of nothing, and failures, timeouts and usage errors throw with the app's message and exit code. Arguments such as a folder path ending in a backslash reach the app intact.
- The engine's scan APIs refuse malformed or oversized targets and a port concurrency below 1 before any network or DNS cache work, instead of reporting an empty scan or waiting forever.
- Saving an export over an existing file keeps the old file intact if the save fails partway. This covers CSV, HTML, SVG, PNG and JPEG exports, history CSV, Keep Alive and terminal saves, and command-line `/export`.
- A scan history file that cannot be read (damaged, locked or access denied) is reported at startup with its location and a Retry, and History says it could not be read instead of showing no scans. The file is never overwritten.
- Saving a profile over an existing name, in any letter case, keeps its Deep Scan choice instead of turning it off.
- History compares a scan only with an earlier completed scan of the same full target list, with the same checks and the same normal or deep pass. Different lists that share a "first +N" caption no longer compare, and older entries saved with only that caption are not used as baselines.
- A canceled or partial scan never reports devices or ports as removed, since it did not check everything.
- Rescanning selected devices reports how many actually refreshed and how many did not respond, instead of counting every selected host. HTML reports keep the original scan time and add when devices were refreshed later and whether that refresh was partial.
- Device names, approvals, type overrides and SSH usernames no longer carry over between customer sites that reuse the same IP addresses. Each now belongs to a Location.
- Removing the last approved device no longer approves every device on the next scan.
- The public subnet helper rejects IPv6 addresses and malformed CIDR suffixes before expanding hosts.
- Terminal tables display escaped network control characters without changing raw scan data or JSON exports.
- Terminal combining marks, wide characters and common emoji clusters keep consistent columns through drawing, editing, copying and resizing.
- Stopping a scan cancels active HTTP and TLS fingerprint connections and prevents probes of later ports.
- Stopping during MAC resolution returns promptly, skips queued lookups and keeps native work bounded across scans.
- A remote SSH host or a device banner shown in the terminal can no longer pose as a ready prompt or start a speed test. Only the terminal's own shell, which knows a per-session value, can.
- Terminal title sequences accept BEL and ESC-backslash terminators across split reads and preserve UTF-8 titles.
- Pasting into the terminal drops control characters other than tab and line breaks, so pasted text can no longer end a bracketed paste early and run the rest as typed commands.
- A large paste into the terminal no longer freezes the window while the shell is busy. Input is written in the background in order, so a 16 MB paste returns at once instead of blocking for many seconds. Input the terminal cannot take, such as a paste over 64 MB, beeps and says so instead of vanishing.
- Topology and Services no longer redraw everything for each device a scan finds. Updates are grouped, so a large scan stays responsive with either view open.
- Topology devices can be used from the keyboard. The diagram is one Tab stop, and arrow keys move between devices and select the one they land on, so Enter and the device shortcuts act on it; Ctrl+arrow keys move without changing the selection, Space selects and Ctrl+Space adds to it, Shift+arrow keys move a device, and screen readers announce each device and whether it is selected. Focus stays on the same device when the diagram updates.
- The device table's MAC column is never narrower than a full address in the current font, so a dragged or older saved width can no longer cut addresses off.
- A device name lookup that never answers no longer holds up a scan. Each lookup gets three seconds, and stalled lookups are capped so they cannot pile up.
- Device and Services column headers follow a language change, and HTML reports declare the app's language and have a translated title. (#5) (Thanks @MrCodeLabs)
- Keep Alive and Diagnose add new targets to a running watch without resetting existing cards or counters.
- Export menus, shortcuts and tooltips match the active view, with one filtered CSV action for Services.
- Terminal output exposes readable text, selection and caret ranges to accessibility tools, with a visible keyboard focus outline.
- Closed website accent pickers stay out of keyboard and accessibility navigation, and return focus to their trigger when dismissed.
- A silent install or update that finds KillerScan running exits with 1618, which Intune, Configuration Manager and RMM tools retry, instead of a general failure.
- Release dependency checks stop on restore or audit failure, malformed results, missing engine coverage and vulnerable packages.
- Repeated same-version Publish refreshes the source ZIP and fails clearly if the archive cannot be replaced.
- Release monitoring includes KillerScan.Engine on NuGet and reports missing or failed publication.
- Chocolatey publishing reports failure when validation is still failing after a retry.
- Chocolatey and WinGet publishing accept only a release whose exe matches its published checksum and carries a valid release signature, skip prereleases, run the WinGet tool only when its hash matches, and treat release tags as data with read-only repository access.
- Window resize minimums stay within the current monitor's work area on small or scaled displays.
- The shortcuts card shows shadows at its edges when more content is out of view. Its border follows gradient themes, and its hint sits centered closer to the bottom edge.
- Sidebar fades leave the scrollbar clear and disappear at the start and end of each list.
- File pickers use theme content colors and app icons; 98SE dialogs have full bevels and compact fields. Text buttons, menus, tooltips and shortcuts use the embedded interface font, including separate windows.
- 98SE menu highlights follow the selected accent.
- Film grain has crisper specks and fades midtones across app themes without dimming bright highlights.
- The website's film grain is lighter, matching KillerTools, and the theme menu can turn it off (Alt+Shift+G). The top bar no longer leaves extra space above the page on narrower windows, and the release details box lifts with an accent border on hover.
- Balanced the Technical page columns by moving the glossary to the left.
- The file picker's Accept button can no longer fire twice: it stays off while a folder is loading or an accept is already running. A mistyped folder path now returns to the last folder that worked instead of leaving an empty list.
- The file picker's recent locations and places lists are named for screen readers.

### Added
- Move the History and Locations sidebar to either side, independently of tab placement.
- Locations can carry an icon (Home, Office, Shop, School, Server room, Cloud, Wi-Fi spot or the pin), picked from a button beside the name in New and Rename Location, and shown in the Locations list and on the Location button.
- Save to puts the unsaved scan on screen into an existing Location, with an optional name.
- Scans in history can be named (right-click Rename scan, or F2); an unnamed scan shows its target.
- Delete a scan from Scan history with right-click Delete or the Delete key.
- Hold Alt and drag anywhere in a window to move it.
- Speed test, Saved scans and History can be dragged into any order on the rail, and the order is kept. Theme, language and shortcuts stay at the bottom edge, as in the other Killer apps.
- Scan settings beside Scan selects discovery and fingerprint checks, saves them with profiles, and applies them to normal and deep scans. (#6) (Thanks @MrCodeLabs)
- Added Slovenian across the app, installation prompts, website and help. (#5) (Thanks @MrCodeLabs)
- Scan settings can let deep scans fingerprint unknown open ports. It is off by default, so unknown ports get a connection check only.
- The PowerShell `scan` command takes `-Demo` for fabricated screenshot data.
- Locations keep each customer site's saved scans, device names, approvals, type overrides, SSH usernames and history separate. Pick one from the Location button beside the target box, or run a Temporary scan that saves nothing. Changing Location clears the results on screen, so a rename or approval never lands in the wrong site, and leaving unsaved Temporary results asks first. On first start, everything you already had moves into a Location named Unassigned, and the original files are left in place.
- The first scan in a Location sets a baseline of Known devices without approving any. Later scans flag devices that are new since that baseline, and the device mark shows approved, new and known.
- Saved scans belong to a Location and are listed in the Location button's dropdown under the Locations. Clicking one loads its target, settings and adapter into the scan bar without scanning. Save scan, Rename scan and Delete scan are in the same dropdown. Picking a Location restores its last targets and settings without scanning.
- Each Location has notes for the gateway, VLANs, quirks or who to call, shown in the Location button's tooltip.
- Export Location saves a Location to one `.ksloc` file to import on another PC. Remembered SSH usernames are left out unless you tick the box, and passwords are never stored. Import always adds a new Location and never runs or scans anything. It reads the file in the background and refuses files over 256 MB.
- When the network's gateway has been seen in a Location, a banner offers to switch to it. It never switches by itself, offers nothing when more than one Location matches beyond listing them, and Dismiss hides it until KillerScan restarts. It reads the gateway from Windows' own ARP cache, so checking sends nothing, and screen readers announce it.
- Scan settings has a Network adapter choice. Picking an adapter sends every probe from it, so Ethernet, Wi-Fi and VPN adapters on the same PC no longer mix results. A bound scan reaches the adapter's own network and anything Windows already routes through it, such as hosts behind a VPN adapter; a target Windows would send out another adapter is refused with a message naming both. Saved scans keep their adapter, and if it is not connected KillerScan asks before using the adapter Windows chooses. A bound scan finds hosts only in its own adapter's ARP table. Each run records the adapter it used, and HTML reports show the Location and adapter.
- Save results to Location moves a Temporary scan's history and any names you typed into a Location, without approving anything.
- Manage Locations on the Location button opens a sidebar list of every Location, with a check on the current one. A click selects a Location, and a double-click or Enter switches to it. Each can be renamed, given notes, exported or deleted from its right-click menu. Deleting moves the Location's files aside to a deleted folder rather than erasing them. The Profiles section is now called Saved scans.
- History lists the current Location's scans. Right-click the History button and choose Show all Locations to list every Location's scans, each labeled with its Location.
- `/scan` and `/probe` take `/location <name or ID>`, and the PowerShell `scan` and `probe` commands take `-Location`. An unknown or shared name is a usage error. The terminal's Scan button scans in the current Location.
- `killerscan locations` lists the saved Locations with their IDs, customers, and last targets and adapters, as a table or with `/json`, and changes nothing. The PowerShell `locations` command returns them as objects.
- `/scan` and `/probe` take `/skip <checks>`, and the PowerShell `scan` and `probe` commands take `-Skip`, to turn off the same checks Scan settings offers. The terminal's Scan button uses the current Scan settings.
- `/scan` and `/probe` take `/adapter <name or address>`, and the PowerShell commands take `-Adapter`, to send every probe from one adapter. An unknown adapter, or a target that is neither on its network nor routed through it, is a usage error, and the terminal's Scan button uses the adapter chosen in Scan settings.

- Each device type now explains itself. Hover over the type for its confidence (Low, Medium, High or Set manually) and its strongest evidence, or right-click and choose Why This Type? (Ctrl+I) for every signal, its source, close alternatives and any conflict. Opening it sends nothing to the device. A manual type keeps the automatic result alongside, HTML reports show the same summary on hover, and CLI JSON and the PowerShell module carry the full evidence.
- Device CSV exports add Confidence and Evidence columns after the existing six.
- Right-click a device and choose Access to see every way into it: each web port and what it answered (a status, a login request or a sign-in page), the SSH banner, and every other open port. Clicking an entry opens it in the browser, SSH or Remote Desktop on that port, and Copy Address copies any of them. Nothing is claimed beyond what was seen, no credentials are ever sent, and opening the menu sends nothing. Deep scans now check every web port so each has its own evidence. CSV adds an Access column, HTML reports list the endpoints when hovering a device's ports, and CLI JSON and the PowerShell module carry the full list.
- About groups settings in two columns and can hide shortcut hints in menus and tooltips without disabling keyboard shortcuts. Hover tooltips clear large pointers while preserving custom placement.

### Changed
- Refreshed the bundled MAC vendor database to 58,645 assignments.
- KillerScan is per-monitor DPI aware, so text and icons stay sharp when the window moves between monitors with different scaling, instead of being stretched by Windows.
- HTML reports carry their own logo in every theme and accent, so opening a report no longer contacts killerscan.net.
- Confirmation prompts, including the install prompt, use the family dialog frame shared with KillerShell, and dragging a pinned place in the file dialog shows the plain arrow.
- Scan profiles from earlier versions become saved scans, each keeping its name, target and settings, inside one Location named Saved scans. Load one and use Save scan to put it into its own Location. Nothing new can be saved into Saved scans, and it removes itself once it is empty.
- Scan is the app's largest button: the filled accent button with its label in the wordmark's lettering.
- Edit Location replaces Rename and Location notes: name, icon and notes in one dialog, which New Location shares. The dialogs are tighter and their text larger.
- Each Location can keep several saved scans, each a target with its settings, labeled by its subnet or a nickname. They are listed under the Location in the tree and load into the scan bar with a click. Every run stays in Scan history.
- The sidebar starts closed instead of reopening History on every launch.
- The Locations sidebar is a tree in the family folder-tree style: each Location expands to its scans, newest first; click a scan to open it in Scan history, right-click to rename or delete it.
- The file picker uses the family's own buttons, fields and menus instead of a private copy of them, so it matches the rest of the app.
- The five views are permanent tabs below the table, or above it from the tab menu (Alt+Shift+Up and Alt+Shift+Down).
- The scan bar moved into the title bar, the filter sits beside the window buttons, and topology settings moved into the topology pane.
- Saved scans are Locations now: each saved scan became a Location with its target, settings and adapter. The Location button opens the Locations sidebar, which carries New and Import.
- Unsaved scans keep their history without appearing as a Location, and Temporary scan is gone.
- Topology cards are sized to their text, centered, and drawn in the tab shape with a fading accent edge.
- Buttons are bare until hovered, fill with the accent on hover and press, and Malaise uses peach for selection.
- KillerScan now shares the family's theme engine, control styles and palettes with KillerShell and the KillerUI template, so its colors, grain and geometry match the rest of the family. Visible differences: dialogs, the footer and the title bar use the family spacing, combo boxes and Light's text fields use the family field colors, menus fade in, the window has the family 1px edge, the close button is gray until hovered, selected table rows use the family highlight, and 98SE uses the family's thinner bevels and smaller caption buttons.
- Light is shades of gray with nothing white: the background, sidebar and footer are the darkest gray (#cccccc), menus and bars a step lighter, then panes, then fields.
- Open in Browser opens the web port that actually answered, HTTPS first, instead of always `http://` on port 80. The Services view opens a port with the scheme it answered with, including on non-standard ports.
- A name containing a device keyword, such as "synology" or "pihole", is now heavy evidence rather than the final word, so clearer protocol answers can outvote it. A UniFi or UBNT name now shows Switch/AP instead of the generic Network type.
- Trusted devices are now called approved devices, and approving is always a choice you make.
- Command-line scans without `/location` no longer apply saved device names or types, and save nothing.
- Updated the app and engine JSON dependency to 10.0.14.
- Updated and pinned the build/release actions to current compatible stable versions.
- Clarified sent-byte upload throughput and its acknowledgment requirement in the app, engine and website documentation.
- Hovered menu and list items show their text in the theme accent, lightened or darkened only as far as needed to stay readable on the hover fill.
- The speed-test server's loopback check (`server\speedtest\engine-loopback.ps1`) runs again. It still looked for the speed-test engine under its name from before the engine split.
- The deployment guide explains that automatic updates and the registered uninstall do not work where Windows PowerShell runs in Constrained Language Mode, and how to update and remove KillerScan there.
- Wordmarks and headings are raised with a subtle bevel in their own color instead of a dark outline; wordmarks keep their drop shadow. The Dark theme's red accent is the brand red again instead of a pink tint.
- Theme menu hover rows reach the right edge with the same inset as the left.

## [1.8.3] - 2026-10-19

### Changed
- Refreshed the bundled MAC vendor database to 58,575 assignments.
- Scan History and Scan Profiles open wide enough to show every date in full. Dragging the sidebar narrower draws their text up to 15% smaller before it is cut off.

### Fixed
- The theme and language menus open beside their own rail buttons instead of at the corner of the device list.
- A thin black line no longer shows under the title bar. The film grain now covers the whole bar.
- The dot in a selected radio button sits in the middle of its ring, and radio buttons and checkboxes line up with the capitals of their labels in Killer Sans UI and on 98SE.

## [1.8.2] - 2026-10-19

### Added
- Commands run as `killerscan scan` without a command prefix. `killerscan --help` lists them, and existing command forms stay compatible. Installed copies use PATH; portable copies run from their folder or full path.
- KillerScan checks for updates on startup and offers a newer release in a dialog with an Always check on startup box. A Check for updates on startup checkbox on the About card turns the check back on.
- Alt+Shift+G turns Film Grain on or off. It is listed in the shortcuts overlay, and the keyboard map has a new Alt+Shift layer for it.
- The website's top bar has a Microsoft Store button on every page, and the MCP page has a Microsoft Store button for KillerMCP.
- The website's About page lists KillerBench and KillerShot under Also try, with larger app icons.

### Changed
- Refreshed the bundled MAC vendor database to 58,575 assignments.
- Film grain is softer and shows on every theme, Black included. Its specks come in many shades, and some lean toward a color opposite the theme accent, such as purple on green and orange-brown on blue and red.
- Rail buttons and the collapse arrow sit centered across the rail, and the arrow is larger.
- Dialog headings, the history sidebar heading and the history detail header use Lacquer, the wordmark's app-name face, in place of Killer Sans. 98SE keeps Killer Sans.
- The history detail header is the same size as the Scan History heading.
- Context menus use Killer Sans UI like the rest of the interface instead of Segoe UI.
- The Light theme draws a softer shadow under the toolbars.
- Toolbar buttons with text under the icon are 6 pixels shorter, without the extra space above and below the icon and label.
- The website's top bar Download button uses the same font and height as the other top bar buttons.
- The PATH entry for installed copies now keeps the existing Path value's type and its %variables%, tells open programs about the change, and is removed by the uninstaller.
- The keyboard focus outline is a thin one-pixel dotted ring in the theme accent instead of a thick dotted line, and uses the text color where the accent would be too faint to see.
- Primary, outline and secondary buttons sit raised on a soft shadow that lifts on hover and sets down when pressed. 98SE keeps its flat beveled buttons.

### Fixed
- CSV exports keep device and scan text from running as spreadsheet formulas. (#10)
- Overlapping scan targets count each address once against the 65,536 address limit, so a /16 with hosts inside it no longer reports as too large. (#11)
- The public subnet helper rejects oversized ranges before expanding them. (#13)
- HTML report columns sort from the keyboard. Each column header takes Tab focus with a visible ring, Enter and Space sort it, and screen readers hear the sort direction. (#12)
- Windows contrast themes now use system colors for text, icons, panels and selections, without decorative grain or shadows.
- The window opens fully on screen on a small or high-scaling display, or when its saved size came from a bigger monitor, instead of running off the edges.
- HTML reports show the KillerScan wordmark in the report's theme and accent, loaded from killerscan.net, instead of always falling back to the text wordmark. 98SE keeps the text wordmark.
- The deep scan status and a few labels that were still in English are translated in every language, and the website's MCP page footer follows the language picker.
- On the Light theme's Yellow and Orange accents, buttons, links, tabs and accent icons keep their color with a thin gray outline around accent text and icons, instead of turning near-black or brown.
- Gray, menu and accent text reads at 4.5:1 contrast or better in every theme and accent, and the footer's version and copyright are slightly dimmed.
- The Dark theme's Red accent stays a true red on buttons, lines, scroll bars and the website's accent swatches, and only small red text uses a lighter red.
- The website's text and accent colors read at 4.5:1 contrast or better in every theme, and a narrow top bar shrinks the Microsoft Store and MCP buttons to their logos and moves links into a More menu instead of wrapping.
- On the website's Light theme with the Yellow or Orange accent, links, headings and labels keep their color with a thin gray outline instead of turning olive or brown.
- The exe's file properties show Steve the Killer as the copyright holder instead of leaving it blank.
- Screen readers no longer read toolbar, menu, tab and title bar icons as unreadable symbols, and every part of each scroll bar now has a name. File-picker rows announce folder and file names.
- Opening the shortcuts or About card moves keyboard focus into it, Tab stays inside the card, and closing it returns focus to where it was.
- Tab no longer stops on hidden links, which screen readers announced as the window.
- Screen readers and accessibility checkers no longer report empty text or the hidden parts of scroll bars as items on screen.
- Each group of option buttons is one Tab stop that lands on the selected option, and the arrow keys move between its options.
- Tab moves past the device, service and history lists in one step instead of visiting every cell, and screen readers read the address and type cells instead of a placeholder and a blank host name, MAC address or vendor as Unknown.
- Turning off Film Grain removes the grain from the device table, Services and Topology views too, instead of leaving it behind them.
- The Scan History sidebar can no longer be dragged or zoomed narrower than its dates, so a saved scan's date and time are never cut off.
- The website top bar no longer leaves extra space under the wordmark and buttons.
- The file picker's close button turns red on hover without filling a red block, like every other dialog's close, and keeps the classic caption button on 98SE.
- Theme and language radio buttons and the Film Grain checkbox line up with their labels again.
- On the website, text on accent-colored buttons, tabs and highlights switches to white or near-black for contrast, so it is never white on yellow.
- Theme names and the Keep Alive view name are translated in every language.
- The website's MCP page, contact cards, carousel and section toggles are translated in every language, the site uses the app's names for Keep Alive and Deep Scan, and text that had lost its accents in several languages is restored.
- On phones, the website's logo and download hash fit the screen instead of making the home page scroll sideways.
- The Network Scanner subtitle in the title bar sits on the baseline of Killer in the wordmark instead of 2px below it.
- The footer copyright sits a few pixels further from the corner grip instead of running into it.
## [1.8.1] - 2026-10-14

### Added
- Added a Film Grain switch to the theme menu. It is on by default, and turning it off removes the grain texture from every surface.
- Added the Windows 11 snap layouts menu to the maximize button.
- The Scan History and Scan Profiles lists fade out at the top or bottom while more entries lie past that edge.

### Changed
- Refreshed the bundled MAC vendor database to 58,526 assignments.
- The wordmarks use Punktype and Lacquer, and the interface text, headings and footers use Killer Sans, in place of the typewriter font in the app and on the website.
- Confirm, install and uninstall dialogs use the family setup dialog layout, with a notice icon, a larger Killer Sans heading and evenly sized buttons, so the Uninstall button is no longer cut off.

### Fixed
- Silent installs refuse an exe without a valid signature, return specific Windows error codes and reject a second concurrent install instead of reporting a general failure.
- A silent install started without admin rights, as the Microsoft Store runs it, installs for the current user with no prompt instead of failing.
- Screen readers announce every button by its name, including buttons with a detailed tooltip, the subnet box, the scroll arrows and the close buttons on dialogs, and read the Scan History and Scan Profiles lists and their rows by date or name and target. Controls reached with the keyboard show a dotted focus outline, drawn around small icon buttons instead of over them.
- Open Ports gets the same share of the device table as Vendor, so its port list is no longer cut off at the edge, and long hostnames and vendor names end with an ellipsis and show in full on hover.
- While a scan runs, the footer drops the adapter and link speed to make room for the progress bar instead of running into the PORTABLE badge, and the progress bar uses the theme accent.
- The footer's device count and network details use the same Killer Sans size as the status line and version.
- Ctrl+C in the Terminal stops a running command even when KillerScan was started by a program that ignores Ctrl+C.
- The Scan History sidebar opens wide enough to show the full date of every saved scan at any app zoom instead of trimming it.
- Scan History view buttons and context-menu choices use normal capitalization in every language.
- Active and hovered sidebar rail icons show the yellow accent with a dark halo on the Light theme's Yellow accent, instead of near-black.
- Dialog title wordmarks keep the main window's dark halo behind the app name on the Light theme's Yellow accent, so the yellow no longer fades into the title bar.
- The wordmark has a soft shadow on the Light theme too, so it no longer looks thin on the light title bar, and the dark themes use the same lighter shadow as the rest of the Killer Tools family.
- Button gradients start at the outline color and darken toward the bottom, so a hovered button no longer looks darker than its border.
- On the 98SE theme, dialog captions use Microsoft Sans Serif like the rest of the interface instead of Tahoma.
- The title bar icon is 27px and has its own 27px image, so it is a little larger and sharper.
- Centered website logos with space above and below and corrected current features, runtime requirements and documentation.
- Menu separators draw their divider line instead of leaving an empty gap.
- The About card's update button uses the family outline button instead of a solid dark block.

## [1.8.0] - 2026-10-08

### Added
- Topology settings for box size, layout, orientation, fonts and connection lines, with Ctrl+wheel zoom, zoom presets, compact radial layouts and hand cursors for dragging. Open settings from the toolbar or the map's right-click menu.
- Scan history settings for retaining scans by age, entry count or without a limit, with confirmation before a changed policy removes saved scans. History menus also copy saved device details, switch views and export snapshots.
- A Services context menu for copying service details and connecting with the selected service's protocol and port.
- Ukrainian, Norwegian (Bokmål) and Brazilian Portuguese localization, with a two-column language menu.
- Drag pinned file-picker places into a new order, and use Ctrl+wheel to change picker views and icon sizes.
- Added yellow and magenta accents to the neutral and 98SE themes and to the HTML export.

### Changed
- Refreshed the bundled MAC vendor database to 58,482 assignments.
- Scanning, discovery and device classification now live in KillerScan.Engine, a separate library the app, its command line and other programs share.
- Context actions show consistent glyphs and keyboard shortcuts in menus, with shortcuts also listed in both F1 help views and the online guide. Additional shortcuts cover Services, profiles, Keep Alive and the terminal.
- Refined theme colors, wordmarks, button gradients, footers and menu styling to match the family palette.
- Refined file-picker and dialog frames, added sharper title bar icons, and gave the uninstaller its own labeled taskbar button.

### Fixed
- Kept app icons sharp at fractional display DPI and app zoom by caching exact pixel sizes from a covering source and aligning the final draw to device pixels.
- Unsigned test builds can no longer push, tag or publish a release.
- Stopping a deep scan no longer waits for stalled fingerprint requests.
- Switching themes or languages no longer leaves toolbar views stuck in the overflow menu or covers the Scan button. A tight toolbar moves views into the overflow menu first, then drops their captions.
- Startup content becomes visible even when its fade animation stalls.
- Scan history uses the Devices table's rounded frame, with its target, accented dates, counts and view switch above the table. Sidebar and settings layouts stay compact, text is sharper, and menus anchor correctly. Localized headings refresh correctly, and history cannot act on a hidden live scan.
- Clicking a saved scan profile loads its target and shows the device list. The Profiles sidebar uses the same compact formatting as Scan History, with a clearer icon in its heading and rail.
- Text stays sharp at app sizes other than 100% again.
- Submenus attach consistently to their opening rows, with native direction and screen-edge placement.
- Corrected menu and selection contrast, including Light/Purple dropdown text, active language and theme highlights, and the 98SE Red accent.
- Lots of various theme and UI fixes, including a Topology settings icon button that matches the filter button, scroll shadows under the scrollbars of tables, topology and Keep Alive, a themed corner between the topology scrollbars, the shortcut list scrollbar on the card edge, and corrected Font and Name labels.

## [1.7.5] - 2026-09-28

KillerScan 1.7.5 improves the in-app update handoff.

### Changed
- Refreshed the bundled MAC vendor database to 58,417 assignments.

### Fixed
- Retried the executable replacement and showed the failure reason when an in-app update could not finish.

## [1.7.4] - 2026-09-28

KillerScan 1.7.4 adds more network commands for KillerMCP and Vietnamese localization.

### Added
- Added headless ping, traceroute, diagnostics, availability monitoring, and native speed tests for KillerMCP and automation.
- Added Vietnamese localization for the app and killerscan.net.

### Changed
- Refreshed the bundled MAC vendor database to 58,417 assignments.

### Fixed
- Allowed more time for upload acknowledgments during KillerSpeed tests on busy connections.
- Improved accessibility labels for window controls and file dialogs.
- Aligned the sidebar rail with the family footer spacing.

## [1.7.3] - 2026-09-21

### Fixed
- Kept the About update action readable and usable in every theme, including 98SE.
- Gave 98SE confirmation dialogs their classic caption bar, beveled frame, and gray close button.
- Made KillerSpeed tolerate brief network stalls and let the terminal `speedtest` command start a fresh run.

## [1.7.2] - 2026-09-17

1.7.2 runs speed tests with a native engine in the themed terminal.

### Added
- KillerSpeed, a native speed test in the terminal backed by a dedicated KillerScan server, with adaptive connections, aligned result tables, and consent before any transfer. It replaces the Ookla download and the HTTP fallback.
- A Scan box in the terminal with in-place progress, width-aware tables, and scan status and trust alerts in the footer.
- A terminal font and size selector with a live preview, plus consistent colors for addresses, MACs, ports, timing, and results in network command output.

### Changed
- Keep Alive's Copy results button was replaced by a TXT export in the Export menu, alongside CSV, HTML and PNG.
- Trust markers are now small centered dots, matched in the trusted-device menu and the device list header spacing.
- The terminal defaults to a smaller 11 point font, and command-line scan status starts with a KillerScan prefix.

### Fixed
- The 98SE file picker close button now keeps its gray caption treatment.
- Stop now cancels active host probes without waiting for their network timeouts.
- 98SE now uses the same rail as every other theme, restoring Export, the speed test, and the history and profiles sidebar.
- Restored the themed pane border and removed the doubled inner border and sunken edge in the Devices, Services and Topology views.
- The toolbar no longer grows taller when the view overflow button appears, and sort and filter controls stay out of views they do not apply to.
- The terminal keeps its history through resizing and clearing, holds its scroll position as output arrives, uses the theme accent for its prompt, clears the ping stop hint at the prompt, and reports Windows error details when it fails to start.
- Corrected About panel colors and header contrast.

## [1.7.1] - 2026-09-08

1.7.1 fixes installation and update failures, clipboard crashes, and reliability issues found during the maintenance audit.

### Fixed
- Completed shortcut references and translations throughout the app and website.
- Themed install and uninstall prompts, with black/orange defaults for standalone dialogs.
- Made installs and updates replace executables safely, report failures accurately, and support silent uninstall.
- Made Chocolatey run the installer and silent uninstaller instead of leaving a separate portable copy.
- Prevented clipboard contention from crashing MAC address, IP address, and hostname copying.
- Prevented duplicate device identities and malformed saved profiles or history from breaking history views.
- Kept browser, RDP, SSH, and website launch failures from crashing the app.
- Limited HTTP fingerprint response sizes to prevent excessive memory use.
- Preserved quotes, commas, and newlines in device and service CSV exports.
- Selected device rows now use their theme's established selection colors.

### Changed
- Updated the website's feature descriptions, help instructions, and keyboard shortcut reference; kept release updates compatible with translated labels.
- Refreshed the bundled MAC vendor database from Wireshark to 58,195 entries.

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
