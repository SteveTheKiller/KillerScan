# Adversarial remediation evidence, October 20, 2026

Implementation is in progress. Builds and controlled fixtures do not close rendered, native interruption, installer, Narrator or release gates.

## ENG-004 diagnosis

Ranked hypotheses before the controlled cancellation test:

1. The MAC phase awaits every synchronous native lookup without a cancellation-aware wait.
2. The batch schedules every discovered host without a concurrency bound, so canceled work remains queued.
3. Discovery or hostname work, rather than MAC lookup, holds the canceled scan open.

The fixture uses the real public subnet-scan API, loopback hosts, an isolated discovery transport and the existing platform boundary. Native resolution blocks on an owned release signal. Stage events and native-entry signals distinguish the MAC phase from discovery and name lookup. The fixture releases all blocked calls before restoring the platform.

Red: both target frameworks failed the 500 ms caller-completion check while native lookup remained blocked. The confirmed cause is the uncancelable batch wait and unbounded native scheduling. The repair shares eight native slots across full and deep scans; a canceled caller stops waiting, queued work checks cancellation before entering native code, and each started native call releases its slot only when it returns.

Green: the blocked-native fixture passes on net48 and net10. Caller cancellation finishes within 500 ms, at most eight native lookups remain active, a second canceled scan starts no extra calls, releasing native work does not launch canceled queued calls, no late completion events appear, and a fresh scan succeeds. The full 14-check suite also passes on both targets after this repair.

## ENG-003 diagnosis

Ranked hypotheses before the controlled UDP test:

1. The backward tag search interprets a long-form BER length byte as the payload length.
2. A byte pattern inside a valid description is mistaken for a later OCTET STRING.
3. The response belongs to another request or OID, but the parser never checks that identity.

The fixture owns loopback UDP port 161, reads the real query and replies with bounded SNMPv1 sysDescr responses. It tests the engine probe API with distinct request IDs, OIDs, error states and malformed BER containers. No real SNMP service is queried.

Red: both targets failed exact preservation of a valid 128-byte description. The backward search treated the long-form length marker as a payload length. The decoder now validates every definite-length container, the SNMPv1 version/community, response PDU, fresh request ID, zero error/index, single sysDescr OID and OCTET STRING, with no trailing bytes. The connected UDP socket accepts only the queried source endpoint.

Green: both targets preserve lengths 0, 1, 3, 4, 33, 127, 128, 188, 255 and 256, a description equal to the community and embedded tag-like bytes. Eleven malformed/identity variants are rejected. A forged response from a different UDP source port is ignored before the legitimate source responds. The full 14-check suite passes on both targets after the repair.

## ENG-008 diagnosis

Ranked hypotheses before the presentation fixture:

1. CLI table cells carry network-supplied controls directly to the terminal.
2. ANSI coloring wraps untrusted content without escaping it first.
3. Embedded presentation follows a separate sanitation path, so fixing one sink leaves inconsistent evidence in the other.

The fixture supplies clear-screen, cursor, OSC, BEL, CR/LF, tab and C1 bytes as device fields to the actual table/presentation APIs. It checks the allowed application ANSI sequences, row counts, visible escaped evidence and raw JSON round trips.

Red: the CLI table emitted untrusted control bytes in both the interactive and redirected presentation route. The repair visibly encodes C0, DEL and C1 before measuring or clipping cells, shares the same escape function with embedded presentation and leaves structured data unchanged. Terminal parsing checks also verify that network text cannot change the title or dispatch the application's speed-test OSC command.

Green: terminal-safety, narrow/wide scan-presentation and CSV-export regressions pass. Both ANSI and plain CLI tables retain their expected row count. Feeding CLI and embedded output through the real terminal parser leaves the existing title unchanged and dispatches no application command. JSON retains the exact hostile input string.

## UI-005 diagnosis

Ranked hypotheses before the watch-command fixture:

1. IncludeTarget returns immediately for an active run, discarding the requested address.
2. ShowDetails cannot create or select a missing card and delegates to that same early return.
3. The polling loop requires a fixed target set, preventing safe additions without restarting counters.

The controlled WPF fixture uses demo replies and diagnostics, so the actual IncludeTarget, ShowDetails and Start/Stop button commands run without network traffic. It observes cards, counters, diagnostic rows, selection and the target box through the displayed model/control boundaries.

Red: a started watch silently ignored IncludeTarget and created no requested card. The polling loop already snapshots its mutable target list each pass, so adding a card and its target safely preserves the existing samples. New targets run their own diagnostic sweep under the existing check token and concurrency slots. Diagnose then selects the newly added card.

Initial green: the controlled run adds and diagnoses new targets, retains existing card identity and growing counters, deduplicates repeat requests and stops through the actual button command. The integrated scan lifecycle/export fixture also passes. Extended checks cover the 16-target limit and visible rejection without changing accepted targets.

## UI-006 diagnosis

Ranked hypotheses before the export-action fixture:

1. Services exposes both generic device CSV and service CSV with the same shortcut.
2. Menu handlers call writers directly while keyboard commands use context dispatch.
3. The shell's static all-format tooltip advertises unsupported exports for the current view.

The fixture drives actual export menu-item events and the same context command called by keyboard shortcuts. Save dialogs are redirected to scratch files. It compares the visible service rows with exported rows and verifies unsupported formats do not open a dialog.

Red: Services offered two visible Ctrl+E entries. Generic menu handlers bypassed context selection, while the command route already distinguished service CSV from device CSV. The repair routes menu and shortcut actions through the context command, shows just service CSV in Services and derives the rail tooltip from the active context.

Green: the actual Services menu and keyboard command produce identical filtered service CSV with one row per visible port. Unsupported service HTML opens no dialog, and the tooltip lists only CSV. Devices still exports the device schema; Topology exposes its three snapshot actions; Watch, Terminal and History route to their own shell writers. The integrated scan lifecycle, export-provenance and watch-action fixtures pass. Rendered menu/tooltip inspection remains separate acceptance evidence.

## Current evidence

| Finding | Local commit | Focused evidence |
| --- | --- | --- |
| ENG-002 | d68f0f7 | Concurrent first load, six reloads with MA-L/MA-M/MA-S readers, failed-reload preservation on net48/net10 |
| ENG-009 | a44a41e | Partial output, truncated staging, denied replacement, first creation and successful publication on both targets |
| UI-011 | 8b2fffd | Real-store sentinels stay unread and unchanged in demo mode; normal persistence still works |
| ENG-007 | 62128dc | IPv6 and malformed CIDR rejected; omitted /24 and empty /31-/32 retained on both targets |
| UI-010 | 1fe1723 | BEL/ST, every byte split, UTF-8 titles, prompt/title integration and bounded payload |
| ENG-005 | f8634b9 | HTTP/TLS connections close within the 500 ms fixture limit; no later candidate requests on both targets |
| ENG-006 | 09e6dd9 | No requests to another loopback IP or DNS redirect; relative, local-port and HTTP-to-HTTPS redirects retained; loops stop at two hops |

Fingerprint fixtures now call the engine APIs directly (64bfe9e). The discovery transport boundary (14788d2) lets the complete 14-check engine suite run without LAN multicast. All 14 checks pass on both targets at that checkpoint. Production's default constructor still uses the existing multicast transport.
