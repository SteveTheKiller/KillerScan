# Scan settings

The gear and **Scan settings** label beside **Scan** opens the choices for that
scan tab. All checks start enabled. Choices stay fixed while a normal or deep scan
runs. **Restore defaults** enables every check again.

Saving a profile remembers the choices with its targets. Loading or running that
profile restores them. Older profiles retain the enabled defaults.

| Choice | What it controls |
| --- | --- |
| Read neighbor cache | Reads devices already known to Windows before and after discovery. |
| Resolve MAC via ARP | Explicitly resolves MAC addresses for discovered hosts. |
| ICMP discovery and TTL | Pings for discovery and the device's TTL hint. |
| TCP discovery fallback | Tries common TCP ports when ping does not find the host. |
| Verified reverse DNS | Flushes the local DNS cache, looks up names, and verifies them against the address. |
| mDNS enrichment | Adds information from multicast DNS. |
| SSDP enrichment | Adds information from SSDP. |
| TCP port scanning | Probes common ports, or ports 1 through 1024 plus service ports during Deep Scan. |
| HTTP/HTTPS title and server | Reads web page titles and server headers from discovered ports. |
| TLS certificate details | Inspects certificates on discovered ports. |
| SSH banner | Reads the SSH banner when port 22 is open. |
| SNMP description | Queries the device description over UDP. |
| NetBIOS name | Queries the device name over UDP. |

HTTP, TLS certificate inspection, and SSH require port scanning. Their choices
are unavailable while port scanning is off, but their selections are remembered.
SNMP and NetBIOS operate independently. Turning off certificate inspection leaves
HTTPS available to the HTTP check.

Discovery needs neighbor-cache reads, ICMP, or TCP discovery to find hosts.
mDNS and SSDP enrich discovered devices. Disabling checks can reduce results and
identification quality. These options control KillerScan's application probes;
Windows can still perform ARP or DNS as part of other enabled traffic.

Keep Alive, device diagnostics, terminal commands, and speed tests retain their
own behavior. These settings belong to the scan tab.

## Engine API

```csharp
var options = new ScanOptions { Mdns = false, Ssdp = false, Snmp = false };
var devices = await scanner.ScanSubnetAsync(targets, cancellationToken, options);
var device = await scanner.DeepProbeHostAsync(address, cancellationToken, options);
```

Options are immutable snapshots. Existing API overloads keep all defaults enabled.
Quick mode continues to skip port scanning and fingerprints even when their
individual choices are enabled.
