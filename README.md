# KillerScan

Fast network scanner built for field techs. ARP + ping discovery, port probing, active fingerprinting (HTTP title, SSH banner, TLS cert, NetBIOS, SNMP), vendor lookup via OUI, and weighted-score device classification. Single portable exe, ~500 KB zipped.

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
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- Run as admin for best ARP results on some networks

## Download

Prebuilt binaries: <https://dl.killertools.net/KillerScan.zip>

## Build from source

```powershell
git clone https://github.com/YOUR_USERNAME/KillerScan.git
cd KillerScan
dotnet publish -c Release -r win-x64 --self-contained false
```

Output lands in `bin/Release/net8.0-windows/win-x64/publish/`.

## How classification works

The classifier accumulates points from every signal (open ports, OUI vendor, hostname keywords, HTTP title, SSH banner, TLS subject, SNMP description, NetBIOS name, TTL) and picks the highest-scoring type above a threshold. This replaces brittle first-match port rules and avoids false positives like "my coworker's laptop is a hypervisor because port 2179 is open."

See `Services/NetworkScanner.cs` → `ClassifyDevice` for the scoring table.

## License

GPLv3. See [LICENSE](LICENSE). If you fork, modify, or redistribute KillerScan, your version must also be released under GPLv3 with source available. No exceptions for commercial rebrands.
