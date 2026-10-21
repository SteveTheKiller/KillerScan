# KillerScan.Engine

KillerScan.Engine is the scanner behind KillerScan, packaged as a library with no WPF and no UI text. The desktop app, its command line and any other program use the same discovery, fingerprinting and classification.

It targets `net48` and `net10.0`. KillerScan bundles the engine DLL into its single exe.

## What it covers

| Area | Types |
| --- | --- |
| Targets | `ScanTargets.Parse` accepts CIDR blocks, single hosts and ranges, separated by commas |
| Scanning | `NetworkScanner.ScanSubnetAsync`, `NetworkScanner.DeepProbeHostAsync` |
| Results | `NetworkDevice` |
| Classification | `NetworkScanner.ClassifyDevice`, `NetworkScanner.AssessDevice` |
| Vendors | `OuiLookup`, `VendorDbUpdater` |
| Local network | `LocalNetwork.Detect`, `ConnectionChecks` |
| Speed test | `KillerScan.Engine.SpeedTest.SpeedTestEngine` |
| OS calls | `INetworkPlatform`, `NetworkPlatform.Current` |

## Scanning

```csharp
using KillerScan.Engine;

LocalNetwork.Detect();          // finds the subnet and tells the classifier the gateway and DNS server
OuiLookup.Load();

var scanner = new NetworkScanner();
scanner.StatusChanged += status => Console.Error.WriteLine(status);
scanner.ProgressChanged += percent => { };
scanner.DeviceFound += device => { };

var devices = await scanner.ScanSubnetAsync("192.168.1.0/24", cancellationToken);
```

Events are raised on worker threads, so a UI host marshals them to its own thread.

`fullScan: false` runs a quick scan that resolves names and vendors without probing ports. `DeepProbeHostAsync` probes every port from 1 to 1024 plus the service ports on one host.

Canceling the token ends the scan with an `OperationCanceledException`.

Bad input is refused before any network or DNS cache work. `ScanSubnetAsync` throws `ArgumentException` for a blank, malformed or oversized target (check with `ScanTargets.Parse` first to show your own message). `DeepProbeHostAsync` throws `ArgumentException` for anything but an IPv4 address and `ArgumentOutOfRangeException` for a port concurrency below 1.

Deep probes connect to unknown open ports without sending data. Set `ScanOptions.ProbeUnknownPorts` to also try HTTP and TLS on them. Raw print ports (9100 to 9103 and 515) never receive fingerprint payloads.

`NetworkScanner.ManualTypeLookup` and `NetworkScanner.DeviceCompleted` apply to every scanner. A host that keeps separate saved data per site can instead set `ManualTypes` and `Completed` on each scanner instance; when set, they replace the static hooks for that scanner.

To scan from a particular adapter, set `NetworkScanner.Binding` to a `ScanBinding` (see `ScanBinding.Available()` and `ScanBinding.Find(nameOrAddress)`). Pings, TCP and UDP probes, HTTP and TLS fingerprints, ARP and mDNS/SSDP discovery are then sent from that adapter's address. A bound scan reaches the adapter's own network, plus routed targets whose best Windows route already goes through that adapter (hosts behind a VPN adapter, for example). Any other target is refused with `ArgumentException` before traffic is sent, naming the adapter Windows would use instead; `ScanBinding.Reaches(address)` makes the same check. Name lookups still go through the system resolver to its DNS servers. Bound pings use the Windows ICMP API.

Pass an immutable `ScanOptions` as the third argument to `ScanSubnetAsync` or
`DeepProbeHostAsync` to select discovery and fingerprint checks. Existing overloads
retain enabled defaults. See [scan settings](../docs/scan-options.md) for dependencies.

## Status and localization

`StatusChanged` reports a `ScanStatus`, which carries a `ScanStage` plus a count or a target label. The engine never produces translated text. `ScanStatus.ToString()` gives plain English for logs and tools.

Stored values such as `NetworkDevice.DeviceType` and the randomized-MAC vendor marker stay in English, because they are written to CSV and matched by filters. A host can translate the device type for display by setting `NetworkDevice.DeviceTypeDisplayResolver`.

Every scanned device also carries `NetworkDevice.Recognition`: the signals behind its type (each with a stable source such as `ports`, `mdns` or `snmp`, the value seen and its weight), close alternatives, whether an unrelated type scored close, and a confidence of `Low`, `Medium`, `High` or `Manual`. Confidence is a grade, not a probability, and protocol values are self-reported, so even High does not authenticate a device. `NetworkScanner.AssessDevice(device, manualTypes)` produces the same result from the fields already on a device without sending anything. It is null on devices that were never assessed. See [docs/recognition.md](../docs/recognition.md) for the grading rules.

`NetworkDevice.Access` lists the web endpoints that answered during the scan, each with its status, whether it asked for a login (`Requested` for a 401, `LoginForm` for a page with a password field, otherwise `Unknown`) and when it was seen. None of these means a login would work, and no credentials are ever sent. `DeviceAccess.ForDevice(device)` adds the device's other open ports, `DeviceAccess.Address` builds an address from the numeric IP and the endpoint only, and `DeviceAccess.BestWeb` picks the endpoint to open by default. A normal scan stops at the first useful web page; a deep probe checks every web port. See [docs/access.md](../docs/access.md).

## Host hooks

These are static and set once at startup.

- `NetworkScanner.ManualTypeLookup` returns a saved device type for a MAC address. That type wins over classification.
- `NetworkScanner.DeviceCompleted` runs on each finished device before it is reported, for example to apply a saved name.
- `NetworkScanner.GatewayIp` and `NetworkScanner.DnsIp` mark the router and the DNS server. `LocalNetwork.Detect` sets both.

## Platform

ARP and route lookups, plus the DNS cache flush, go through `INetworkPlatform`. `NetworkPlatform.Current` picks `WindowsNetworkPlatform` on Windows. On other systems it uses `UnsupportedNetworkPlatform`, so discovery relies on ping and TCP liveness and devices have no MAC address. Tests and other hosts can assign their own implementation.

## Speed-test byte counts

`SpeedTestPhaseResult.BytesTransferred` counts payload read during download and payload written to the HTTP transport during upload. The upload rate includes in-flight requests and buffered payload at the measurement cutoff. At least one valid exact-size upload acknowledgment is required during measurement. Sent, receiver-observed and acknowledged byte totals can differ.

Warmup bytes are separate from measured bytes. Neither direction's payload count includes HTTP headers or TLS framing. The result's `Elapsed` is the measured interval used to calculate `Mbps`.

## Example and tests

- `samples/KillerScan.Engine.Example` scans a target and prints JSON: `dotnet run --project samples/KillerScan.Engine.Example -- 192.168.1.0/24 --quick`
- `tests/KillerScan.Engine.Tests` runs the engine checks against both targets.
