# Locations

Status: agreed in planning on October 21, 2026, and built in 1.8.4. "As built" at the end records where the build differs from this plan and what is still open.

Locations keep each customer site's saved scans, device names, approvals, history and remembered usernames separate. Today all of that is global and keyed by MAC or IP, so two sites that both use `192.168.1.x` can share names, trust and SSH usernames (review finding BF-003). Locations fix that and replace the Scan Profiles list with Saved scans inside each Location.

## Decisions

| Topic | Decision |
| --- | --- |
| Placement | A Location picker beside the target box, with that Location's Saved scans under it. The rail order does not change. |
| No Location picked | A Temporary scan. Opening the app and pressing Scan still works with no setup. |
| Existing data | Moves untouched into one Location named Unassigned. Nothing is guessed or split. |
| First scan in a Location | Devices become Known, not Approved. Approval is a separate, explicit step. |
| Recognizing a site | KillerScan may suggest a Location from the network you are on. It never switches by itself. |
| Extras in the first release | Export and import a Location, per-Location notes, and an adapter preference on each Saved scan. |
| SSH usernames in exports | Left out unless a checkbox is ticked. Passwords are never stored or exported. |
| Contributor PRs #7 to #9 and #15 | Reference only. The code is written fresh. |

## Terms

- **Location**: a customer site or named network workspace. It has a stable ID, a renamable name, an optional customer label and free-text notes. It owns its Saved scans, device annotations, approved devices, history, remembered usernames and retention setting.
- **Saved scan**: a repeatable scan inside a Location. It has a stable ID, a renamable name, the full target text, scan settings, the Deep Scan choice and an optional adapter preference. It replaces today's Scan Profile.
- **Run**: one execution of a scan. It records the Location ID, Saved scan ID (if any), full targets, settings, deep or normal pass, adapter actually used, start and end time and completion. History already stores targets, settings, deep pass and completion; this adds the IDs, the adapter and an end time.
- **Device annotation**: something a technician said about a device in one Location, such as a name, type override, approval or SSH username. A MAC or IP is a lookup key, never proof of identity.
- **Temporary scan**: a run with no Location. It reads no annotations and saves no history until it is assigned to a Location.

## How it works for the technician

1. The picker beside the target box shows the current Location, or "Temporary scan".
2. Choosing a Location sends no network traffic. It fills the target box and settings with that Location's last-used values and lists its Saved scans.
3. Clicking a Saved scan loads it into the target box and settings. Running it is a separate action (Run, Enter, or the Scan button), so loading and scanning stay distinct.
4. **Save scan** updates the loaded Saved scan in place. **Save as** creates a new one. A name that already exists in the same Location asks before replacing, and never resets fields the dialog does not show.
5. Saved scans and Locations can be renamed and deleted from their context menus. Renaming changes only the display name; IDs and ownership stay put.
6. The active Location and the adapter used are shown near the scan controls and included in every export.
7. After a Temporary scan, **Save to Location** assigns the results (history entry and any names you typed) to a chosen or new Location. Nothing is approved by doing this.

## Known and Approved

- **First observed**: seen for the first time in this Location in this run.
- **Known**: seen before in this Location's history. The first completed scan in a new Location makes everything it found Known.
- **Approved**: a technician explicitly approved it, per device or with "Approve all shown".
- **Unknown**: outside this Location's Known list. It means "new since the baseline", not "intruder".

This replaces today's behavior where the first scan trusts everything (BF-004 context). The existing trust mark becomes the Approved mark. Removing the last approved device leaves the list empty; it never re-approves on the next scan. Whether a baseline exists is stored separately from how many devices are in it.

## Suggesting a Location

- When the active network matches a Location's recorded fingerprint, a banner offers it: "This looks like Acme Dental. Switch?" with Switch and Dismiss.
- The fingerprint is what earlier runs in that Location saw: gateway MAC, gateway IP and subnet, plus SSID on Wi-Fi. A suggestion needs the gateway MAC to match; subnet alone is never enough, because MSP sites reuse addresses.
- More than one match lists them all and picks none.
- Dismissing hides the suggestion for that network until the app restarts.
- A suggestion never switches the picker, sends traffic or changes any data by itself.

## Notes

- Each Location has one free-text notes field (gateway, VLANs, quirks, contacts).
- Notes are plain text, saved with the Location, included in Location export, and left out of scan reports unless a report option is added later.

## Adapter preference

- A Saved scan can name a preferred adapter (Ethernet, Wi-Fi or a VPN), stored by friendly name and description, not interface index.
- At run time the preference is resolved again on this machine. If no matching adapter is up, the run asks before falling back to the system's normal route and never falls back silently.
- The adapter actually used is recorded in the run and shown in exports.
- Binding traffic to an adapter is engine work (backlog B04). See "As built" for how a bound scan treats on-link and routed targets.

## Export and import

- **Export Location** writes one `.ksloc` file (JSON) containing: name, customer label, notes, Saved scans, device annotations except usernames, approved and known lists, and history (optional, ticked by default).
- An **Include SSH usernames** checkbox adds remembered usernames. It is off by default. Passwords are never stored, so they never travel.
- **Import Location** always creates a new Location with new IDs, even when the name matches an existing one, then offers to rename it. It never merges into an existing Location in this release.
- The file carries a format version. Unknown newer versions are refused with a clear message.
- Imported files are data only: nothing in them runs, opens a URL or starts a scan.

## Storage and migration

The five current stores are global: `profiles.json`, `history.json`, `devices.json`, `overrides.json` and `logins.json`.

- A new `locations.json` catalog holds each Location (ID, name, customer label, notes, last-used target and settings, fingerprint).
- Per-Location data lives in `locations\<id>\` with the same five stores, reusing `JsonPreferenceFile` and `HistoryArchive` with their mutex, reread-before-write and failure reporting unchanged.
- Every run captures its Location ID before it starts. Results that arrive after the user switches Locations still go to the Location they started in.
- **Migration** runs once on first start of the new version:
  1. Copy the five existing files into `locations\<new id>\` for a Location named Unassigned. Saved scans come from `profiles.json` unchanged, including their settings and Deep Scan choice.
  2. Validate the copies, then publish the catalog last. If anything fails, the app keeps using the old files and reports why; the next start retries.
  3. Leave the original files in place, untouched, as the backup. A later release may offer to remove them.
- Old global approvals are never copied into any other Location. Moving devices or history out of Unassigned is an explicit action.

## What gets fixed along the way

- BF-003: names, approvals and usernames no longer cross between sites that reuse addresses.
- BF-004: removing the last approved device no longer re-approves everything.
- BF-017 follow-through: Save versus Save as replaces "same name silently replaces".

## Acceptance checks

- Two Locations with the same IPs, MACs and Saved scan names keep names, approvals, usernames and history fully separate, including devices with no MAC.
- Renaming a Location or Saved scan, then restarting, keeps all ownership.
- Two app windows on different Locations write concurrently without losing or crossing data.
- A scan started in Location A that finishes after switching to B records in A.
- Migration interrupted at each step resumes cleanly; a corrupt catalog is reported without touching the original files.
- The first scan in a new Location makes devices Known and approves none.
- A suggestion appears only on a gateway MAC match and never switches by itself.
- Export without the checkbox contains no usernames; import always creates a new Location; a newer format version is refused.
- A missing preferred adapter asks before using the normal route, and exports record the adapter actually used.
- Temporary scans read no annotations and save no history until assigned.
- `/scan /location <name>` records into that Location; an unknown or ambiguous name exits 2 without scanning, through both the CLI and the PowerShell module.

## Further decisions

1. **Tabs:** picking a Location changes only the active scan tab, so two sites can be open side by side.
2. **Saved scans list:** in the Location picker's dropdown under the Location name, plus a "Manage Locations" item that opens the existing Profiles sidebar section, renamed Locations.
3. **History:** shows the active Location by default, with an "All Locations" switch; each row is labeled with its Location.
4. **Command line and terminal:** `/scan` and `/probe` take `/location <name>`, and the PowerShell `scan` and `probe` commands take `-Location`. With it, the run reads that Location's names and approvals, records history there and uses its Known list. Without it, they stay Temporary scans. An unknown or ambiguous name is a usage error (exit 2), never a silent Temporary scan.
5. **Retention:** per Location, defaulting to the current global setting at migration.
6. **Keep Alive:** unchanged and not Location-aware in the first release.

## As built

Where 1.8.4 differs from the plan above:

- **One scan workspace.** The app has a single scan workspace rather than tabs, so "the active tab" is that workspace. Its Location drives the History and Saved scans sidebar lists, the status tooltip, SSH usernames and the terminal's Scan button.
- **Starting Location.** The workspace starts in the Location used last. After migration that is Unassigned, so an upgrade looks the same as before. A fresh install starts as a Temporary scan.
- **Changing Location clears the results on screen.** Otherwise renaming, approving or typing a row would write into the newly picked Location. A completed run is already in its own Location's history. A Temporary scan's results are nowhere else, so leaving them asks first and points to Save results to Location, which keeps them on screen in the Location they move to. Picking the Location the tab is already in changes nothing.
- **Migration across windows.** Migration holds a named lock, so two windows starting together migrate once; the second finds the catalog and loads it. A `locations\unassigned` folder left by an interrupted migration is moved to `locations\.deleted`, never deleted.
- **An unreadable catalog.** A catalog that is corrupt, from a newer version, or empty is reported at startup and its file is left as it is. Before migration has published a catalog, the app keeps using the original files. After it has, those files are a stale backup, so the app runs Temporary scans that save nothing until the catalog reads again.
- **Import checks.** Every store in a `.ksloc` file must be a JSON object or list, and the staged copy is opened the way the app will open it before the Location is added. A file that cannot be read adds nothing.
- **Device mark.** Green is approved, amber is new since the baseline, and the muted text color is known but not approved. A Temporary scan shows everything as known.
- **Suggestion.** Matches on gateway MAC only. Each completed scan records the MAC of the device at the gateway address in its Location. SSID and subnet are not used yet. Dismiss hides a gateway's suggestion until the app restarts.
- **Adapter binding.** Scan settings has a Network adapter choice (Automatic, or a connected adapter), and `/adapter` and `-Adapter` do the same from the command line. A chosen adapter sends pings, TCP and UDP probes, HTTP and TLS fingerprints, ARP and mDNS/SSDP from its own address. Saved scans keep the choice; a chosen adapter that is not connected asks before falling back to Windows' route. A bound scan reaches the adapter's own network, and routed targets only when Windows' best route to them already goes through that adapter (hosts behind a VPN adapter, for example). A target Windows would send out another adapter, or has no route to, is refused before any traffic, and the message names the adapter Windows would use. KillerScan never adds or forces routes. Name lookups still use the system resolver. Every run records the adapter it used.
- **Export.** History is always included. The only choice is the SSH usernames checkbox, because the existing dialog has one checkbox.
- **Command line.** Without `/location`, scans no longer apply any saved names or types, because those now belong to a Location. `/probe /location` applies names and types but records no history.
- **History.** "Show all Locations" is on the History button's right-click menu.
- **Temporary results.** "Save results to Location" moves a Temporary scan's history entries and typed names into a Location without approving anything or touching its baseline.

- **Manage Locations.** Opens a Locations section in the sidebar, with no rail button of its own so the rail order is unchanged. Each row switches (click or Enter), renames (F2), edits notes, exports or deletes (Delete). The old Profiles section is titled Saved scans and lists the current Location's saved scans.
- **Deleting a Location.** Asks first, removes it from the list, and moves its folder to `locations\.deleted\<id>-<time>` so it can be recovered by hand. A workspace in that Location becomes a Temporary scan. Nothing ever empties the deleted folder.

Still open:
- **Notes in reports.** Notes stay out of reports, as planned; there is no report option yet.
- **Retention.** Each Location keeps its own retention setting, copied at migration. The History settings dialog changes the current Location's only.
- **Real-app checks.** Migration, the picker, the banner and the adapter prompt are covered by automated checks with scratch folders, but none has yet been run against a real install's data or on hardware with several adapters.
