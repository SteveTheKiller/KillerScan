using System.Text.Json;
using KillerScan.Engine;

// Usage: KillerScan.Engine.Example [targets] [--quick]
// Targets accept anything the KillerScan subnet box does, for example 192.168.1.0/24 or
// 192.168.1.10-50. With no target the attached network is scanned.

bool quick = args.Contains("--quick", StringComparer.OrdinalIgnoreCase);
string? target = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

// Detect also tells the classifier which address is the gateway and which is the DNS server.
var local = LocalNetwork.Detect();
target ??= local?.Subnet;
if (string.IsNullOrEmpty(target))
{
    Console.Error.WriteLine("No target given and no attached IPv4 network was found.");
    return 2;
}

var parsed = ScanTargets.Parse(target);
if (!parsed.Ok)
{
    Console.Error.WriteLine($"Target not accepted: {parsed.Error} {parsed.Detail}".Trim());
    return 2;
}

OuiLookup.Load();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var scanner = new NetworkScanner();
scanner.StatusChanged += status => Console.Error.WriteLine(status);

try
{
    var devices = await scanner.ScanSubnetAsync(target!, cts.Token, fullScan: !quick);
    var rows = devices.Select(d => new
    {
        d.IpAddress,
        d.Hostname,
        d.MacAddress,
        d.Vendor,
        d.DeviceType,
        d.OpenPorts,
    });
    Console.WriteLine(JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Canceled.");
    return 1;
}
