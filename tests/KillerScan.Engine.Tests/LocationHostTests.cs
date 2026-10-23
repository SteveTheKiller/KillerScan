using KillerScan.Engine;

internal static class LocationHostTests
{
    private sealed class Probes : NetworkScanner.ScanProbes
    { internal override Task<bool> TcpAlive(System.Net.IPAddress address) => Task.FromResult(true); }
    internal static async Task InstancePreferences()
    {
        var priorType = NetworkScanner.ManualTypeLookup;
        var priorCompletion = NetworkScanner.DeviceCompleted;
        try
        {
            NetworkScanner.ManualTypeLookup = _ => "Wrong global location";
            NetworkScanner.DeviceCompleted = device => device.Hostname = "Wrong global name";
            var a = new NetworkScanner(new Probes()) { ManualTypeResolver = _ => "Printer", DeviceFinalizer = device => device.Hostname = "Alpha" };
            var b = new NetworkScanner(new Probes()) { ManualTypeResolver = _ => "NAS", DeviceFinalizer = device => device.Hostname = "Beta" };
            var options = new ScanOptions().Without(string.Join(",", ScanOptions.Features.Where(name => name != "tcp-liveness")));
            var scans = await Task.WhenAll(a.ScanSubnetAsync("192.0.2.10", CancellationToken.None, options, false),
                b.ScanSubnetAsync("192.0.2.10", CancellationToken.None, options, false));
            if (scans[0].Single().Hostname != "Alpha" || scans[0].Single().DeviceType != "Printer" ||
                scans[1].Single().Hostname != "Beta" || scans[1].Single().DeviceType != "NAS")
                throw new InvalidOperationException("Concurrent scanners must use their own location callbacks, even with the same IP and conflicting static defaults.");
        }
        finally { NetworkScanner.ManualTypeLookup = priorType; NetworkScanner.DeviceCompleted = priorCompletion; }
    }
}
