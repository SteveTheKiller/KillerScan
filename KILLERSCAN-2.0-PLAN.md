# KillerScan 2.0 - Port Plan (themes, i18n, refactor from KillerPDF)

Bring KillerScan to KillerPDF parity on three subsystems:

1. Live theming - 6 themes (Dark, Light, Black, Blood, Greed, Cyanotic) + 6 accent hues.
2. Localization - the full string-resource system, English shipped now, other locales scaffolded for later.
3. Refactor - full partial-class decomposition of MainWindow, KillerPDF-style.

## Constraints

- net48 WPF, Windows-only build. The plan author cannot compile here; every phase ends with a build/verify checkpoint you run on Windows.
- Only change what each phase requires. Uncommitted work is protected; no git resets or reverts.
- Version stays 1.4.0 until you explicitly bump it.

## Current state vs target

KillerScan today:

- One theme, `Themes/DarkTheme.xaml`, mixing colors + brushes + control styles in a single file.
- 35 `StaticResource` refs in `MainWindow.xaml`, 42 in the theme, 0 `DynamicResource`. Nothing switches live.
- Registry is already used under `Software\KillerScan` for install/uninstall, but there is no general `GetSetting/SetSetting` for preferences.
- No i18n. All UI text hardcoded in `MainWindow.xaml` and code-behind.
- `MainWindow.xaml.cs` ~420 lines, monolithic.

Reusable straight from KillerPDF:

- `Services/ThemeManager.cs` (~229 lines) - near-verbatim port.
- `Services/LocaleManager.cs` (~68 lines) - near-verbatim port.
- App registry `GetSetting/SetSetting/RemoveSetting` pattern (`Software\KillerScan\Settings`).
- `Loc()` helper: `Application.Current.TryFindResource(key) as string ?? key`.
- Theme file structure (flat brush-key dictionary) + `Accents/{family}/{accent}.xaml` overlays.
- `Strings/*.xaml` with en-US as the always-present base fallback.

## Key technical decisions

1. **Keep KillerScan's own minimal key set; reuse KillerPDF's exact color values.** KillerScan's palette is a tidy 13-key set (`PrimaryBrush`, `BackgroundBrush`, `SurfaceBrush`, `TextBrush`, `MutedTextBrush`, `DimTextBrush`, `CardBorderBrush`, `TableHeaderBrush`, `RowAltBrush`, `RowHoverBrush`, `RowSelectedBrush`, `PrimaryHoverBrush`, `PrimaryPressedBrush`) that fits its simpler UI. Rather than adopt KillerPDF's 40-key role vocabulary, author the six theme files against KillerScan's keys, pulling each value from the matching KillerPDF theme so the colors are identical. Accent overlays use KillerPDF's exact accent hues (`#DD504B` red, `#50AEE8` blue, `#B982E3` purple, `#E8962C` orange, `#1FB8A8` teal over the `#1EA54C` green base). This keeps `MainWindow.xaml` churn to just `StaticResource` to `DynamicResource` with no key renames - far less risk on a codebase that cannot be compiled here. Role-to-key mapping (KillerPDF -> KillerScan): accent->Primary, darkest bg/sidebar->Background, mid surface->Surface, panel/header->TableHeader, dim border->CardBorder, primary/secondary/dim text->Text/MutedText/DimText, selection->RowSelected.
2. **Control styles become theme-independent.** Move `DataGridRow/Cell`, `PrimaryButton`, `OutlineButton`, `SurfaceButton`, `DarkTextBox`, `Card` out of the theme file and into App-level resources that reference brushes by `DynamicResource` (mirrors KillerPDF, where only colors live in `Themes/*.xaml`).
3. **Convert every theme-dependent `StaticResource` to `DynamicResource`** in `MainWindow.xaml` and the styles. This is the single biggest mechanical task and the main correctness risk; Phase 0 isolates it before any new theme exists.
4. **Settings:** add `GetSetting/SetSetting/RemoveSetting` to `App` under `Software\KillerScan\Settings`. Persist `Theme`, `<Family>Accent` (Dark/Light/Black), and `Locale`.
5. **Accent model:** KillerScan's "Primary" green is the base accent. Accent overlays recolor the `Primary*` keys plus `RowSelected` and the scrollbar accent keys. Accent-capable families: Dark, Light, Black. Blood/Greed/Cyanotic are fixed-accent (no overlays).
6. **i18n key-vs-label subtlety:** the device-type context menu's `MenuItem.Tag` values (Router, Switch/AP, Windows Server, ...) are persisted by `DeviceOverrides` and compared in code. Tags stay canonical English; only the `Header` localizes. Same rule for any string used as a lookup key or stored value.
7. **MergedDictionaries layout** matches the managers' index assumptions exactly: `[0]` theme, `[1]` en-US strings (base), `[2]` locale override (added/removed at runtime). Control styles live as inline App resources, not as a merged dictionary, so locale swapping never disturbs them.

## Phase plan

Each phase is independently buildable and shippable.

### Phase 0 - Foundation (settings + theme restructure)

- Add `GetSetting/SetSetting/RemoveSetting` to `App.xaml.cs` (`Software\KillerScan\Settings`).
- Split `Themes/DarkTheme.xaml` into `Themes/Dark.xaml` (colors + brushes only, current values) and move the control styles into App-level inline resources that use `DynamicResource` brush refs.
- Update `App.xaml` MergedDictionaries to `[0] Themes/Dark.xaml`, `[1] Strings/en-US.xaml` (placeholder until Phase 2).
- Convert `MainWindow.xaml` + the moved styles from `StaticResource` to `DynamicResource` for all theme brushes.
- **Checkpoint:** build; app looks pixel-identical; dark theme intact.

### Phase 1 - Themes (6 + accents)

- Port `Services/ThemeManager.cs` (namespace `KillerScan.Services`, settings keys, key mapping to KillerScan's brush keys, DWM dark titlebar, `RefreshIcons`).
- Author theme files Light, Black, Blood, Greed, Cyanotic against the same keys as Dark, using KillerPDF's palettes as the source of truth.
- Author accent overlays `Themes/Accents/{Dark,Light,Black}/{Red,Blue,Purple,Orange,Teal}.xaml`.
- Add switcher UI: theme swatches + accent dots (mirrors KillerPDF chrome, scaled to KillerScan's slim titlebar - see open question 2).
- Wire startup: `ThemeManager.Initialize()` before MainWindow; `ApplyDwm` on `SourceInitialized`; `RefreshIcons` on `ContentRendered`.
- **Checkpoint:** build; switch all themes/accents live; confirm persistence across restart and correct native titlebar color.

### Phase 2 - i18n scaffold (English)

- Port `Services/LocaleManager.cs` (namespace, settings key, Strings paths, en-US base fallback).
- Create `Strings/en-US.xaml` with `Str_*` keys for every UI string (inventory below).
- Add `Loc()` to MainWindow; convert code-behind strings to `Loc("Str_...")` with runtime formatting for counts/messages.
- Convert XAML text/headers/tooltips to `{DynamicResource Str_*}` (Tags excluded - see decision 6).
- Add a language switcher (globe menu), seeded with the locale list even though only English ships.
- Add `Strings/TRANSLATING.md` (port KillerPDF's).
- **Checkpoint:** build; every visible string resolves; no key-name fallbacks showing.

### Phase 3 - Refactor (full partial split)

Guiding principle (from Steve): draw a hard line between the **scanner core** (the app's actual function) and a reusable **UI shell** (the new "UI language" - chrome, theme, locale, grain). The shell wraps the scanner and is generic enough to lift onto a future port; the scanner core stays a self-contained unit.

Split `MainWindow.xaml.cs` into partials (all `partial class MainWindow`), grouped by side of that line:

UI shell (reusable across killertools apps):

- `MainWindow.xaml.cs` - fields, ctor, init wiring.
- `WindowChrome.cs` - Win32 maximize (`WndProc`/`MINMAXINFO`/monitor info) + titlebar drag/min/max/close.
- `Theme.cs` / `Locale.cs` - switcher handlers.
- `Grain.cs` - `ApplyGrainTexture`.
- `Install.cs` - `Install_Click`, portable badge, hyperlink nav.

Scanner core (the app's function; portable):

- `NetworkInfo.cs` - `PopulateNetworkInfo`.
- `Scanning.cs` - `ScanBtn_Click` + scanner event handlers.
- `Filtering.cs` - `FilterInput_TextChanged` + collection-view setup.
- `DeviceActions.cs` - context-menu handlers + `GetSelectedDevice`.
- `Export.cs` - CSV/HTML.

The scanner engine itself already lives in `Services/` (`NetworkScanner`, `OuiLookup`, `DeviceOverrides`) + `Models/NetworkDevice`; the partials above hold only the thin glue between that engine and the shell. Keeping that glue minimal is what makes a future port straightforward.

- **Checkpoint:** build; full smoke test (scan, filter, export, every context action, install badge, theme + language switch).

### Phase 4 - Polish + ship

- CHANGELOG entry; README note.
- Version bump (only on your say-so) + `release.ps1`.

## String inventory (Phase 2, rough)

- **Window/chrome:** app title, portable badge label, Install.
- **Toolbar/inputs:** Subnet label, Scan/Stop, Export (+ CSV/HTML items), Filter placeholder, device-count (`{n} devices found`, `{shown} of {total} shown`).
- **Status messages:** `Ready - {n} OUI vendors loaded`, Scan cancelled, Scan error, Export error/success, Override set/cleared.
- **Network info labels:** Local IP, Interface (Wi-Fi/Ethernet), Gateway, DNS.
- **DataGrid headers:** IP Address, Hostname, MAC Address, Vendor, Type, Open Ports.
- **Context menu:** Copy IP/MAC/Hostname, Ping, Open in Browser, RDP, SSH, Set Device Type (+ submenu headers), Clear Override.
- **ConfirmDialog:** its body text and buttons.

Roughly 70-90 keys. Device-type submenu Headers localize; their Tags stay English.

## Resolved decisions (from Steve)

1. **Languages:** English string breakout now; no other-language translation yet. Locale machinery still goes in so translations drop in later.
2. **Colors/assets:** use KillerPDF's exact colors and reuse its theme + accent asset files verbatim (see decision 1 above).
3. **Switcher UI:** KillerPDF-style flyout as the baseline, but exploit KillerScan's roomier toolbar (simpler app, less interaction). Best-judgment call: put the theme swatches + accent dots inline in the header toolbar where there is space, and keep the language picker behind a compact globe menu. Revisit once it is on screen.
4. **Export headers:** keep CSV/HTML column headers English for machine-readability; localize only on-screen text.
5. **Accent set:** the exact five KillerPDF overlays - Red, Blue, Purple, Orange, Teal over the Green base.

## Risk notes

- No compile feedback in this environment; phases are sized to be independently verifiable on your machine.
- `StaticResource` to `DynamicResource` is the main correctness risk; Phase 0 isolates and proves it before any theme is added.
- Per repo rules: no git operations, no reverting the working tree, atomic file edits only.

## UI overhaul backlog (Steve, in progress)

The surface-hierarchy overhaul spawned a running list. Done items pruned as we go.

Done:
- Tiered surface hierarchy (chrome / bar / pane) from KillerPDF's exact grays.
- Titlebar + toolbar merged into one chrome unit; borders removed; footer top border removed.
- Grain across titlebar, toolbar, and footer.
- IP readout redesigned (labels + accent interface chip, no terminal box).
- Devices pane drop shadow + dedicated `PaneBorderBrush` (darker in Black); shadow z-ordered over the footer.
- Export moved into the pane as an icon; device count leads with the scanned subnet.
- Black theme retuned: dark borders + subtle tiering (table lines + input borders no longer harsh).
- Filter box: compact, filter icon + placeholder.
- Window chrome: rounded corners, native shadow, fade-in. Version 1.5.0.

Queued (rough priority):
1. Smarter detection - keep classifying after the first match and report multiple roles (e.g. "Router, DNS Server"); router/gateway takes priority. Affects the Type column rendering.
2. Typewriter font (`typewriterA602`) - register it; apply to the wordmark (Killer + Scan split color, drop the `ks` monogram) and the "Discovered Devices" heading.
3. About dialog on version-click + update checker - port from KillerPDF (About.cs + the updater).
4. Inline accent picker - clicking a theme swatch reveals its accent hues right there (website-style), plus the 5 accent overlays for Dark/Light/Black (Phase 1b). A compact language menu; no centralized settings panel.
5. Per-theme device-type colors (the hardcoded Router/Windows/etc. colors).
6. Tabs - multiple scan sessions; pane indented into a tab shape with a (+) to add. Export already moved to the pane in anticipation.
7. Scrollbar moved out of the table to the pane's right edge.
8. Borders audit - keep darkening any remaining too-bright borders per theme.
9. HTML export (later): themable, responsive, user-reorderable columns + color options.
10. i18n scaffold (Phase 2) once the UI settles.
