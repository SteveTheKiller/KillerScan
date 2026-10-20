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
