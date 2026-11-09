using System.Diagnostics;
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
            await NoPassEverBurstsPastThePacedWidth();
            await RoutedAddressesKeepTheirSecondPassUnderTheBudget();
            await TheSweepLaunchesInPacedBatches();
            await ThePacedPassReportsProgress();
            await CancelDuringThePacedPassReturnsPromptly();
            await ABusyNetworkIsProbedUntilAPassAddsNothing();
            await TheExtraRoundsReportProgress();
            await TheExtraRoundsStopAtTheirTimeBudget();
            await ALossyLinkKeepsRecoveringPastTheNormalBudget();
            await ALossyLinkAsksArpInEveryRound();
            await TheDiscoveryDetailReportsTheCacheAndTimings();
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
        // ping gets a second one with a longer wait. On-link addresses get that paced second
        // ping too, and then the sweep.
        // Counted per pass by the wait each pass uses: the extra rounds that follow a pass that
        // found a host ping again with their own longer wait.
        var pings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        int longest = 0;
        ScanSource.PingOverride = (address, timeout) =>
        {
            int n = pings.AddOrUpdate(address.ToString(), 1, (_, count) => count + 1);
            if (timeout == NetworkScanner.SecondPingTimeoutMs) InterlockedMax(ref longest, timeout);
            return Task.FromResult((address.ToString() == "192.0.2.77" && timeout == NetworkScanner.SecondPingTimeoutMs, 64));
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
            Require(pings.Values.All(count => count >= 2), "On-link addresses get the paced second ping before the ARP sweep.");
        }
        finally { ScanSource.PingOverride = null; }
    }

    private static async Task NoPassEverBurstsPastThePacedWidth()
    {
        // Synthetic overload boundary: a target stops answering if probed with more than
        // 64 requests in flight. This verifies concurrency, not a real switch's packet rate.
        int inFlight = 0, peak = 0, shortestTimeout = int.MaxValue;
        var dropped = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>();
        var pings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        ScanSource.PingOverride = async (address, timeout) =>
        {
            string ip = address.ToString();
            pings.AddOrUpdate(ip, 1, (_, count) => count + 1);
            int now = Interlocked.Increment(ref inFlight);
            try
            {
                InterlockedMax(ref peak, now);
                await Task.Delay(30);
                if (now > 64) dropped[ip] = true;
                bool live = int.Parse(ip.Split('.')[3]) % 3 == 0;
                // Recorded, not asserted here: the scanner swallows a fake's exception and the
                // failure would surface later under another message.
                InterlockedMin(ref shortestTimeout, timeout);
                return (live && !dropped.ContainsKey(ip), 64);
            }
            finally { Interlocked.Decrement(ref inFlight); }
        };
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "");
            var found = await Quick("192.0.2.1-254", new ScanOptions().Without("tcp-liveness"));
            var expected = Enumerable.Range(1, 254).Where(n => n % 3 == 0).Select(n => "192.0.2." + n).ToList();
            Require(peak > 0 && peak <= NetworkScanner.PacedPassWidth,
                $"No liveness pass has more than {NetworkScanner.PacedPassWidth} pings in flight (saw {peak}).");
            Require(shortestTimeout >= 1000, $"Every liveness ping outlasts one ARP retransmit (shortest was {shortestTimeout} ms).");
            var missing = expected.Where(ip => found.All(d => d.IpAddress != ip)).ToList();
            Require(missing.Count == 0, $"Every live host is found by the first wave; missing {missing.Count}: {string.Join(", ", missing.Take(8))}");
            Require(expected.All(ip => pings[ip] == 1), "A host found in the first wave is not pinged again.");
            Require(pings.Where(pair => !expected.Contains(pair.Key)).All(pair => pair.Value == 2),
                "On a link that answers the first wave, silent hosts get the second pass and no extra rounds.");
        }
        finally { ScanSource.PingOverride = null; }
    }

    private static async Task RoutedAddressesKeepTheirSecondPassUnderTheBudget()
    {
        // The second pass has a budget (SecondPassLimit). A routed address has nothing else: no
        // sweep reaches it. So routed addresses spend the budget first, and on-link ones take
        // what is left, instead of a local range crowding a routed one out of its only retry.
        int originalLimit = NetworkScanner.SecondPassLimit;
        NetworkScanner.SecondPassLimit = 4;
        // Only the second pass's pings count here (by its wait); the extra rounds after it have
        // their own budget and wait.
        var pings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        ScanSource.PingOverride = (address, timeout) =>
        {
            bool second = timeout == NetworkScanner.SecondPingTimeoutMs;
            int n = pings.AddOrUpdate(address.ToString(), second ? 2 : 1, (_, count) => second ? 2 : count);
            return Task.FromResult((address.ToString() == "192.0.2.2" && second, 64));
        };
        try
        {
            // .1-.3 sit behind a router; .4-.12 are on-link. Twelve silent addresses, budget of four.
            NetworkPlatform.Current = new ArpPlatform(ip => int.Parse(ip.Split('.')[3]) <= 3 ? "192.0.2.254" : "", _ => "");
            var found = await Quick("192.0.2.1-12", new ScanOptions().Without("tcp-liveness"));
            Require(found.Count == 1 && found[0].IpAddress == "192.0.2.2", "A routed host that answers only its second ping is found when the range is over budget.");
            Require(pings["192.0.2.1"] == 2 && pings["192.0.2.3"] == 2, "Every routed address gets its second ping first.");
            Require(pings.Count(p => p.Value == 2) == 4, "On-link addresses take only the budget the routed ones leave.");
        }
        finally
        {
            ScanSource.PingOverride = null;
            NetworkScanner.SecondPassLimit = originalLimit;
        }
    }

    private static async Task TheSweepLaunchesInPacedBatches()
    {
        // The customer's switches drop a burst of 250 ARP requests sent at once. The sweep keeps
        // its wide pool, because dead addresses hold SendARP for seconds, but starts requests in
        // small batches with a pause between them. This network answers only when few requests
        // are in flight; every address holds its call for a while, like a real dead address.
        int originalBatch = NetworkScanner.ArpStartBatch;
        var originalInterval = NetworkScanner.ArpStartInterval;
        NetworkScanner.ArpStartBatch = 4;
        NetworkScanner.ArpStartInterval = TimeSpan.FromMilliseconds(150);
        try
        {
            ArpPlatform platform = null!;
            platform = new ArpPlatform(_ => "", ip =>
            {
                bool calm = platform.Active <= 4;
                Thread.Sleep(100);
                return calm && int.Parse(ip.Split('.')[3]) % 2 == 0 ? "00:04:F2:00:00:" + int.Parse(ip.Split('.')[3]).ToString("X2") : "";
            });
            NetworkPlatform.Current = platform;
            var found = await Quick("192.0.2.1-16", Silent);
            Require(platform.PeakActive <= 4, $"No more than one batch of ARP requests is in flight at once (saw {platform.PeakActive}).");
            Require(found.Count == 8, $"Every host that answers a calm ARP request is found ({found.Count} of 8).");
        }
        finally
        {
            NetworkScanner.ArpStartBatch = originalBatch;
            NetworkScanner.ArpStartInterval = originalInterval;
        }
    }

    private static async Task ThePacedPassReportsProgress()
    {
        // The paced pass can hold the scan for seconds; the bar must move while it does.
        ScanSource.PingOverride = async (_, _) => { await Task.Delay(20); return (false, 0); };
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "");
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            var seen = new System.Collections.Concurrent.ConcurrentBag<int>();
            scanner.ProgressChanged += p => seen.Add(p);
            await Quick("192.0.2.1-200", new ScanOptions().Without("tcp-liveness,arp"), scanner);
            Require(seen.Any(p => p is > 25 and < 30), "Progress moves between 25 and 30 while the paced pass runs: " + string.Join(",", seen.Distinct().OrderBy(p => p)));
        }
        finally { ScanSource.PingOverride = null; }
    }

    private static async Task ABusyNetworkIsProbedUntilAPassAddsNothing()
    {
        // A customer's network: 1.9.3 found 141 one day and 83 the next with the same exe, and
        // 1.9.4 found 97 from the command line and 119 from the window. Phones and small
        // switches throttle after a probe and answer when asked again a little later, so a fixed
        // number of passes finds whatever happened to answer that day. Discovery now keeps going:
        // while a pass still adds hosts the network is still hiding some, so the silent addresses
        // are asked again, narrower and slower, until a pass adds nothing. Here .20 answers its
        // second ping, .10 its third and .30 its fourth; the rest of the live hosts answer first
        // time, and the dead addresses never do.
        int inFlight = 0, peakLate = 0;
        var pings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        ScanSource.PingOverride = async (address, timeout) =>
        {
            string ip = address.ToString();
            int n = pings.AddOrUpdate(ip, 1, (_, count) => count + 1);
            int now = Interlocked.Increment(ref inFlight);
            try
            {
                if (n >= 3) InterlockedMax(ref peakLate, now);
                await Task.Delay(20);
                int last = int.Parse(ip.Split('.')[3]);
                int answersOn = last switch { 20 => 2, 10 => 3, 30 => 4, _ => last % 3 == 0 ? 1 : int.MaxValue };
                return (n >= answersOn, 64);
            }
            finally { Interlocked.Decrement(ref inFlight); }
        };
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "");
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            var found = await Quick("192.0.2.1-120", new ScanOptions().Without("tcp-liveness,arp"), scanner);
            foreach (string ip in new[] { "192.0.2.20", "192.0.2.10", "192.0.2.30" })
                Require(found.Any(d => d.IpAddress == ip), $"A host that answers a later probe is found ({ip}, pinged {(pings.TryGetValue(ip, out int times) ? times : 0)} times).");
            Require(pings.Values.Max() == 5, $"After the pass that found .30 one more pass runs, adds nothing, and discovery stops (max {pings.Values.Max()} pings).");
            Require(peakLate > 0 && peakLate <= NetworkScanner.ConvergeWidth,
                $"The extra rounds keep at most {NetworkScanner.ConvergeWidth} pings in flight (saw {peakLate}).");
            foreach (string part in new[] { "first wave 39", "second pass +1", "ARP sweep +0", "extra rounds +2 in 3" })
                Require(scanner.DiscoverySummary.Contains(part), $"The scanner reports what each phase found ({part}): " + scanner.DiscoverySummary);
        }
        finally { ScanSource.PingOverride = null; }
    }

    // .5 answers only its second ping, so the second pass adds a host and the extra rounds run.
    private static Func<IPAddress, int, Task<(bool, int)>> LateHostPing(System.Collections.Concurrent.ConcurrentDictionary<string, int> pings, int delayMs) =>
        async (address, _) =>
        {
            string ip = address.ToString();
            int n = pings.AddOrUpdate(ip, 1, (_, c) => c + 1);
            await Task.Delay(delayMs);
            return (ip == "192.0.2.5" && n >= 2, 64);
        };

    private static async Task TheExtraRoundsReportProgress()
    {
        // The rounds wait out a full timeout on every dead address, which on a flat /22 is minutes.
        // The bar has to move through 30 to 40 while they do, not sit at 30.
        var pings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        ScanSource.PingOverride = LateHostPing(pings, 5);
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "");
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            var seen = new System.Collections.Concurrent.ConcurrentBag<int>();
            scanner.ProgressChanged += p => { if (p > 30 && p < 40) seen.Add(p); };
            await Quick("192.0.2.1-60", new ScanOptions().Without("tcp-liveness,arp"), scanner);
            Require(scanner.DiscoverySummary.Contains("extra rounds +0 in 1"), "The extra rounds ran: " + scanner.DiscoverySummary);
            Require(seen.Distinct().Count() >= 3, $"Progress moves between 30 and 40 during the extra rounds (saw {seen.Distinct().Count()} values).");
        }
        finally { ScanSource.PingOverride = null; }
    }

    private static async Task TheExtraRoundsStopAtTheirTimeBudget()
    {
        // A slow network must not turn three rounds into minutes: once the budget is spent the
        // rounds stop asking, and the summary says so.
        var original = NetworkScanner.ConvergeBudget;
        NetworkScanner.ConvergeBudget = TimeSpan.FromMilliseconds(300);
        var pings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        ScanSource.PingOverride = LateHostPing(pings, 100);
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "");
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            var clock = Stopwatch.StartNew();
            await Quick("192.0.2.1-200", new ScanOptions().Without("tcp-liveness,arp"), scanner);
            int roundPings = pings.Values.Sum() - 2 * 200;
            Require(roundPings > 0 && roundPings < 199, $"The extra rounds stop asking once their budget is spent ({roundPings} round pings).");
            Require(scanner.DiscoverySummary.EndsWith("(time budget reached)"), "The summary says the budget ran out: " + scanner.DiscoverySummary);
        }
        finally { ScanSource.PingOverride = null; NetworkScanner.ConvergeBudget = original; }
    }

    private static async Task ALossyLinkKeepsRecoveringPastTheNormalBudget()
    {
        // Phil's Realtek: the first wave caught 86 of 152 hosts and every later pass kept finding
        // more until the 30-second budget cut it off. When the passes after the first wave recover
        // over a tenth of it, the link is dropping replies, and the rounds must keep going while
        // they still find hosts instead of stopping at the healthy-network limits.
        var original = NetworkScanner.ConvergeBudget;
        NetworkScanner.ConvergeBudget = TimeSpan.FromMilliseconds(400);
        var pings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        ScanSource.PingOverride = async (address, _) =>
        {
            int last = address.GetAddressBytes()[3];
            int n = pings.AddOrUpdate(address.ToString(), 1, (_, c) => c + 1);
            await Task.Delay(5);
            // .1-.20 answer at once; .21-.40 need between 2 and 7 asks; .41-.60 are dead.
            return (last <= 20 || (last <= 40 && n >= 2 + last % 6), 64);
        };
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "");
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            await Quick("192.0.2.1-60", new ScanOptions().Without("tcp-liveness,arp"), scanner);
            Require(scanner.DiscoverySummary.Contains("lossy link"), "The summary names the lossy link: " + scanner.DiscoverySummary);
            Require(DiscoveredCount(scanner) == 40, "Every host that answers eventually is found: " + scanner.DiscoverySummary);
        }
        finally { ScanSource.PingOverride = null; NetworkScanner.ConvergeBudget = original; }
    }

    private static async Task ALossyLinkAsksArpInEveryRound()
    {
        // On Phil's Realtek each extra round recovered only about three hosts by ping alone,
        // while an ARP scanner found all of them. On a lossy link every round also asks ARP for
        // each silent on-link address, so a host gets two independent chances per round.
        // Here .31-.40 never answer ping, and answer ARP only once the extra rounds have begun
        // (the first third ping anywhere), which the sweep never reaches.
        var pings = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        int roundsBegan = 0;
        ScanSource.PingOverride = async (address, _) =>
        {
            int last = address.GetAddressBytes()[3];
            int n = pings.AddOrUpdate(address.ToString(), 1, (_, c) => c + 1);
            if (n >= 3) Volatile.Write(ref roundsBegan, 1);
            await Task.Delay(5);
            return (last <= 20 || (last <= 30 && n >= 2), 64);
        };
        var arps = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", ip =>
            {
                int last = int.Parse(ip.Split('.')[3]);
                arps.AddOrUpdate(ip, 1, (_, c) => c + 1);
                return last > 30 && last <= 40 && Volatile.Read(ref roundsBegan) == 1 ? $"00:11:22:33:44:{last:X2}" : "";
            });
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            await Quick("192.0.2.1-60", new ScanOptions().Without("tcp-liveness"), scanner);
            Require(scanner.DiscoverySummary.Contains("lossy link"), "The link counts as lossy: " + scanner.DiscoverySummary);
            Require(DiscoveredCount(scanner) == 40, "Hosts that only answer a later ARP request are found: " + scanner.DiscoverySummary + "; ARP asks for .35: " + (arps.TryGetValue("192.0.2.35", out int a35) ? a35 : 0) + ", pings for .35: " + (pings.TryGetValue("192.0.2.35", out int p35) ? p35 : 0));
        }
        finally { ScanSource.PingOverride = null; }
    }

    private static int DiscoveredCount(NetworkScanner scanner)
    {
        var m = System.Text.RegularExpressions.Regex.Matches(scanner.DiscoverySummary, @"(?:first wave |\+)(\d+)");
        return m.Cast<System.Text.RegularExpressions.Match>().Sum(x => int.Parse(x.Groups[1].Value));
    }

    private static async Task TheDiscoveryDetailReportsTheCacheAndTimings()
    {
        // The two theories for a lost host, a switch dropping a burst and Windows evicting
        // neighbor entries past its per-interface limit, look the same in the found counts. The
        // detail line carries what separates them: the cache size before and after, the limit,
        // and how long each pass took.
        var original = NetworkScanner.NeighborCacheLimitReader;
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "", read => Enumerable.Range(1, read == 1 ? 3 : 5)
                .ToDictionary(i => "198.51.100." + i, i => $"00:11:22:33:44:{i:X2}"));
            NetworkScanner.NeighborCacheLimitReader = () => 256;
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            await Quick("192.0.2.1-10", new ScanOptions().Without("tcp-liveness"), scanner);
            Require(System.Text.RegularExpressions.Regex.IsMatch(scanner.DiscoveryDetail,
                @"^neighbor cache 3 entries before, 5 after, limit 256 per interface; first wave \d+\.\d s, second pass \d+\.\d s, ARP sweep \d+\.\d s, extra rounds \d+\.\d s$"),
                "The detail line reports the cache, its limit and each pass's time: " + scanner.DiscoveryDetail);

            NetworkScanner.NeighborCacheLimitReader = () => null;
            await Quick("192.0.2.1-10", new ScanOptions().Without("tcp-liveness"), scanner);
            Require(scanner.DiscoveryDetail.Contains("limit unknown"), "An unreadable limit is reported as unknown: " + scanner.DiscoveryDetail);
#if NET
            if (OperatingSystem.IsWindows())
#endif
            {
                Require(WindowsNetworkPlatform.ParseNeighborCacheLimit("Default Hop Limit                   : 128 hops\r\nNeighbor Cache Limit                : 256 entries per interface\r\n") == 256
                    && WindowsNetworkPlatform.ParseNeighborCacheLimit("Limite du cache : 256") == null,
                    "The limit is read from netsh's English output and is unknown otherwise.");
            }
        }
        finally { NetworkScanner.NeighborCacheLimitReader = original; }
    }

    private static async Task CancelDuringThePacedPassReturnsPromptly()
    {
        // A ping cannot be canceled, but the scan must not wait out every in-flight one. The
        // 100th call lands in the first wave's second round of 64, with the rest of that round
        // and the next still to come.
        int calls = 0;
        using var cancel = new CancellationTokenSource();
        var clock = new Stopwatch();
        ScanSource.PingOverride = async (_, _) =>
        {
            if (Interlocked.Increment(ref calls) == 100) { clock.Start(); cancel.Cancel(); }
            await Task.Delay(3000);
            return (false, 0);
        };
        try
        {
            NetworkPlatform.Current = new ArpPlatform(_ => "", _ => "");
            try
            {
                await new NetworkScanner(new IsolatedDiscovery()).ScanSubnetAsync("192.0.2.1-254", cancel.Token, new ScanOptions().Without("tcp-liveness"), fullScan: false);
                Require(false, "A canceled scan throws.");
            }
            catch (OperationCanceledException) { }
            Require(clock.ElapsedMilliseconds < 2000, $"Cancel during the paced pass returns without waiting out the pings ({clock.ElapsedMilliseconds} ms).");
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

    private static void InterlockedMin(ref int target, int value)
    {
        int seen;
        while ((seen = target) > value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

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
