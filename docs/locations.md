# Locations

Use the location pin on the left rail to create and choose a named location. The
active name stays in the window title bar and the picker tooltip. A location has
an opaque, stable ID; renaming it keeps the same data. No location is inferred
from an IP range, MAC, adapter, SSID or gateway. Two sites may use identical
addresses and identical device identifiers without sharing saved data.

The selection applies to the application's workspace, including history, profiles
and its terminal scan toolbar. The catalog remembers the last explicit GUI choice;
it does not detect when you physically move to a different network. Choose the
correct location yourself before scanning.

## What is separate

Each location owns its scan history and retention policy, device names and trust
baseline, manual device types, saved SSH usernames and scan profiles. Passwords
and SSH keys are not stored. Profiles retain the location ID alongside their
targets, scan options and interface identity. A profile from another location is
refused rather than silently relabelled or run.

Changing location clears scan/service/topology results, targets and explicit
interface choices. It also closes the idle terminal and monitoring views after
confirmation, so old addresses are not shown as results of the new location.
Switching is disabled while a normal/deep/terminal scan or terminal command is
running. Stop that operation first. Scanner preference callbacks capture their
own snapshot, preventing late completions from using another location's names or
manual types. Existing engine static callbacks remain available for older hosts.

CSV/HTML device exports, service/history CSV, terminal text and Keep Alive text/CSV/HTML
include location metadata. Hosted scan JSON remains an array with optional
`LocationId`/`LocationName` fields on each device; unhosted engine results do not add
those fields. Topology HTML/SVG includes the name and ID, while the network node
shows the name in raster images. Image export filenames also carry the stable ID.

## Legacy files and recovery

At first use, the app validates the old JSON files, copies their original bytes
to `backups/before-locations-<timestamp>-<id>/`, then copies them to
`locations/legacy/`. The original root files are left intact. Only after copying
succeeds is the versioned `locations.json` catalog published. Unreadable JSON,
changed source data or conflicting interrupted copies stop migration instead of
overwriting them. The backup includes a `RESTORE.txt` note.

All old data belongs to **Legacy data (unassigned)**. The app cannot know which
physical site produced each old scan. It does not split or distribute the files
using network addresses. New named locations start empty. Old data remains
available by explicitly selecting the legacy group.

The normal root is `%LOCALAPPDATA%\KillerScan`. The catalog and all location
directories belong together: do not copy one location's files into another ID's
directory. Profiles/history with a mismatched stored ID are refused; unreadable
files are preserved. Catalog and preference updates use atomic replacement.

To roll back, close all copies, preserve the complete new root (including any
changes made since migration), and use the previous app with the unchanged root
files or the migration backup. Changes made after migration live under
`locations/` and are not automatically copied back into the old format. Running
the old and new apps against the same root does not synchronize their data.

## CLI and PowerShell

```powershell
KillerScan.exe /locations /json
KillerScan.exe /scan 192.0.2.10 /location <location-id> /json
KillerScan.exe /probe 192.0.2.10 /location <location-id> /json
scan 192.0.2.10 -LocationId '<location-id>'
probe 192.0.2.10 -LocationId '<location-id>'
```

Use an existing ID from `/locations`; unknown IDs are usage errors and never
create a location or fall back to a different one. The GUI terminal inherits its
location through `KS_LOCATION_ID`; explicit arguments take precedence. An
external CLI with no explicit/inherited choice uses the legacy group, independently
of the last GUI selection. CLI selection does not change that saved GUI choice.
Arbitrary shell tools such as `ping` retain their own behavior. A location is a
data boundary; it does not select an interface or restrict Windows routing.

## Isolated development

Set `KILLERSCAN_DATA_ROOT` to a separate directory before launching a development
copy. Locations, settings, terminal modules/font/prompt and the vendor cache then
use that root. Settings use its `settings.json` instead of the installed app's
registry key; the portable install button is unavailable in this mode. There is
no automatic scan on a normal launch. Do not use `/uiscan` unless you intend to
start scanning.

Retained tests: `SpeedTest.Tests.exe --locations` exercises migration, byte-for-byte
backups, malformed/interrupted storage, stable IDs, same-IP/MAC isolation,
preferences, history, profiles, leases and export escaping. `--release-ui` adds
picker/profile/reset/locking checks using fabricated devices. Engine checks cover
independent callbacks for concurrent scanners. Fixtures use temporary data and
loopback servers; physical network pilots remain separate.

`SpeedTest.Tests.exe --location-profiles-ui` uses the real create/rename/save/load/delete
GUI handlers with two same-name profiles in different locations, then starts a
fresh process to verify persistence and isolation. It never starts a scan. Its
temporary data root and registry are isolated. The same check verifies all 19
language dictionaries contain their own location/profile labels (including GUI
error guidance), exercises live language changes and checks classic dialog titles.
User-provided location/profile names are kept as entered across language changes.
