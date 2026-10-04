# KillerScan.Engine

KillerScan.Engine is the scanner behind KillerScan, packaged as a library with no WPF and no UI text. The desktop app, its command line and any other program use the same discovery, fingerprinting and classification.

It targets `net48` and `net10.0`. KillerScan bundles the engine DLL into its single exe.

## What it covers

| Area | Types |
| --- | --- |
| Targets | `ScanTargets.Parse` accepts CIDR blocks, single hosts and ranges, separated by commas |
| Scanning | `NetworkScanner.ScanSubnetAsync`, `NetworkScanner.DeepProbeHostAsync` |
| Results | `NetworkDevice` |
| Classification | `NetworkScanner.ClassifyDevice` |
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

## Status and localization

`StatusChanged` reports a `ScanStatus`, which carries a `ScanStage` plus a count or a target label. The engine never produces translated text. `ScanStatus.ToString()` gives plain English for logs and tools.

Stored values such as `NetworkDevice.DeviceType` and the randomized-MAC vendor marker stay in English, because they are written to CSV and matched by filters. A host can translate the device type for display by setting `NetworkDevice.DeviceTypeDisplayResolver`.

## Host hooks

These are static and set once at startup.

- `NetworkScanner.ManualTypeLookup` returns a saved device type for a MAC address. That type wins over classification.
- `NetworkScanner.DeviceCompleted` runs on each finished device before it is reported, for example to apply a saved name.
- `NetworkScanner.GatewayIp` and `NetworkScanner.DnsIp` mark the router and the DNS server. `LocalNetwork.Detect` sets both.

## Platform

ARP and route lookups, plus the DNS cache flush, go through `INetworkPlatform`. `NetworkPlatform.Current` picks `WindowsNetworkPlatform` on Windows. On other systems it uses `UnsupportedNetworkPlatform`, so discovery relies on ping and TCP liveness and devices have no MAC address. Tests and other hosts can assign their own implementation.

## Example and tests

- `samples/KillerScan.Engine.Example` scans a target and prints JSON: `dotnet run --project samples/KillerScan.Engine.Example -- 192.168.1.0/24 --quick`
- `tests/KillerScan.Engine.Tests` runs the engine checks against both targets.
