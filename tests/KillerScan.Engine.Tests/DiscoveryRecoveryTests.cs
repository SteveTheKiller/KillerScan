using System.Net;
using KillerScan.Engine;

internal static class DiscoveryRecoveryTests
{
    internal static async Task Run()
    {
        var original = NetworkPlatform.Current;
        try
        {
            NetworkPlatform.Current = new EmptyPlatform();
            await AnAnsweringHostIsAnnouncedBeforeProbing();
            await AHostCanRecoverWithoutAnotherLateResponder();
            await MulticastRespondersAreDiscovered();
            await MulticastEvidenceSurvivesSharedMac();
            await ALatePingCanConfirmAnArpCandidate();
        }
        finally
        {
            NetworkPlatform.Current = original;
            ScanSource.PingOverride = null;
        }
        Console.WriteLine("PASS: discovery recovery finds hosts missed by the initial passes.");
    }

    private static ScanOptions None() => new ScanOptions().Without(string.Join(",", ScanOptions.Features));

    private static async Task AnAnsweringHostIsAnnouncedBeforeProbing()
    {
        // The window lists a host the moment it answers; it must not wait for discovery to end.
        ScanSource.PingOverride = (address, _) => Task.FromResult((address.ToString().EndsWith(".5") || address.ToString().EndsWith(".9"), 64));
        var scanner = new NetworkScanner(new IsolatedDiscovery());
        var announced = new System.Collections.Concurrent.ConcurrentQueue<string>();
        bool probingStarted = false; int announcedBeforeProbing = 0;
        scanner.HostAnswered += ip => { announced.Enqueue(ip); if (!probingStarted) Interlocked.Increment(ref announcedBeforeProbing); };
        scanner.StatusChanged += status => { if (status.Stage != ScanStage.Discovering) probingStarted = true; };
        var devices = await scanner.ScanSubnetAsync("192.0.2.1-12", CancellationToken.None,
            None().WithFeature("icmp", true), fullScan: false);
        Require(announced.OrderBy(ip => ip).SequenceEqual(new[] { "192.0.2.5", "192.0.2.9" }),
            "Each answering host is announced once: " + string.Join(", ", announced));
        Require(announcedBeforeProbing == 2, $"Both hosts are announced during discovery ({announcedBeforeProbing}).");
        Require(devices.Count == 2, "The announced hosts are the devices returned; got " + devices.Count);
    }

    private static async Task AHostCanRecoverWithoutAnotherLateResponder()
    {
        int attempts = 0;
        ScanSource.PingOverride = (_, _) => Task.FromResult((Interlocked.Increment(ref attempts) >= 3, 64));
        var scanner = new NetworkScanner(new IsolatedDiscovery());
        var devices = await scanner.ScanSubnetAsync("192.0.2.224", CancellationToken.None,
            None().WithFeature("icmp", true), fullScan: false);
        Require(devices.Count == 1 && devices[0].IpAddress == "192.0.2.224",
            "A phone that answers its third echo must be found even when neither earlier pass found a host; got " + devices.Count);
        Console.WriteLine("ok - a third-probe responder is found without another host unlocking retries");
    }

    private sealed class EmptyPlatform : INetworkPlatform
    {
        public bool SharedMac;
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(IPAddress address) => SharedMac ? "00:11:22:33:44:55" : string.Empty;
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(IPAddress address) => ("fixture", "");
    }

    private static async Task MulticastEvidenceSurvivesSharedMac()
    {
        NetworkPlatform.Current = new EmptyPlatform { SharedMac = true };
        var scanner = new NetworkScanner(new ReplyDiscovery());
        var devices = await scanner.ScanSubnetAsync("192.0.2.224,192.0.2.231-232", CancellationToken.None,
            None().WithFeature("arp", true).WithFeature("mdns", true).WithFeature("ssdp", true), fullScan: false);
        Require(devices.Select(device => device.IpAddress).OrderBy(ip => ip).SequenceEqual(new[] { "192.0.2.224", "192.0.2.231" }),
            "Independent multicast replies must survive a shared MAC, while an ARP-only proxy answer is excluded; got " + string.Join(",", devices.Select(device => device.IpAddress)));
        Require(devices.All(device => device.MacAddress.Length == 0),
            "The responding hosts must not inherit the proxy's shared MAC or vendor.");
        NetworkPlatform.Current = new EmptyPlatform();
        Console.WriteLine("ok - independently responding hosts survive proxy-ARP filtering");
    }

    private static async Task ALatePingCanConfirmAnArpCandidate()
    {
        NetworkPlatform.Current = new EmptyPlatform { SharedMac = true };
        var attempts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        ScanSource.PingOverride = (address, _) =>
        {
            int attempt = attempts.AddOrUpdate(address.ToString(), 1, (_, count) => count + 1);
            bool responds = address.ToString() switch
            {
                "192.0.2.224" => attempt >= 3,
                "192.0.2.231" => attempt >= 4,
                _ => false
            };
            return Task.FromResult((responds, 64));
        };
        var scanner = new NetworkScanner(new IsolatedDiscovery());
        var devices = await scanner.ScanSubnetAsync(
            "192.0.2.224,192.0.2.231-232", CancellationToken.None,
            None().WithFeature("arp", true).WithFeature("icmp", true), fullScan: false);
        Require(devices.Select(device => device.IpAddress).OrderBy(ip => ip).SequenceEqual(new[] { "192.0.2.224", "192.0.2.231" })
                && devices.All(device => device.MacAddress.Length == 0),
            "Late echoes must confirm ARP candidates across recovery rounds without promoting silent proxy answers; got " + devices.Count);
        Require(scanner.DiscoverySummary.Contains("ARP confirmed 2, filtered -1"),
            "Discovery diagnostics must account for the two recovered hosts and the excluded proxy answer: " + scanner.DiscoverySummary);
        NetworkPlatform.Current = new EmptyPlatform();
        ScanSource.PingOverride = null;
        Console.WriteLine("ok - recovery confirms a late-ping phone before discarding proxy-ARP candidates");
    }

    private static async Task MulticastRespondersAreDiscovered()
    {
        foreach (bool full in new[] { false, true })
        {
            foreach (string feature in new[] { "mdns", "ssdp" })
            {
                var scanner = new NetworkScanner(new ReplyDiscovery());
                var devices = await scanner.ScanSubnetAsync("192.0.2.224,192.0.2.231", CancellationToken.None,
                    None().WithFeature(feature, true), fullScan: full);
                string expected = feature == "mdns" ? "192.0.2.224" : "192.0.2.231";
                Require(devices.Count == 1 && devices[0].IpAddress == expected,
                    feature + " must discover its responding target without ping or ARP and exclude replies outside the target; got " + devices.Count);
                Require(feature == "mdns" ? devices[0].Hostname == "phone" : devices[0].SsdpServer == "switch",
                    "A newly discovered multicast host keeps its response details.");
            }
        }
        Console.WriteLine("ok - quick and full scans discover selected multicast responders within the target");
    }

    private sealed class ReplyDiscovery : IScanDiscovery
    {
        public Task<Dictionary<string, MulticastDiscovery.MdnsInfo>> CollectMdnsAsync(CancellationToken ct) =>
            Task.FromResult(new Dictionary<string, MulticastDiscovery.MdnsInfo>
            {
                ["192.0.2.224"] = new() { Name = "phone" },
                ["192.0.2.250"] = new() { Name = "outside-target" }
            });
        public Task<Dictionary<string, string>> CollectSsdpAsync(CancellationToken ct) =>
            Task.FromResult(new Dictionary<string, string>
            {
                ["192.0.2.231"] = "switch", ["192.0.2.250"] = "outside-target"
            });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
