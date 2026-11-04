using System.Net;
using KillerScan.Engine;

/// <summary>
/// A host that answers ARP but drops ping and has no liveness port open, and that has already
/// aged out of the neighbor cache, is still a device. Off-link addresses answer SendARP with the
/// next hop's MAC, so they must never count as devices this way.
/// </summary>
internal static class ArpSweepTests
{
    internal static async Task Run()
    {
        var original = NetworkPlatform.Current;
        try
        {
            var onLink = new ArpOnlyPlatform(nextHop: "", answering: "192.0.2.77");
            NetworkPlatform.Current = onLink;
            var found = await new NetworkScanner(new IsolatedDiscovery())
                .ScanSubnetAsync("192.0.2.74-79", CancellationToken.None, fullScan: false);
            Require(found.Any(d => d.IpAddress == "192.0.2.77"), "An on-link host that answers only ARP is discovered.");
            Require(found.Count == 1, "Addresses that do not answer ARP stay undiscovered.");

            var offLink = new ArpOnlyPlatform(nextHop: "192.0.2.1", answering: null);
            NetworkPlatform.Current = offLink;
            var routed = await new NetworkScanner(new IsolatedDiscovery())
                .ScanSubnetAsync("192.0.2.74-79", CancellationToken.None, fullScan: false);
            Require(routed.Count == 0, "Off-link addresses answered by the next hop's MAC are not devices.");

            var skipped = new ArpOnlyPlatform(nextHop: "", answering: "192.0.2.77");
            NetworkPlatform.Current = skipped;
            var noArp = await new NetworkScanner(new IsolatedDiscovery())
                .ScanSubnetAsync("192.0.2.74-79", CancellationToken.None, new ScanOptions { Arp = false }, fullScan: false);
            Require(noArp.Count == 0 && skipped.Calls == 0, "Turning ARP off sends no ARP requests.");
        }
        finally { NetworkPlatform.Current = original; }
        Console.WriteLine("PASS: on-link ARP-only hosts are discovered; off-link and disabled ARP add none.");
    }

    private sealed class ArpOnlyPlatform(string nextHop, string? answering) : INetworkPlatform
    {
        private int _calls;
        internal int Calls => Volatile.Read(ref _calls);
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(IPAddress address)
        {
            Interlocked.Increment(ref _calls);
            // Off-link: every address resolves to the router. On-link: only the real host answers.
            if (answering == null) return "00:11:22:33:44:55";
            return address.ToString() == answering ? "00:04:F2:12:34:56" : string.Empty;
        }
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(IPAddress address) => ("Ethernet", nextHop);
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
