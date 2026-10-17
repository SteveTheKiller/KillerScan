# Scan options

The gear button beside Scan controls application-level probes for that scan tab. The
terminal scan toolbar has its own gear button. Choices are locked while a scan or deep
rescan is running. Save a scan profile to remember the choices with its targets;
loading or running the profile restores them. Existing profiles keep all defaults on.

| Choice / CLI name | Effect when enabled |
| --- | --- |
| `neighbor-cache` | Read the existing neighbor cache before and after discovery. |
| `arp` | Ask the platform to resolve MAC addresses for discovered hosts, and on deep probes. |
| `icmp` | Ping for discovery and the TTL hint, including deep probes. |
| `tcp-liveness` | Try common TCP ports when ICMP does not answer during discovery. |
| `dns` | Flush the local DNS cache, then reverse-resolve and forward-verify hostnames. |
| `mdns` | Query multicast DNS and enrich discovered hosts. |
| `ssdp` | Query SSDP and enrich discovered hosts. |
| `ports` | Probe TCP ports: common ports for a normal scan, 1–1024 plus service ports for deep scans. |
| `http` | Read HTTP/HTTPS title and Server headers from discovered open ports. |
| `tls` | Inspect TLS certificates on discovered open ports. |
| `ssh` | Read the SSH banner when port 22 is open. |
| `snmp` | Query the SNMP description over UDP. |
| `netbios` | Query the NetBIOS name over UDP. |

Turning off `ports` also prevents HTTP, TLS-certificate and SSH probes because they
require discovered open ports. SNMP and NetBIOS are independent UDP probes. Turning
off TLS-certificate inspection does not disable HTTPS used by HTTP fingerprinting.
Quick mode still skips port scanning and all fingerprints, irrespective of their
checkboxes. Discovery still needs at least one of the neighbor cache, ICMP or TCP;
mDNS/SSDP currently enrich the resulting hosts rather than seeding discovery.

These choices do not change OS routing or select an interface. For example, enabled
TCP traffic may cause OS ARP even with the application's explicit ARP probe off.
Disabling probes may reduce discovered devices or identification quality. An absent
result does not prove a device is absent. Unrelated device actions, diagnostics,
Keep Alive, speed tests and arbitrary shell commands have their own behavior.

## CLI and PowerShell

`/scan` and `/probe` accept `/skip` (or `--skip`) with comma-separated names above.
Unknown or empty names return usage error 2 before starting the scan. `/ports` keeps
its existing meaning as an **output filter**; `/skip ports` disables probing instead.

```powershell
KillerScan.exe /scan 192.0.2.0/24 /skip mdns,ssdp,snmp,netbios /json
KillerScan.exe /probe 192.0.2.10 /skip dns,icmp /json
# Built-in PowerShell module:
scan 192.0.2.0/24 -Skip mdns,ssdp
probe 192.0.2.10 -Skip ports,dns
```

Manual CLI/module calls start with defaults and the supplied skip list, independently
of open GUI tabs. The terminal toolbar forwards its own selected options to the CLI.
No persistent global policy is applied to arbitrary terminal commands.

## Engine API

```csharp
var options = new ScanOptions { Mdns = false, Ssdp = false, Snmp = false };
var devices = await scanner.ScanSubnetAsync(targets, token, options);
var device = await scanner.DeepProbeHostAsync(ip, token, options);
```

`ScanOptions` is an immutable record. Replacing it with `options with { Icmp = false }`
does not alter a scan already using the original instance. Existing method signatures
remain available and enable all defaults. Options are per call rather than static.
The scanner clears previous multicast enrichment for every new subnet scan and filters
cached enrichment according to the deep probe's choices. A failed multicast collector
does not discard results from other sources.

Tests exercise each disabled choice at the probe I/O boundary in full, quick and deep
paths without contacting a LAN. They also cover all-off behavior, existing API defaults,
stale enrichment, pre-cancellation and JSON compatibility. Loopback regression tests
disable multicast to avoid querying the surrounding network.
