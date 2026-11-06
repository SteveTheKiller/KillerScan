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
        var originalQuickFailure = NetworkScanner.ArpQuickFailure;
        string originalGateway = NetworkScanner.GatewayIp;
        // The fakes answer at once, where a real dead address holds SendARP for seconds, so the
        // quick-failure retry is off except in the test that is about it.
        NetworkScanner.ArpQuickFailure = TimeSpan.Zero;
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
            await CacheReadAfterTheSweepFindsLateAnswers();
            await QuickArpFailuresAreAskedAgain();
            await SilentRoutedAddressesGetASecondLivenessPass();
            await HostsLostInTheFirstBurstAreFoundByThePacedPass();
            await TapMacIsBlankedOnHostsThatAnswerPing();
            await GatewayMacIsBlankedOnOtherHosts();
            await BroadcastIsNotADevice();
        }
        finally
        {
            NetworkPlatform.Current = original;
            NetworkScanner.ArpSweepLimit = originalLimit;
            NetworkScanner.ArpQuickFailure = originalQuickFailure;
            NetworkScanner.GatewayIp = originalGateway;
            ScanSource.PingOverride = null;
        }
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
        // A proxy-ARP router or a TAP adapter answers for every on-link address with one MAC. The
        // gateway is outside the range and the cache is empty, so nothing names that MAC a router's.
        NetworkScanner.GatewayIp = "192.0.2.1";
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

    private static async Task CacheReadAfterTheSweepFindsLateAnswers()
    {
        // SendARP can give up on a host whose reply Windows still records a moment later. The
        // neighbor cache read after the sweep is the last look, and it sends no traffic.
        var platform = new ArpPlatform(_ => "", _ => "",
            reads => reads >= 3 ? new Dictionary<string, string> { ["192.0.2.77"] = "00:04:F2:12:34:56" } : new Dictionary<string, string>());
        NetworkPlatform.Current = platform;
        var found = await Quick("192.0.2.74-79", Silent);
        Require(found.Count == 1 && found[0].IpAddress == "192.0.2.77" && found[0].MacAddress == "00:04:F2:12:34:56",
            "A host the neighbor cache holds only after the ARP sweep is still found.");
    }

    private static async Task QuickArpFailuresAreAskedAgain()
    {
        // A dead address holds SendARP for seconds. One that fails at once never got a fair
        // chance (a busy phone, a request the stack dropped), so it is asked one more time.
        NetworkScanner.ArpQuickFailure = TimeSpan.FromMilliseconds(150);
        int seventySeven = 0;
        var platform = new ArpPlatform(_ => "", ip =>
        {
            if (ip == "192.0.2.77") return Interlocked.Increment(ref seventySeven) >= 2 ? "00:04:F2:12:34:56" : "";
            Thread.Sleep(250);
            return "";
        });
        NetworkPlatform.Current = platform;
        try
        {
            var found = await Quick("192.0.2.74-79", Silent);
            Require(found.Count == 1 && found[0].IpAddress == "192.0.2.77", "An address whose ARP request failed at once is asked again and found.");
            Require(platform.CallsFor("192.0.2.77") == 2 && platform.CallsFor("192.0.2.74") == 1,
                "Only the quick failure is asked again; an address that waited out the full ARP timeout is not.");
        }
        finally { NetworkScanner.ArpQuickFailure = TimeSpan.Zero; }
    }

    private static async Task SilentRoutedAddressesGetASecondLivenessPass()
    {
        // Across a router the ARP sweep cannot help, so a routed address that missed its first
        // ping gets a second one with a longer wait. On-link addresses already get the sweep.
        var pings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        int longest = 0;
        ScanSource.PingOverride = (address, timeout) =>
        {
            int n = pings.AddOrUpdate(address.ToString(), 1, (_, count) => count + 1);
            if (n == 2) InterlockedMax(ref longest, timeout);
            return Task.FromResult((address.ToString() == "192.0.2.77" && n == 2, 64));
        };
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "192.0.2.1", _ => "");
            var found = await Quick("192.0.2.74-79", new ScanOptions().Without("tcp-liveness"));
            Require(found.Count == 1 && found[0].IpAddress == "192.0.2.77", "A routed host that answers only its second ping is found.");
            Require(longest >= 1000, "The second ping waits out at least one ARP retransmit.");

            pings.Clear();
            NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "");
            await Quick("192.0.2.74-79", new ScanOptions().Without("tcp-liveness"));
            Require(pings.Values.All(count => count == 2), "On-link addresses get the paced second ping before the ARP sweep.");
        }
        finally { ScanSource.PingOverride = null; }
    }

    private static async Task HostsLostInTheFirstBurstAreFoundByThePacedPass()
    {
        // A customer's /24 with about 160 live hosts lost the same 17 devices on every scan, all
        // of which answered a lone ping in under a millisecond. Client Windows caps the neighbor
        // cache at 256 entries per interface, and switches rate-limit broadcast storms, so a wave
        // of 200 pings at once loses whichever answers arrive while the stack is full. This
        // network drops every request issued while more than 64 are in flight. The first wave
        // loses most hosts; the paced pass, which never exceeds that, must find every one.
        int inFlight = 0, peakPaced = 0, firstWaveDone = 0;
        var pings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        ScanSource.PingOverride = async (address, timeout) =>
        {
            int n = pings.AddOrUpdate(address.ToString(), 1, (_, count) => count + 1);
            int now = Interlocked.Increment(ref inFlight);
            try
            {
                if (n >= 2) InterlockedMax(ref peakPaced, now);
                await Task.Delay(30);
                bool live = int.Parse(address.ToString().Split('.')[3]) % 3 == 0;
                return (live && now <= 64, 64);
            }
            finally
            {
                Interlocked.Decrement(ref inFlight);
                if (n == 1) Interlocked.Increment(ref firstWaveDone);
            }
        };
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "");
            var found = await Quick("192.0.2.1-254", new ScanOptions().Without("tcp-liveness"));
            var expected = Enumerable.Range(1, 254).Where(n => n % 3 == 0).Select(n => "192.0.2." + n).ToList();
            var missing = expected.Where(ip => found.All(d => d.IpAddress != ip)).ToList();
            Require(missing.Count == 0, $"Every host the first burst lost is found by the paced pass; missing {missing.Count}: {string.Join(", ", missing.Take(8))}");
            Require(peakPaced > 0 && peakPaced <= NetworkScanner.PacedPassWidth,
                $"The paced pass never has more than {NetworkScanner.PacedPassWidth} pings in flight (saw {peakPaced}).");
        }
        finally { ScanSource.PingOverride = null; }
    }

    private static async Task TapMacIsBlankedOnHostsThatAnswerPing()
    {
        // An on-link VPN or TAP adapter answers ARP for every host behind it with its own MAC
        // (Fortinet here). Hosts that answer ping are real, but that MAC is not theirs: it would
        // stamp each with the adapter's vendor and type, so they keep their address and lose it.
        NetworkScanner.GatewayIp = "192.0.2.1";
        ScanSource.PingOverride = (address, _) => Task.FromResult((address.ToString() is "192.0.2.74" or "192.0.2.75" or "192.0.2.76", 64));
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", ip => ip is "192.0.2.74" or "192.0.2.75" or "192.0.2.76" ? "00:09:0F:AA:BB:CC" : "");
            var found = await Quick("192.0.2.74-79", new ScanOptions().Without("tcp-liveness"));
            Require(found.Count == 3 && found.All(d => d.MacAddress.Length == 0),
                "Hosts that answer ping behind one shared MAC stay devices without that MAC.");
        }
        finally { ScanSource.PingOverride = null; }
    }

    private static async Task GatewayMacIsBlankedOnOtherHosts()
    {
        // Hosts that answer ping but resolve to the gateway's own MAC borrowed it (proxy ARP):
        // they stay as devices, the gateway keeps its MAC, and the rest lose the borrowed copy.
        NetworkScanner.GatewayIp = "192.0.2.74";
        ScanSource.PingOverride = (address, _) => Task.FromResult((address.ToString() is "192.0.2.74" or "192.0.2.75" or "192.0.2.76", 64));
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", ip => ip is "192.0.2.74" or "192.0.2.75" or "192.0.2.76" ? "00:11:22:33:44:55" : "");
            var found = await Quick("192.0.2.74-79", new ScanOptions().Without("tcp-liveness"));
            Require(found.Count == 3, "Hosts that answer ping stay devices whatever MAC they resolved to.");
            Require(found.Single(d => d.IpAddress == "192.0.2.74").MacAddress == "00:11:22:33:44:55"
                && found.Where(d => d.IpAddress != "192.0.2.74").All(d => d.MacAddress.Length == 0),
                "Only the gateway keeps the gateway's MAC.");
        }
        finally { ScanSource.PingOverride = null; }
    }

    private static async Task BroadcastIsNotADevice()
    {
        // Measured on Windows 11: SendARP for the subnet broadcast succeeds and answers with this
        // PC's own adapter MAC. A typed range ending in .255 then listed a phantom device wearing
        // the scanner's own vendor. This PC's own address keeps its MAC; no other address may.
        NetworkScanner.LocalIdentityOverride = () => (new HashSet<string> { "00:04:F2:AA:AA:AA" }, new HashSet<string> { "192.0.2.10" });
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", ip => ip switch
            {
                "192.0.2.10" or "192.0.2.255" => "00:04:F2:AA:AA:AA",
                "192.0.2.77" => "00:04:F2:12:34:56",
                _ => "",
            });
            var found = await Quick("192.0.2.1-255", Silent);
            Require(found.Select(d => d.IpAddress).OrderBy(ip => ip).SequenceEqual(new[] { "192.0.2.10", "192.0.2.77" }),
                "The broadcast address answered with this PC's own MAC is not a device: " + string.Join(", ", found.Select(d => d.IpAddress)));
            Require(found.Single(d => d.IpAddress == "192.0.2.10").MacAddress == "00:04:F2:AA:AA:AA", "This PC keeps its own MAC.");
        }
        finally { NetworkScanner.LocalIdentityOverride = null; }
    }

    // No ping and no TCP: the ARP sweep and the neighbor cache are the only evidence.
    private static readonly ScanOptions Silent = new ScanOptions().Without("icmp,tcp-liveness");

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    private sealed class ArpPlatform(Func<string, string> nextHop, Func<string, string> answer,
        Func<int, IReadOnlyDictionary<string, string>>? cache = null) : INetworkPlatform
    {
        private int _calls, _active, _peak, _reads;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _perAddress = new();
        internal int Calls => Volatile.Read(ref _calls);
        internal int Active => Volatile.Read(ref _active);
        internal int PeakActive => Volatile.Read(ref _peak);
        internal int CallsFor(string ip) => _perAddress.TryGetValue(ip, out int n) ? n : 0;
        internal System.Collections.Concurrent.ConcurrentBag<int> RouteThreads { get; } = new();
        public IReadOnlyDictionary<string, string> ReadNeighborCache() =>
            cache?.Invoke(Interlocked.Increment(ref _reads)) ?? new Dictionary<string, string>();
        public string ResolveMac(IPAddress address)
        {
            Interlocked.Increment(ref _calls);
            _perAddress.AddOrUpdate(address.ToString(), 1, (_, n) => n + 1);
            InterlockedMax(ref _peak, Interlocked.Increment(ref _active));
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
