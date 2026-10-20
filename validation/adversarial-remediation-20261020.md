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
