# Device recognition, evidence and confidence

Right-click a device or service and choose **Recognition evidence**, or press
**Ctrl+I** with its row focused. The type cell shows the confidence beside the
existing type. Saved history has the same read-only action. Opening evidence does
not probe a host, open a connection or refresh a saved result.

The details show the automatic type, contributing source families, alternatives,
raw observed values, observation times and limitations. A user-assigned type is
labelled **Manual** separately from the automatic inference. Clearing it restores
the stored automatic assessment; a user-renamed hostname does not become new
network evidence. Device names, types and saved assessments retain the existing
per-location boundaries.

## Meaning of confidence

Confidence applies to the suggested **type/role**, not to ownership, authenticity,
the exact hardware model or device availability. It is a heuristic grade, not a
calibrated probability, and no percentage is displayed.

- **Not available**: no sufficient evidence, or an older record without evidence.
- **Low**: weak hints such as a name, MAC vendor, ports, or conflicting candidates.
- **Medium**: a specific protocol hint, or the configured gateway/DNS role.
- **High**: at least two non-reused protocol families support the type, with a
  specific fingerprint and no close/strong contradictory candidate.
- **Manual**: a user assignment. The automatic assessment is still visible.

Candidate scoring retains the existing fingerprint catalog, but names contribute
weak hints instead of short-circuiting the protocol evidence. Repeated rules for
one source family contribute at most that family's strongest weight (capped at
15). Candidates need a score of at least 6. Ties use a deterministic type order
and lower confidence; scores are not probabilities. Network-category hints are
combined under Switch/AP, as in the previous generic-network result.

Missing replies, no open TCP ports or a locally administered MAC no longer imply
IoT, Android or Mobile on their own. An uncertain result can therefore change to
Unknown. OUI/vendor hints may suggest a broad role at low confidence but cannot
establish a model. Original MAC-prefix corrections are retained and the corrected
vendor is recorded when used.

All protocol strings are self-reported and may be wrong or spoofed. Even a High
type grade does not authenticate the device or its hardware. Related services can
also share an implementation; source-family counting is only a conservative
heuristic, not a claim of statistical independence.

## Models and time

A model is shown only for an explicit `model=` / `model:` / `modelname=` declaration
in captured SNMP, HTTP or SSDP values. It remains **reported (unverified)**. This
first version does not infer a model from a hostname, port, OUI or a product-looking
title, and does not use an online product catalog. Distinct model declarations
produce a conflict and no selected model. Declarations longer than 64 characters
are not truncated into a different model.

Actual scanner results record the per-host observation window in UTC and the
collection time for used multicast data. mDNS/SSDP reused by a deep probe keeps its
earlier time and is labelled cached; it cannot count as fresh corroboration for
High confidence. A hostname's DNS/mDNS/NetBIOS origin is recorded where known.
Network-wide discovery may precede the per-host activity window; its source time
is shown separately. An OUI lookup is labelled cached and has no invented live
observation time. Externally supplied or old data without capture times displays
them as unavailable. Assessment time and observation time are distinct.

Full, quick and deep modes and explicitly disabled probe choices are saved in the
assessment. Quick mode does not turn its missing port/fingerprint data into negative
evidence. Empty fields mean unavailable information; they do not distinguish a
timeout, a closed port, an unsupported protocol or a skipped operation. This change
does not add probes or claim per-attempt failure diagnostics.

## Storage, exports and API

`NetworkDevice.Identification` is optional and omitted for unassessed devices.
Version 1 contains `AutomaticType`, `Confidence`, `ManualType`, `ReportedModel`,
conflict flags, assessment/observation times, scope, disabled choices, observed
evidence, matched rules and ranked candidates. Source IDs and types are stable
language-independent values; UI labels are translated in all 19 supported languages.

`NetworkScanner.IdentifyDevice(device)` assesses supplied fields without I/O,
global routing hints or saved manual preferences. Existing classification entry
points still return a type and also populate the optional assessment. Normal
scanner entry points attach the scan context before the host applies saved names.

History takes a separate snapshot of the assessment, including independent rule
and candidate source arrays. Optional fields need no migration; older records
remain readable and are not retroactively assessed. Existing retention still
applies; evidence increases the size of saved scans. No real user history was
migrated during development.

- CLI JSON includes the nested assessment. PowerShell preserves it as
  `.Identification` without adding it to the default table view. The scan/probe
  wrappers no longer pass `/quiet` (which suppresses JSON too), and an explicit
  pipeline waits for the Windows GUI-subsystem EXE before parsing the output.
- Device CSV adds confidence, reported model and a quoted `Identification JSON`
  field after the existing columns; scripts relying on an exact column count must
  account for these additions. Service/history CSV also carries the structured data.
- HTML has expandable evidence within each type cell. All remote values are
  escaped as text. Observed strings are bounded, control characters are replaced
  by spaces, and an HTTP title/server share one source family.

The topology and terminal table layouts are unchanged. Structured JSON, CSV,
HTML and the details window carry the evidence rather than crowding those views.

## Isolated validation and demo

Engine regression checks run on net48 and net10.0 with fabricated observations
and isolated loopback fixtures. They cover weak hints, contradictory sources,
duplicate families, explicit/conflicting models, hostile/long values, manual
assignments, snapshots/JSON, full/quick/deep scope and cached multicast times.

`SpeedTest.Tests.exe --evidence-ui` exercises all 19 language dictionaries,
focused device/service selection, manual provenance, stored/legacy history and
escaped exports. It is also included in `--release-ui`. Run UI checks in an isolated
data root; retained test runners keep fabricated data separate from installed data.

`tests/Test-EvidenceModule.ps1 -ExePath <built-exe>` tests the real module/EXE JSON
round trip using `/demo` and a temporary data root, including usage-error propagation
for an invalid location before any probe can start. The separate scan-options
module tests retain the mocked argument-forwarding and metadata checks.

`KillerScan.exe --demo` shows fabricated devices. Printer and hypervisor examples
include synthetic protocol values; preset demo types that disagree with the
automatic inference are explicitly marked Manual. Demo capture times remain
unavailable. The CLI `/scan /demo /json` is a safe way to inspect the new JSON shape
without scanning a network. Neither a demo nor loopback fixtures replace a physical
pilot or native-speaker review of machine-assisted translations.
