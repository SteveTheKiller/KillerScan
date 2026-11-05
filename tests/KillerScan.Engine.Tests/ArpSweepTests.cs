using System.Net;
using KillerScan.Engine;

/// <summary>
/// A host that answers ARP but drops ping and has no liveness port open, and that has already
/// aged out of the neighbor cache, is still a device. Off-link addresses answer SendARP with the
/// next hop's MAC, so they must never count as devices this way or borrow that MAC.
/// </summary>
internal static class ArpSweepTests
{
    internal static async Task Run()
    {
        var original = NetworkPlatform.Current;
        int originalLimit = NetworkScanner.ArpSweepLimit;
        try
        {
            await OnLinkArpOnlyHostIsFound();
            await OffLinkAndDisabledArpAddNothing();
            await SweepReportsProgress();
            await OneMacForEveryAddressIsNotADevice();
            await LargeRangesSkipTheSweep();
            await RoutedHostsNeverBorrowTheNextHopMac();
            await RouteLookupsStayOffTheCallersThread();
            await BoundScanTreatsItsOwnNetworkAsOnLink();
        }
        finally { NetworkPlatform.Current = original; NetworkScanner.ArpSweepLimit = originalLimit; }
        Console.WriteLine("PASS: the ARP sweep finds on-link ARP-only hosts, reports progress, and adds or stamps nothing off-link.");
    }

    private static Task<List<NetworkDevice>> Quick(string target, ScanOptions? options = null, NetworkScanner? scanner = null) =>
        (scanner ?? new NetworkScanner(new IsolatedDiscovery())).ScanSubnetAsync(target, CancellationToken.None, options ?? new ScanOptions(), fullScan: false);

    private static async Task OnLinkArpOnlyHostIsFound()
    {
        NetworkPlatform.Current = new ArpPlatform(_ => "", ip => ip == "192.0.2.77" ? "00:04:F2:12:34:56" : "");
        var found = await Quick("192.0.2.74-79");
        Require(found.Count == 1 && found[0].IpAddress == "192.0.2.77", "An on-link host that answers only ARP is discovered, and silent addresses are not.");
    }

    private static async Task OffLinkAndDisabledArpAddNothing()
    {
        NetworkPlatform.Current = new ArpPlatform(_ => "192.0.2.1", _ => "00:11:22:33:44:55");
        Require((await Quick("192.0.2.74-79")).Count == 0, "Off-link addresses answered by the next hop's MAC are not devices.");

        var skipped = new ArpPlatform(_ => "", ip => ip == "192.0.2.77" ? "00:04:F2:12:34:56" : "");
        NetworkPlatform.Current = skipped;
        Require((await Quick("192.0.2.74-79", new ScanOptions { Arp = false })).Count == 0 && skipped.Calls == 0,
            "Turning ARP off sends no ARP requests.");
    }

    private static async Task SweepReportsProgress()
    {
        // Dead addresses make SendARP wait; the scan must visibly move while they do.
        // Staggered waits, 20 to 600 ms, so early answers arrive while later lookups still wait.
        var slow = new ArpPlatform(_ => "", ip => { Thread.Sleep(20 * (int.Parse(ip.Split('.')[3]) - 99)); return ""; });
        NetworkPlatform.Current = slow;
        var scanner = new NetworkScanner(new IsolatedDiscovery());
        int during = 0;
        scanner.ProgressChanged += p =>
        {
            if (slow.Active > 0 && p is > 25 and <= 30) Interlocked.Increment(ref during);
        };
        await Quick("192.0.2.100-129", scanner: scanner);
        Require(slow.Calls == 30, "Every silent on-link address is swept.");
        Require(during > 0, "Progress moves between 25 and 30 while the ARP sweep is waiting on silent addresses.");
    }

    private static async Task OneMacForEveryAddressIsNotADevice()
    {
        // A proxy-ARP router or a TAP adapter answers for every on-link address with one MAC.
        NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "00:09:0F:AA:BB:CC");
        Require((await Quick("192.0.2.74-79")).Count == 0, "Addresses whose only evidence is one shared ARP answer are not devices.");
    }

    private static async Task LargeRangesSkipTheSweep()
    {
        NetworkScanner.ArpSweepLimit = 3;
        var platform = new ArpPlatform(_ => "", ip => ip == "192.0.2.77" ? "00:04:F2:12:34:56" : "");
        NetworkPlatform.Current = platform;
        await Quick("192.0.2.74-79");
        Require(platform.Calls == 0, "More silent on-link addresses than the sweep limit are not swept one by one.");
        NetworkScanner.ArpSweepLimit = 1024;
    }

    private static async Task RoutedHostsNeverBorrowTheNextHopMac()
    {
        // Loopback answers ping, so it is found without ARP; here it is routed through 192.0.2.1,
        // and SendARP would answer with that router's MAC.
        NetworkPlatform.Current = new ArpPlatform(_ => "192.0.2.1", _ => "00:11:22:33:44:55");
        var found = await Quick("127.0.0.1");
        Require(found.Count == 1 && found[0].MacAddress.Length == 0, "A routed host found by ping is not given the next hop's MAC.");
        var none = new ScanOptions().Without("ports,http,tls,ssh,snmp,netbios,dns,mdns,ssdp,icmp");
        var probed = await new NetworkScanner(new IsolatedDiscovery()).DeepProbeHostAsync("127.0.0.1", CancellationToken.None, none);
        Require(probed.MacAddress.Length == 0, "A deep probe of a routed host is not given the next hop's MAC.");
    }

    private static async Task RouteLookupsStayOffTheCallersThread()
    {
        // The app awaits scans on its UI thread. Per-address route lookups must not run there.
        var platform = new ArpPlatform(_ => "", _ => "");
        NetworkPlatform.Current = platform;
        using var context = new SingleThreadContext();
        await context.Run(() => Quick("192.0.2.74-79"));
        Require(platform.RouteThreads.Count > 0, "The sweep looked up routes.");
        Require(!platform.RouteThreads.Contains(context.ThreadId), "Route lookups for the sweep run off the caller's thread.");
    }

    private static async Task BoundScanTreatsItsOwnNetworkAsOnLink()
    {
        // A client VPN pushes the site's own subnet through a next hop, so Windows' best route for
        // every address goes elsewhere. A scan bound to the local adapter still reaches its own
        // network directly (ScanBinding.Reaches) and must sweep and resolve MACs there.
        NetworkPlatform.Current = new ArpPlatform(_ => "127.0.0.254",
            ip => "00:04:F2:00:00:" + int.Parse(ip.Split('.')[3]).ToString("X2"));
        var bound = new NetworkScanner(new IsolatedDiscovery()) { Binding = new ScanBinding("Loopback", IPAddress.Loopback, 8) };
        var pinged = await Quick("127.0.0.5-6", scanner: bound);
        Require(pinged.Count == 2 && pinged.All(d => d.MacAddress.StartsWith("00:04:F2", StringComparison.Ordinal)),
            "A bound scan resolves MACs on its own network even when Windows routes that subnet elsewhere.");
        var swept = await Quick("127.0.0.5-6", new ScanOptions().Without("icmp,tcp-liveness"), bound);
        Require(swept.Count == 2, "A bound scan sweeps its own network with ARP even when Windows routes that subnet elsewhere.");
    }

    private sealed class ArpPlatform(Func<string, string> nextHop, Func<string, string> answer) : INetworkPlatform
    {
        private int _calls, _active;
        internal int Calls => Volatile.Read(ref _calls);
        internal int Active => Volatile.Read(ref _active);
        internal System.Collections.Concurrent.ConcurrentBag<int> RouteThreads { get; } = new();
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(IPAddress address)
        {
            Interlocked.Increment(ref _calls);
            Interlocked.Increment(ref _active);
            try { return answer(address.ToString()); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(IPAddress address)
        {
            RouteThreads.Add(Environment.CurrentManagedThreadId);
            return ("Ethernet", nextHop(address.ToString()));
        }
    }

    /// <summary>Runs continuations on one dedicated thread, the way a UI dispatcher does.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = new();
        private readonly Thread _thread;
        internal int ThreadId { get; private set; }

        internal SingleThreadContext()
        {
            _thread = new Thread(() =>
            {
                ThreadId = Environment.CurrentManagedThreadId;
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable()) callback(state);
            }) { IsBackground = true };
            _thread.Start();
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        internal Task Run(Func<Task> work)
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try { await work(); done.SetResult(true); }
                catch (Exception error) { done.SetException(error); }
            }, null);
            return done.Task;
        }

        public void Dispose() { _queue.CompleteAdding(); _thread.Join(5000); _queue.Dispose(); }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
