# KillerScan

Fast network scanner built for field techs. ARP + ping discovery, port probing, active fingerprinting (HTTP title, SSH banner, TLS cert, NetBIOS, SNMP), vendor lookup via OUI, and weighted-score device classification. Single portable EXE, ~865 KB zipped, no runtime install required.

Part of [killertools.net](https://killertools.net).

## Features

- ARP cache + parallel ping sweep for fast discovery (catches devices that ignore ICMP)
- TCP port scan across 24 common service ports with 200ms timeout
- Active fingerprinting: HTTP title/Server header, SSH banner, TLS cert subject, NetBIOS name (UDP 137), SNMPv1 sysDescr (UDP 161), ICMP TTL
- MAC OUI vendor identification against the IEEE registry
- Weighted-score classifier identifies hypervisors, Windows boxes, Linux servers, printers, NAS, network gear, cameras, IoT, mobile, Home Assistant, and more
- Right-click to copy IP/MAC/hostname, launch RDP/SSH/browser, or override device type
- CSV and HTML export

## Requirements

- Windows 10 or 11 (x64)
- No runtime install. Everything needed is inside the EXE (targets .NET Framework 4.8, which ships with every supported Windows release).
- Run as admin for best ARP results on some networks

## Download

- Prebuilt binary: <https://dl.killertools.net/KillerScan.zip>
- Source (GPL3 corresponding source for this release): <https://dl.killertools.net/KillerScan-1.2.0-src.zip>

## Build from source

```powershell
git clone https://github.com/SteveTheKiller/KillerScan.git
cd KillerScan
dotnet publish -c Release
```

Output lands in `bin/Release/net48/publish/`. The publish step produces a single Costura-bundled `KillerScan.exe` plus a versioned `KillerScan-<version>-src.zip` for GPL3 source distribution.

Requires the .NET 8 SDK or later to build (even though the output targets .NET Framework 4.8).

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

## How classification works

The classifier accumulates points from every signal (open ports, OUI vendor, hostname keywords, HTTP title, SSH banner, TLS subject, SNMP description, NetBIOS name, TTL) and picks the highest-scoring type above a threshold. This replaces brittle first-match port rules and avoids false positives like "my coworker's laptop is a hypervisor because port 2179 is open."

See `Services/NetworkScanner.cs` → `ClassifyDevice` for the scoring table.

## License

GPLv3. See [LICENSE](LICENSE). If you fork, modify, or redistribute KillerScan, your version must also be released under GPLv3 with source available. No exceptions for commercial rebrands.
