using System.Net;
using KillerScan.Engine;

internal static class Program
{
    private static int _passed;

    private static async Task<int> Main()
    {
        try
        {
            await Run("Targets parse ranges, de-duplicate overlaps and refuse bad input", Targets);
            await Run("CIDR expansion excludes the network and broadcast addresses", Subnet);
            await Run("ARP output parsing keeps IPv4 entries with full MAC addresses", ArpParsing);
            await Run("Classifier honors manual types, the gateway, DNS and hostname rules", Classifier);
            await Run("Bundled vendor database loads from the engine assembly", Vendors);
            await Run("Status text is English, numeric and free of double hyphens", StatusText);
            await Run("Quick scan reports stages, devices and host hooks through a fake platform", QuickScan);
            await Run("A canceled token stops the scan", Canceled);
            Console.WriteLine("PASS: " + _passed + " engine regression checks.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static async Task Run(string name, Func<Task> test)
    {
        await test();
        _passed++;
        Console.WriteLine("ok - " + name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Task Targets()
    {
        var range = ScanTargets.Parse("192.168.1.10-12, 192.168.1.11");
        Require(range.Ok, "Range and single host should parse");
        Require(range.Addresses.Count == 3, "Overlapping host should collapse, got " + range.Addresses.Count);
        Require(range.Summary == "192.168.1.10-12 +1", "Summary was " + range.Summary);

        var invalid = ScanTargets.Parse("not-an-address");
        Require(invalid.Error == TargetError.Invalid, "Invalid input should be refused");

        var huge = ScanTargets.Parse("10.0.0.0/8");
        Require(huge.Error == TargetError.TooLarge, "A /8 should exceed the address ceiling");

        Require(ScanTargets.Parse("  ").Error == TargetError.Empty, "Blank input should be empty");
        return Task.CompletedTask;
    }

    private static Task Subnet()
    {
        var list = NetworkScanner.GetAddressesInSubnet("10.0.0.0/30");
        Require(list.Count == 2, "A /30 has two hosts, got " + list.Count);
        Require(list[0].ToString() == "10.0.0.1" && list[1].ToString() == "10.0.0.2", "Unexpected hosts");
        return Task.CompletedTask;
    }

    private static Task ArpParsing()
    {
        const string output =
            "Interface: 192.168.1.20 --- 0x7\r\n" +
            "  Internet Address      Physical Address      Type\r\n" +
            "  192.168.1.1           00-00-0c-12-34-56     dynamic\r\n" +
            "  192.168.1.255         ff-ff-ff-ff-ff-ff     static\r\n" +
            "  224.0.0.22            01-00-5e            static\r\n";
        var cache = ArpOutput.Parse(output);
        Require(cache.TryGetValue("192.168.1.1", out var mac) && mac == "00:00:0C:12:34:56", "Gateway MAC should normalize");
        Require(cache.ContainsKey("192.168.1.255"), "Broadcast row is kept as the cache reports it");
        Require(!cache.ContainsKey("224.0.0.22"), "Short MAC rows are rejected");
        return Task.CompletedTask;
    }

    private static Task Classifier()
    {
        string oldGateway = NetworkScanner.GatewayIp, oldDns = NetworkScanner.DnsIp;
        var oldLookup = NetworkScanner.ManualTypeLookup;
        try
        {
            NetworkScanner.GatewayIp = "192.168.1.1";
            NetworkScanner.DnsIp = "192.168.1.1";
            Require(NetworkScanner.ClassifyDevice(new NetworkDevice { IpAddress = "192.168.1.1" }) == "Router/DNS",
                "Gateway that serves DNS should be Router/DNS");

            NetworkScanner.DnsIp = "192.168.1.5";
            Require(NetworkScanner.ClassifyDevice(new NetworkDevice { IpAddress = "192.168.1.1" }) == "Router",
                "Gateway that forwards DNS should be Router");

            Require(NetworkScanner.ClassifyDevice(new NetworkDevice { IpAddress = "192.168.1.40", Hostname = "office-synology" }) == "NAS",
                "Hostname keyword should classify a NAS");

            NetworkScanner.ManualTypeLookup = mac => mac == "AA:BB:CC:00:00:01" ? "Printer" : null;
            Require(NetworkScanner.ClassifyDevice(new NetworkDevice
                    { IpAddress = "192.168.1.1", MacAddress = "AA:BB:CC:00:00:01" }) == "Printer",
                "Manual type should win over the gateway rule");
        }
        finally
        {
            NetworkScanner.GatewayIp = oldGateway;
            NetworkScanner.DnsIp = oldDns;
            NetworkScanner.ManualTypeLookup = oldLookup;
        }
        return Task.CompletedTask;
    }

    private static Task Vendors()
    {
        OuiLookup.Load();
        Require(OuiLookup.Count > 10000, "Vendor database looks empty: " + OuiLookup.Count);
        Require(OuiLookup.GetVendor("00:00:0C:12:34:56").IndexOf("Cisco", StringComparison.OrdinalIgnoreCase) >= 0,
            "00:00:0C should be Cisco");
        return Task.CompletedTask;
    }

    private static Task StatusText()
    {
        var all = new[]
        {
            new ScanStatus(ScanStage.Discovering, 256, "192.168.1.0/24"),
            new ScanStatus(ScanStage.ResolvingMacs, 12),
            new ScanStatus(ScanStage.Probing, 12),
            new ScanStatus(ScanStage.ResolvingHosts, 12),
            new ScanStatus(ScanStage.Complete, 12),
        };
        foreach (var status in all)
        {
            string text = status.ToString();
            Require(text.Length > 0 && !text.Contains("--") && text.IndexOf((char)0x2013) < 0 && text.IndexOf((char)0x2014) < 0,
                "Bad status text: " + text);
        }
        Require(all[0].ToString().Contains("192.168.1.0/24"), "Discovering names the target");
        Require(all[4].ToString() == "Scan complete - 12 devices found", "Complete text was " + all[4]);
        return Task.CompletedTask;
    }

    private sealed class FakePlatform : INetworkPlatform
    {
        public int Flushes;
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(IPAddress address) => "00:00:0C:12:34:56";
        public void FlushDnsCache() => Flushes++;
        public (string Interface, string NextHop)? BestRoute(IPAddress address) => ("Fake", "");
    }

    private static async Task QuickScan()
    {
        var oldPlatform = NetworkPlatform.Current;
        var oldCompleted = NetworkScanner.DeviceCompleted;
        var fake = new FakePlatform();
        try
        {
            NetworkPlatform.Current = fake;
            int completedHook = 0;
            NetworkScanner.DeviceCompleted = _ => completedHook++;

            var scanner = new NetworkScanner();
            var stages = new List<ScanStage>();
            var found = new List<NetworkDevice>();
            int lastProgress = -1;
            scanner.StatusChanged += s => { lock (stages) stages.Add(s.Stage); };
            scanner.DeviceFound += d => { lock (found) found.Add(d); };
            scanner.ProgressChanged += p => lastProgress = p;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var devices = await scanner.ScanSubnetAsync("127.0.0.1", cts.Token, fullScan: false);

            Require(fake.Flushes == 1, "Scan should flush the DNS cache once");
            Require(devices.Count == 1 && found.Count == 1, "Loopback should be found once, got " + devices.Count);
            Require(devices[0].MacAddress == "00:00:0C:12:34:56", "MAC should come from the platform");
            Require(devices[0].Vendor.IndexOf("Cisco", StringComparison.OrdinalIgnoreCase) >= 0, "Vendor should resolve from the MAC");
            Require(completedHook == 1, "DeviceCompleted should run once per device");
            Require(stages.SequenceEqual(new[] { ScanStage.Discovering, ScanStage.ResolvingMacs, ScanStage.ResolvingHosts, ScanStage.Complete }),
                "Stages were " + string.Join(", ", stages));
            Require(lastProgress == 100, "Progress should finish at 100");
            Require(ConnectionChecks.Route(IPAddress.Loopback)?.Interface == "Fake", "Route should come from the platform");
        }
        finally
        {
            NetworkPlatform.Current = oldPlatform;
            NetworkScanner.DeviceCompleted = oldCompleted;
        }
    }

    private static async Task Canceled()
    {
        var oldPlatform = NetworkPlatform.Current;
        try
        {
            NetworkPlatform.Current = new FakePlatform();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            try
            {
                await new NetworkScanner().ScanSubnetAsync("127.0.0.1", cts.Token, fullScan: false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            throw new InvalidOperationException("A canceled scan should throw OperationCanceledException");
        }
        finally
        {
            NetworkPlatform.Current = oldPlatform;
        }
    }
}
