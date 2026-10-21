# Adversarial remediation evidence, October 20, 2026

Source remediation and final controlled integration are complete. The untraced combined suite passes all 47 checks, both engine targets pass all 14 checks, and the fresh solution build has zero warnings/errors. Builds and controlled fixtures do not close native interruption, installer, Narrator, supported-font/live shell or release gates.

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

Final Spec review found that the demo shell route still called RestartWith on every visit before forwarding Keep Alive or Diagnose. Ranked hypotheses: unconditional view-entry restart, target insertion failure, or queued-start timing. The expanded approved command fixture constructs the actual shell without showing it or starting a native shell, finds its watch through the content tree, and drives OpenNetworkTool. Settings remain redirected by the existing process fixture and all network replies remain fabricated.

Observed red: after a second shell Diagnose command the requested target never receives a sample within four seconds. The earlier standalone insertion checks pass. Restricting automatic demo startup to initial workspace creation preserves subsequent cards, counters and added targets; the separate explicit demo-reroll restart is unchanged. Green: the same actual shell route preserves object identity and sample counts, retains the earlier Keep Alive target, selects/checks the new Diagnose target, and deduplicates another Keep Alive request. The complete controlled scan-lifecycle/export suite passes. The fresh isolated source snapshot is committed source at 09cfb71 plus the owned runner/watch changes, excluding concurrent deployment/theme work, and builds with zero warnings/errors.

## UI-006 diagnosis

Ranked hypotheses before the export-action fixture:

1. Services exposes both generic device CSV and service CSV with the same shortcut.
2. Menu handlers call writers directly while keyboard commands use context dispatch.
3. The shell's static all-format tooltip advertises unsupported exports for the current view.

The fixture drives actual export menu-item events and the same context command called by keyboard shortcuts. Save dialogs are redirected to scratch files. It compares the visible service rows with exported rows and verifies unsupported formats do not open a dialog.

Red: Services offered two visible Ctrl+E entries. Generic menu handlers bypassed context selection, while the command route already distinguished service CSV from device CSV. The repair routes menu and shortcut actions through the context command, shows just service CSV in Services and derives the rail tooltip from the active context.

Green: the actual Services menu and keyboard command produce identical filtered service CSV with one row per visible port. Unsupported service HTML opens no dialog, and the tooltip lists only CSV. Devices still exports the device schema; Topology exposes its three snapshot actions; Watch, Terminal and History route to their own shell writers. The integrated scan lifecycle, export-provenance and watch-action fixtures pass. Rendered menu/tooltip inspection remains separate acceptance evidence.

## ENG-001 investigation

The approved contract retains sent-byte throughput with a valid acknowledgment required. The engine summary must describe that same contract. This is a documentation repair and controlled investigation, not an acknowledged-only algorithm change.

The later rendered Technical-page check exposed the same stale acknowledged-only claim in its English paragraph and all 18 translated versions. Those existing paragraphs now describe sent transport payload, including buffered/in-flight bytes at cutoff, with at least one valid acknowledgment during measurement. No runtime algorithm changed. The Technical page's translation cache version advanced. JavaScript syntax, the KillerScan locale-key/placeholder comparison and all 18 changed paragraph keys pass; unrelated family-site coverage findings remain outside this app scope.

The loopback fixture separately records the engine's serialized payload result, receiver-observed bytes and acknowledged bytes. One exact acknowledgment is followed by either a fully received unacknowledged request or a partially read request with a stalled receiver. It captures phase-end counts and drains buffered tails afterward. Separate cases retain no-ack rejection, normal budget behavior and user cancellation on both target frameworks.

Both targets reported 131,072 serialized bytes and 65,536 acknowledged bytes. The fully received stalled-request case also recorded 131,072 received bytes. The partial-reader case recorded 69,632 bytes at phase completion and 131,072 after draining the buffered tail. Measured intervals were approximately 508 to 546 ms for a 500 ms phase. No-ack rejection, normal exact byte budgets and cancellation all passed on both targets. The result remained frozen after draining. This confirms the chosen sent-byte contract and its buffering limitation; no algorithm change was made. The class summary, app README and engine README now agree.

## FAM-001 diagnosis

Ranked hypotheses before the extracted release-gate fixture:

1. Native restore/audit failures are overwritten by later commands because their exits are unchecked.
2. English text matching accepts a failed or malformed audit as clean.
3. A structured result can omit an intended project or report a feed problem without findings.

The fixture executes only the actual dependency-gate source block with the dotnet process boundary replaced. Restore/audit failures, malformed/missing project data, audit problems, vulnerabilities and clean output are tested in unique scratch directories. It cannot reach build, signing or publication.

Red: the actual release gate continued after simulated restore exit 42. The repair captures each native exit immediately, saves raw diagnostics, requires structured audit schema and app/engine coverage, and rejects audit problems or vulnerable packages before later release steps.

The extracted-gate cases pass under Windows PowerShell 5.1 and PowerShell 7 with rejection reasons verified, including transitive vulnerabilities, unsupported schema and incomplete package data. Actual native stderr/exit fixtures verify diagnostic preservation and error-preference restoration for both commands. The real extracted gate also completed a fresh solution restore and structured audit covering the app, engine, engine tests and sample, with no known vulnerabilities reported. Diagnostics are retained in obj/release-validation. No later release steps ran. The existing five unsigned/dry-run publication-guard fixtures also pass without reaching real publication.

## FAM-002 diagnosis

Ranked hypotheses before the monitor fixture:

1. Empty NUGET_ID disables the entire package row.
2. Missing publication receives the wrong review-window or failed-run outcome.
3. A literal engine-project version comparison would misread its MSBuild expression.

The fixture executes the workflow's actual inline monitor with only HTTP, Git and GitHub process boundaries replaced. It preserves the configured environment, uses app/engine project source, and captures the generated channel table under scratch. No external services are contacted.

Red: the published-package fixture generated a table without any NuGet row. Setting NUGET_ID to killerscan.engine enables the existing version-specific package lookup. ENGINE_PROJECT_PATH stays intentionally empty because the engine derives its version from the app project rather than a literal XML version. The package is compared with the requested release version.

The six monitor scenarios pass under PowerShell 7. Cross-version fixture execution also exposed PowerShell 5.1's different empty-JSON-array pipeline behavior; the fallback now keeps only entries with a run status before deciding pending versus missing. A real MSBuild property evaluation confirms the engine version resolves to 1.8.4, matching the current app version.

## FAM-003 diagnosis

Ranked hypotheses before the disposable Publish fixture:

1. The bundler returns early when a versioned archive already exists.
2. Publish ignores bundler failures and can report success with an incomplete source deliverable.
3. Native file replacement or source discovery can fail after damaging the previous archive.

The fixture clones the actual BundleSource target into a minimal disposable MSBuild project and uses the actual bundler. Its build artifact copies a tracked input, making paired binary/source freshness directly observable across same-version Publish runs. It also tests destination locks and tracked Unicode paths without publishing the app.

Red: the second same-version Publish copied the changed source into its build artifact while the ZIP retained the first source. An adjacent fixture also showed quoted Unicode git paths could prevent the first archive from being created while Publish still returned success. The repair always creates a fresh staged archive, publishes it atomically, reports source-discovery failures and lets bundler failure fail the actual Publish target.

Initial green: actual repeated Publish refreshes both fixture build and source content. A locked destination makes MSBuild return failure while the prior ZIP hash remains unchanged; retry succeeds after the lock is released. Tracked Unicode filenames survive, neighboring output artifacts remain intact and an empty tracked-source set fails explicitly. Native interruption behavior beyond the controlled lock case remains a separate lab gate.

## FAM-004 diagnosis

Ranked hypotheses before the actual browser check:

1. Opacity and pointer-events hide the strip visually while leaving its buttons in keyboard navigation.
2. The closed strip remains in the accessibility tree because no inert or hidden state is set.
3. Closing while focused inside leaves focus stranded on an invisible control.

Red: on the local index page, the browser accessibility tree exposed all eight accent controls while the trigger was collapsed. Two Tab presses from the accent trigger moved focus to the invisible Red control. The repair makes the strip inert and aria-hidden at initialization and close, restores trigger focus when closing from inside, and removes inert state only when opening. Existing CSS geometry and transitions are unchanged.

Green: the actual browser tree excludes closed accent controls, and the same two Tab presses now reach the visible Download link. Opening exposes all eight choices. Escape from Red closes the strip and focuses its trigger. On Help, Technical, About and MCP, initial closed state excludes the choices, opening exposes them, and Close returns trigger focus. The open picker was visually inspected. All five page references received a cache-version update. The required landing-page parity script passes for five sites and four shared carousel copies. Other app sites were not changed.

## Current evidence

Final reconciliation distinguishes source/component acceptance from native or release acceptance:

| Finding | Local source repair | Acceptance boundary and remaining gate |
| --- | --- | --- |
| UI-001 | 221959a | Real archive transaction/reload and controlled cross-process writers pass |
| UI-002 | 1a721df | Preference failure/retry and persistence fixtures pass |
| UI-003 | bca4a05 | Successful empty run saved; canceled/failed runs excluded |
| UI-004 | 9b924cd | Automatic deep final snapshot and explicit completion statuses pass |
| UI-005 | 3fa88a3, 8d853bd | Active standalone and actual demo shell command fixtures pass |
| UI-006 | 6576252 | Context menu/shortcut export files pass; native rendered menu acceptance separate |
| UI-007 | 6122f52 | Frozen target/input/provenance export fixture passes |
| UI-008 | 4a3d805, 4ad6ccb | Actual TextPattern peer and mouse range checks pass; Narrator/native client gate open |
| UI-009 | 3d28c13 | Bounded memory investigation resolved for measured presets; unlimited-duration behavior unmeasured |
| UI-010 | 1fe1723 | BEL/ST and split UTF-8 parser/integration fixtures pass |
| UI-011 | 8b2fffd | Demo store isolation and normal persistence checks pass |
| UI-012 | 3c51238, 4508270 | Cluster/parser/render checks pass; supported-font/live shell/SSH acceptance open |
| ENG-001 | d2f2c17, 09cfb71 | Chosen sent-byte contract verified with buffering limits; app/engine/site descriptions agree |
| ENG-002 | d68f0f7 | Atomic OUI snapshot readers/reloads pass on both targets |
| ENG-003 | 26ceb19 | Bounded BER, exact identity and source checks pass on both targets |
| ENG-004 | affd6ad | Shared bounded native slots and prompt caller cancellation pass on both targets |
| ENG-005 | f8634b9 | Active HTTP/TLS cancellation loopbacks pass on both targets |
| ENG-006 | 09e6dd9 | Same-IP redirect and rejection cases pass on both targets |
| ENG-007 | 62128dc | IPv4 CIDR and documented /31-/32 behavior pass on both targets |
| ENG-008 | 5bf587b | CLI/embedded terminal control escaping preserves raw structured data |
| ENG-009 | a44a41e | Atomic vendor publication/fault fixtures pass on both targets |
| DEP-001 | Owner 2892599 | Bounded updater recovery source/component packet accepted; native interruption gate open |
| DEP-002 | 3997a3e | Registered completion protocol, rollback and caller fixtures pass; native lifecycle open |
| DEP-003 | 3997a3e | Owned-image cleanup preserves unrelated files, rejects links and restores failures; native acceptance open |
| DEP-004 | 22afee5 | Trusted installed helper and protected immutable publication implemented; rejection/parser fixtures pass; native acceptance open |
| FAM-001 | ff760ae | Thirteen audit rejection/clean cases pass under PowerShell 5.1/7 |
| FAM-002 | 137dd1b | Six monitor cases pass under PowerShell 5.1/7 |
| FAM-003 | 53a68f2 | Actual disposable Publish freshness/lock/Unicode fixtures pass under both hosts |
| FAM-004 | a51c409 | All five navigation pages browser-verified; landing parity passes |

The fresh solution build at b2ea71f completed with zero warnings/errors. Both complete 14-check engine suites passed from that output. Final solution vulnerability and direct-outdated queries covered four projects and reported no known vulnerable or outdated direct packages. These are checkpoint results, not acceptance of later concurrent deployment changes.

The requested Technical layout adjustment is committed as 87a2c23. Moving the entire Glossary section left reduced the original 4330/9858 pixel column imbalance to 7013/7162. After correcting the existing upload paragraph, the final browser measurements are 7013/7210 at the same viewport. Existing page geometry/styles are unchanged. The preserved screenshot is C:/Users/steve/.codex/visualizations/2026/10/20/01a15b1f-63bf-7282-aa9a-5bdafad13939/technical-balanced-final.png.

Deployment coordination is explicitly authorized. KillerShell owns its App.xaml.cs, update/removal helpers, Chocolatey script and focused deployment tests; no competing implementation is made here. Its intermediate missing-report compile error was reported and the owner confirmed correction. The watch regression uses an isolated committed-source snapshot, so its passing build does not accept owner WIP. All VM operations remain with the Bench-coordinated exclusive windows; this chat has made no guest changes.

## Deployment source acceptance

KillerShell returned the index after commits 3997a3ef67c8bbfd2d4511d6a01f91e867ca638c and 22afee50716cf9e2d22020333154435d5a44e272. Author and committer identities were checked. The bounded three-pass review found one low notification omission; its red/green repair and subsequent review are recorded in KillerHarness/validation/evidence/killershell-20261020/family-final/scan-deployment. The caller correction separately verifies that missing images and denied registry access cannot masquerade as successful Chocolatey removal. Independent acceptance compared all 17 task-owned files with that packet's source-manifest.json and found no remaining material source gap.

The registered QuietUninstallString owns final silent completion. Direct loaded-image /uninstall-silent returns 1618 without mutation, and the deployment guide documents that protocol. Removal preserves unknown contents, rejects linked paths, attempts image/registration restoration on failure and broadcasts final PATH state. Elevated updates use the protected installed helper, exact process identities, digest, signing certificate and newer product version checks before protected staging and immutable publication. Actual publication completion remains distinct from successful handoff.

The local --installation-cleanup dispatch runs only GUID-owned filesystem fixtures and generated-command parsing. It passes preserved-report, empty/absent directory, locked-image, rollback, directory lease, junction, all user/machine silent/interactive command parsing and untrusted-update rejection cases. The actual Chocolatey caller fixture passes all four cases under both Windows PowerShell 5.1 and PowerShell 7. It replaces only the registry read boundary and never launches removal. Integration dispatch is committed as 64323ec. Native lifecycle, UAC refusal, alternate administrator, denied/locked/disconnected storage, interrupted/concurrent operations, tampering and final signed artifacts remain open with the lifecycle owner.

## Combined-suite timeout repair

The resumed investigation froze cdbdc3d source and added scratch-only scheduling and HTTP diagnostics. A combined --worker run reproduced AdaptiveCancel's timeout at RunAsync line 44, after successful preflight but during an idle sample. It had one accepted connection, two parsed zero-byte requests and two completed responses, with context none, 45 process threads and about 52.7 MiB private memory. Independent source inspection found no direct preceding-suite mutation of context, proxy, connection limits or thread-pool limits. Framework tracing showed successful zero-body exchanges could spend 1140-1304 ms before header parsing, then complete EOF/disposal within about 0.5 ms.

The reduced parent-predecessor/transfer run reproduced the same timeout. Its server received the second request's first byte at 42 ms but had not parsed that header when the client timed out at 2298 ms. Other headers took 466-821 ms between first byte and parsing. The fixture awaited a separate NetworkStream read for every byte. An 8192-character endpoint path, still inside its existing 16 KiB header limit, amplified the same bottleneck into an observed Timeout before any complete request. This failure is intermittent: the permanent long-header check also passed once with the old reader, so a single passing old-fixture run does not disprove it.

The bounded repair wraps only incoming reads in one BufferedStream per connection. Headers and upload bodies share that reader so prefetched body/next-header bytes are retained. Existing raw response writes, protocol bounds, engine code and two-second request timeout remain unchanged. Independent review found no buffering/ownership blocker. Scratch-only diagnostics and framework trace configuration are excluded from the checkout.

Green: eight amplified header/reuse repetitions pass; warmed pairs complete in about 7-19 ms. The original reduced parent/transfer sequence now passes, and header parsing generally takes 0-1 ms after first byte. The permanent --loopback-headers check and all 29 speed/Worker checks pass from committed deployment source plus the owned runner change. The fixture repair is committed as dfb3625. The solution build at 64323ec source completes with zero warnings/errors. The final combined run passes all 47 checks, including the two newly integrated deployment groups, without framework tracing or temporary diagnostics. Concurrent UI/font/grain WIP is preserved and excluded from this committed-source snapshot.

## Final controlled acceptance

The final snapshot combines deployment baseline 22afee50716cf9e2d22020333154435d5a44e272 with Program.cs from 64323ec09c3dcb8e53beed21332d148fd93723ed. The runner source hash is f82cf8e51646619fcc7d00b17573d38520412a3d77507199d6ffdbdb79fe8a3f and executable hash is 0944cea06176e3a9ba708b65365b55a697dc5de5b2b9875873fbb6adffbc083f. The complete solution and runner builds each finish with zero warnings/errors. Source-resource discovery is explicitly pointed at the matching snapshot.

The ordinary combined --worker run passes all 47 checks with exit 0. It includes 78,454 topology assertions, 1,197 history/context/shortcut assertions, the controlled deployment groups, the permanent long-header regression, real transfer/error/cancellation checks, the actual local Node Worker and offscreen terminal rendering. Both complete 14-check engine suites pass from the same final solution build on net48 and net10.0. No public internet speed test or real installation/removal is performed.

Fresh structured dependency queries cover the app, engine, engine tests and sample. The transitive vulnerability query reports no known vulnerable packages; the direct-outdated query reports no outdated packages. Both exit successfully with four project records and no reported query problems. Dependency updates remain c7f331a and b2ea71f.

KillerShell's older production chat was stopped by the user to avoid overlap with the active KillerShell chat. It handed the remaining KillerScan native acceptance back to this chat and withdrew its queued window. The active KillerShell chat, 01a15b8b-9c13-7b21-bcd7-4b5e474128c0, confirmed that it retains only its own KillerShell plan. KillerScan requested its separate acceptance window directly from KillerBench, the VM board coordinator. The current board assigns the guest to KillerBench. No guest changes are made here, and native acceptance cannot be claimed until the coordinated handoff and prerequisites are available. Narrator/native UIA, supported fonts and live shell/SSH editing remain separate manual gates.

## Final-review follow-ups

These paragraphs retain earlier checkpoints. The resumed diagnosis and latest acceptance are recorded in the sections above.

The integrated runner first failed before any full-suite check because source-resource lookup only searched ancestors of its executable. Isolated artifacts are outside the checkout. The test-only lookup now shares the existing explicit KILLERSCAN_TEST_ROOT boundary across all source-dependent fixtures, validates its project marker and retains ancestor discovery for normal in-repository builds. Product source, runtime paths and test assertions are unchanged by this runner correction.

The next full-run attempt passed menu/startup checks, then exposed a legacy reflection call to LoadSnapshot that did not supply the new optional timestamp parameter. Topology now calls the actual workspace API directly, preserving the existing assertions and synthetic inputs.

The subsequent integrated run passed 78,454 topology assertions, 1,197 history assertions and the next controlled protocol checks, then the existing NoPayload fixture returned Timeout instead of expected TransferFailed. Ranked hypotheses before focused rerun: (1) stalled fixture I/O delays the short phase cancellation, (2) warmup and measurement share lingering server connections, (3) the fixture reaches request timeout rather than its intended zero-byte phase cutoff. Isolated empty-transfer and actual Worker entry points preserve the original assertions and allow independent diagnosis without repeating rendered suites.

Both focused empty-transfer cases and the actual Node Worker loopback passed in fresh processes. A subsequent full --worker attempt again passed the 78,454 topology and 1,197 history assertions but stopped earlier at AdaptiveCancel's latency preflight with request Timeout. This is a distinct observed failure; it does not establish that NoPayload is fixed. Phase/request diagnostics were retained in Reject for a subsequent discriminating run. Full app integration remains open and no timeout or assertion was weakened. Runner corrections are committed as 1fefa5d.

The minimized five-check --transfer-sequence passed both adaptive modes, adaptive cancellation, empty download and no-ack upload in one process. The broader --speed-loopback --worker then passed all 28 existing speed/Worker checks in one process without the preceding rendered UI groups. Both runs used the isolated committed-source snapshot plus owned runner/watch changes. This narrows the failure to a condition absent from those runs; it does not prove which UI state, resource pressure or timing caused the full-run timeouts. The test selector preserves default ordering/coverage and unchanged assertions/time limits; independent Standards source comparison found no orchestration regression. Full combined acceptance remains open pending a reproducible diagnosis and final owner-source integration.

The independent Standards review traced a right-edge VS16 promotion when DECAWM is disabled. Ranked causes before reproduction: (1) promotion clamps to total columns instead of remaining columns, (2) wrapping-disabled output still tries to allocate a continuation, (3) promotion loses the cluster. The focused parser regression uses four columns, ESC[?7l and abc + heart + VS16. The intended bounded fix preserves the cluster in the remaining cell without wrapping or writing outside the row.

Observed red: the exact parser input threw IndexOutOfRangeException. Promotion now clamps to columns remaining after its leading cell. Green: the complete cluster remains at the right edge without wrapping; the complete Unicode, UIA and OSC focused suites pass. This is a review-discovered regression in the new width model, corrected before final acceptance.

The Spec review found a mouse-origin selection discrepancy at a wide character's continuation cell. Ranked causes before reproduction: (1) clipboard expands to cluster boundaries while UIA uses raw endpoints, (2) reverse selections normalize differently, (3) drawing and copying disagree on selected width. SelectCells is the real mouse-drag cell-selection operation; the fixture selects the continuation half of CJK text and compares clipboard-source text with TextPattern. No host input or clipboard write is used.

Observed red: continuation-origin selection copied the CJK character while TextPattern returned an empty range. Ordered selection endpoints now expand to complete clusters before rendering, copying or UIA mapping. Forward and reverse selection use the same normalization.

Green: the actual terminal TextPattern fixture passes both forward and reverse continuation-origin selections, with copied text matching the accessible range. Unicode/parser neighbors also pass.

Standards source re-review reports no remaining hard finding after the right-edge repair. Spec source re-review reports no remaining concrete gap in the mouse-range or demo shell-watch fixes after 4ad6ccb and 8d853bd. These independent source reviews did not rerun the fixtures or provide native mouse, Narrator, live shell or installer acceptance.

## Dependency compatibility inventory

System.Text.Json 10.0.13 is already applied to app/engine. Official repository release/tag API checks on October 20 identified checkout v7.0.1 (3d3c42e5aac5ba805825da76410c181273ba90b1), setup-dotnet v6.0.0 (a98b56852c35b8e3190ac28c8c2271da59106c68), upload-artifact v7.0.2 (cf430e030ddbb5b0abf93d22962f4752f3646cd9) and NuGet/login v1.2.0 (8d196754b4036150537f80ac539e15c2f1028841). Every workflow reference is pinned to its verified commit.

All four actions use Node 24. These workflows use GitHub-hosted Windows 2025, windows-latest or ubuntu-latest runners. The upstream Node 24 minimum is runner 2.327.1; checkout's newer Docker-auth path requires 2.329.0, and these workflows do not use that path. Checkout v7's restricted fork behavior applies to pull_request_target/workflow_run; the PR workflow uses pull_request. Existing input/output names were verified against each pinned action.yml. Artifact ZIP behavior stays at its default and NuGet retains user/NUGET_API_KEY. No workflow was dispatched and no release was published.

Primary references: [checkout](https://github.com/actions/checkout/releases/tag/v7.0.1), [setup-dotnet](https://github.com/actions/setup-dotnet/releases/tag/v6.0.0), [upload-artifact](https://github.com/actions/upload-artifact/releases/tag/v7.0.2), [NuGet login](https://github.com/NuGet/login/releases/tag/v1.2.0). Hosted workflow execution remains unverified until separately authorized push/CI.

## UI-009 measurement protocol

The approved unlimited-retention policy is unchanged. Ranked questions before measurement: (1) retained cells dominate steady private memory, (2) cloning on shell replacement creates the practical peak, (3) repeated replacement or resize retains obsolete copies. A child waits on stdin until assigned to a Windows job with a 384 MiB committed-memory ceiling and kill-on-close. No allocation workload starts if the job assignment fails. Presets are 20,000 x 80, 50,000 x 80 and 25,000 x 160 output cells, not an allocation-until-failure stress test. Each includes three replacements, width changes and an alternate-screen round trip with full row-content verification.

Investigation budgets are 256 MiB steady private memory, 384 MiB replacement peak and 1,000 ms per replacement. They are local diagnostic thresholds, not a new product retention limit. The parent stops its owned child after 30 seconds. Measurements report process private bytes, job peak committed bytes, managed retained bytes and copy pauses. Results determine whether a follow-up storage-policy decision is needed.

The actual terminal-control preset additionally reads its complete transcript through TextPattern after each replacement. All four capped subprocesses completed with every expected row intact. Representative final run:

| Preset | Private MiB after output | Private MiB after third replacement | Job peak MiB | Replacement pause ms |
| --- | --- | --- | --- | --- |
| 20,000 rows x 80 | 71.6 | 65.7 | 108.1 | 43-88 |
| 50,000 rows x 80 | 134.8 | 122.0 | 238.2 | 161-204 |
| 25,000 rows x 160 | 130.5 | 120.8 | 233.4 | 142-252 |
| Actual control, 20,000 x 80, full UIA reads | 92.4 | 130.2 | 180.9 | 74-145 |

Resize and alternate-screen round trips preserved every row in the buffer presets. Three replacements did not produce continuing retained growth. These measured workloads stay within the stated budgets and do not establish a practical defect requiring a retention/storage change. Memory still grows with transcript length by design; this is not an unlimited-duration guarantee. Native shell/SSH throughput and very large UIA histories beyond these presets remain unmeasured.

## UI-012 diagnosis

Ranked hypotheses before the parser regression:

1. Print advances once per scalar instead of by terminal cluster width.
2. Rendering and copying independently assume one scalar per column.
3. Erase/insert/delete can leave partial wide characters unless both cells are normalized.

KillerShell has the same scalar-per-cell pattern and offers no compatible width implementation to reuse. Tests cover ASCII, decomposed accents, CJK, supplementary symbols, variation selectors, emoji modifiers, regional pairs and ZWJ clusters, including byte-split input and line edges. Width policy must be explicit because Unicode East Asian Width alone does not define terminal grapheme rendering.

Red: the actual UTF-8 parser advanced three columns for decomposed e + acute + X. The repair stores a cluster on its leading cell and reserves continuation cells for width two. Unicode 17.0 W/F properties define wide scalars; ambiguous scalars stay narrow. Combining marks attach, emoji VS16 promotes eligible bases, regional pairs and common emoji modifier/ZWJ sequences retain a two-column cluster. VS15 preserves the base's intrinsic width. This is an explicit terminal policy, not a claim of complete Unicode grapheme-conformance or color-emoji rendering.

Green: byte-split parser cases, edge wrapping/promotion, insertion, overwrite, erase, repeat, resize and shell-replacement fixtures pass. Selection copying and UIA use the same cell text; character navigation uses stored cluster boundaries. The actual control rendered decomposed accents, CJK, supplementary rocket, VS16 heart and a ZWJ emoji with aligned columns; its PNG was inspected. Full supported-font and live shell/SSH keyboard-editing acceptance remain open. Retention is unchanged. Fresh isolated builds are used when a source edit overlaps compilation, since incremental timestamps can otherwise hide the edit.

## UI-008 diagnosis

Ranked hypotheses before the UIA regression:

1. The custom FrameworkElement has no automation peer, so no transcript provider can be obtained.
2. The audit's Control-only filter hides the missing peer.
3. Selection and caret changes lack automation notifications even when the visual output updates.

The approved boundary is the actual terminal control and its UI Automation provider, without starting a shell or sending host input. Manual Narrator acceptance remains separate.

Red: CreatePeerForElement returned null for the actual terminal. Green: the peer exposes Document TextPattern, live output, text search, actual selection and the insertion caret. Text ranges support bounded reads, endpoint movement, cloning, viewport ranges, screen bounds and scrolling. Output/selection notifications coalesce over 200 ms and stop on disposal. The audit includes custom focusable FrameworkElements and records MISSING_PEER. A localized existing Terminal resource supplies the name. Narrator announcements, native UIA client behavior and focus rendering remain acceptance gates.

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
