# Explicit IPv4 interface selection

The network button beside Scan selects Automatic or an active Ethernet/Wi-Fi IPv4
address. Selection is per scan tab; the terminal scan toolbar has its own selection.
The menu is refreshed when opened. A profile stores the adapter ID and source address,
not a transient interface index. Missing profiles fields retain Automatic behavior.

Automatic keeps existing system routing. An explicit selection resolves the current
adapter index and takes a fixed link snapshot. A missing adapter/address fails instead
of switching to Automatic. Changing the choice clears that tab's old results, avoiding
deep probes of an old network under a new selection. The controls are locked during a
scan or deep rescan. A bound scan accepts targets only within the selected IPv4 subnet.

## Network operations

- TCP and UDP bind to the source IPv4 address and set Windows `IP_UNICAST_IF`; a failed
  bind/option never causes an unbound retry. Multicast sets `IP_MULTICAST_IF` and mDNS
  joins only on that source interface. Received multicast and UDP fingerprints are
  checked against packet-information interface index, subnet and (for fingerprints)
  expected peer. These are distinct Windows socket options; see [Microsoft's reference](https://learn.microsoft.com/en-us/windows/win32/winsock/ipproto-ip-socket-options).
- Neighbor-cache reads use `arp -a -N <source>`. Explicit ARP uses the selected source
  in [`SendARP`](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-sendarp).
- ICMP uses [`IcmpSendEcho2Ex`](https://learn.microsoft.com/en-us/windows/win32/api/icmpapi/nf-icmpapi-icmpsendecho2ex)
  with a source address after confirming a source-aware route to that interface. A
  conflicting route stops the scan; disable ICMP or correct the route to continue.
- DNS sends bounded PTR/A requests directly to the selected adapter's IPv4 DNS server
  over a pinned UDP socket, checks the receiving interface and peer, and forward-verifies
  the returned hostname. A configured DNS server can be outside the target subnet. It does not
  use OS resolver fallback or flush its global cache. Missing IPv4 DNS, truncation,
  unsupported answers or a timeout yield no DNS name. No TCP/DoH fallback is attempted.
- HTTP/HTTPS, TLS and SSH use pinned TCP connections, numeric destinations, bounded
  reads and timeouts. HTTP does not use a proxy or follow redirects in bound mode.
  SNMP and NetBIOS use pinned UDP. Existing Automatic fingerprint behavior is retained.
- The bound classifier uses that binding's gateway/DNS, independently of the static
  defaults used by older hosts.

A per-run watcher checks link identity every 250 ms; probe checks are cached for at
most 100 ms. Loss or changes to adapter/index/address/subnet/gateway/DNS or connected
Wi-Fi identity cancel the run and close its sockets. Completed rows already delivered
to the GUI remain available. A selected Wi-Fi connection must expose its identity via
Windows WLAN APIs; if permission/identity is unavailable, explicit selection is
unavailable rather than guessed. The application does not change Windows permissions.

This is Windows IPv4 application routing control, not a packet firewall. OS/filter
drivers and packets already in flight cannot be retrospectively controlled. A physical
Ethernet/Wi-Fi/VPN and same-address reconnect pilot remains necessary before claiming
production field certification. Virtual adapters with an Ethernet type may appear with
their Windows hardware description; select the intended adapter explicitly.

## CLI and terminal

```powershell
KillerScan.exe /interfaces /json
KillerScan.exe /scan 192.0.2.0/24 /interface "<adapter-id>" /source-ip 192.0.2.20 /json
KillerScan.exe /probe 192.0.2.10 /interface "<adapter-id>" /source-ip 192.0.2.20 /skip mdns,ssdp
scan 192.0.2.0/24 -InterfaceId '<adapter-id>' -SourceIp '192.0.2.20'
```

Both ID and source address are required. The `/interfaces` list includes a `CanBind`
field. Omitting both preserves Automatic. The in-app terminal's scan button forwards
its picker; manually typed commands use their own explicit parameters. Other commands
such as ping, diagnostics, Keep Alive and speed test retain their own behavior.
History and device preferences keep their existing storage; interface selection does
not create separate site namespaces.

The regression suite uses loopback fixtures and controlled link changes: real socket
source/index options, packet receive filtering, native source-bound ICMP, DNS PTR/A,
HTTP fingerprints/no redirects, independent classification, cancellation and old/new
profiles. It does not scan the surrounding LAN or reconfigure adapters/VPNs.
