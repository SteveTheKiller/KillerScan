# Context menus and keyboard scope

The F1 list and keyboard map read `Shell/Shortcuts.cs`. The help page reads the same table and locale resources through `build/shortcut-catalog.py`; `python build/shortcut-catalog.py --check` detects stale website data. Rebuild the website data after changing that table or its translations.

| Surface | Keyboard actions |
| --- | --- |
| Devices and topology devices | Copy IP/MAC/hostname, browser, ping, SSH/account, RDP, deep rescan, rename (F2), trust (Ctrl+Shift+B), clear manual name (Ctrl+Alt+N), clear type override (Ctrl+Alt+T) |
| Services | Copy IP/hostname, endpoint (Ctrl+Shift+C), service name (Ctrl+Shift+Y), port (Ctrl+Alt+P), protocol-appropriate browser/SSH/RDP, select all and CSV export |
| History rows and pane background | Copy saved IP/MAC/hostname/details, CSV export, Changes/All Devices (Ctrl+Shift+H), sidebar (Ctrl+H), current Devices (F6). Background menus disable row copy. Archived addresses never receive live device commands. |
| History icon | History settings (Ctrl+Alt+R), with the same clock icon as the rail button. |
| Profile rows | Run (Enter), load (Ctrl+L), toggle deep scan (Ctrl+Alt+D), delete (Delete). Save the current scan profile with Ctrl+Alt+S. |
| Keep Alive cards | Copy address (Ctrl+C), refresh checks (F3), reset counters (Ctrl+R), remove (Delete). Actions apply to the focused card. |
| Terminal | Copy/paste/select all with Ctrl+Shift+C/V/A, copy session text (Ctrl+Shift+Y), clear viewport (Ctrl+Shift+L). Normal shell control keys keep their existing meaning. |
| Exports and toolbar appearance | Menu hints match the CSV/report/text/image export chords and Ctrl+Shift+1 through 6 toolbar choices. History's rail export writes the visible saved history table. |

Shift+F10 and the Menu key open the focused surface's context menu. Right-click selects the row or card under the pointer, so it does not act on an old selection. A focused text box keeps its normal editing commands. Context-specific entries are labeled with their surface in F1 help and the website.

The deliberate exceptions are variable choices: device types, themes, accents, languages, topology fonts/layout/zoom presets, picker display options and terminal font selection. Those menus remain keyboard navigable; they do not reserve a global chord for every choice. The external-window ping/SSH alternatives are secondary menu actions. Window Move/Size/Restore/Minimize/Maximize use the native Alt+Space system menu; Close uses Alt+F4.

`--history-ui` in the SpeedTest.Tests console runner uses fabricated snapshots, redirects settings into a temporary registry key, and renders the actual WPF controls offscreen. It compares whole-window table positions with the original history pane at normal and narrow widths, with the sidebar open and closed. The scan identity and Changes/All Devices toggle occupy the outer toolbar above the content panel. The suite also checks saved row values, CSV escaping, empty and first scans, history command isolation, locale changes, themes, app scaling, service port routing and shortcut-map parsing. History settings fixtures cover exclusive modes, numeric bounds, Cancel, the clock-icon menu, bound shortcut, and the real removal-confirmation controls. Scratch archive fixtures verify migration, confirmation before deletion, cancellation, atomic persistence, reload, future pruning, Save all, failed writes and a scan finishing during confirmation. Tests never start a live scan, change the user's clipboard, read or prune real history. Physical keyboard, mouse and mixed-monitor DPI behavior remain separate hands-on checks.

History settings chooses one retention mode: a duration of 1-36500 days, a positive entry count, or Save all. New installations default to the latest 100 entries; existing archives with no selected policy keep Save all on upgrade. Saving a policy that would remove current scans presents the exact deletion count and requires Delete scans confirmation. Cancel preserves the saved policy and archive. The selected policy applies to later scans; time retention also expires scans on a later startup. Unreadable archives are preserved rather than overwritten.
