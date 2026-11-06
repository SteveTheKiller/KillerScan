using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KillerScan.Engine
{
    public class NetworkScanner
    {
        private readonly IScanDiscovery _discovery;
        public NetworkScanner() : this(new MulticastScanDiscovery()) { }
        public NetworkScanner(IScanDiscovery discovery) => _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        private static readonly SemaphoreSlim NativeMacSlots = new(8);

        // A dead address holds its SendARP call for about 3.7 seconds. Measured on SN02 on a /24 with
        // four live hosts: 64 at a time added 16 seconds to a 6-second quick scan, 256 added 4.
        private static readonly SemaphoreSlim ArpSweepSlots = new(256);

        /// <summary>The most silent on-link addresses the ARP sweep asks one by one: 1,024 (a /22)
        /// is four waves of 256, about 15 seconds. A larger range relies on ping, TCP liveness and
        /// the neighbor cache.</summary>
        internal static int ArpSweepLimit = 1024;

        /// <summary>The sweep starts this many ARP requests at a time, then waits
        /// <see cref="ArpStartInterval"/> before the next batch. The pool above stays wide so dead
        /// addresses can hold their calls, but no instant carries a burst the customer's switches
        /// would drop as a broadcast storm: 254 addresses start over about a second, and the
        /// quick-failure retry waits a batch interval too.</summary>
        internal static int ArpStartBatch = 64;
        internal static TimeSpan ArpStartInterval = TimeSpan.FromMilliseconds(250);

        /// <summary>A SendARP that fails faster than this never waited out Windows' retransmits
        /// (a dead address takes about 4 seconds), so the sweep asks that address once more.</summary>
        internal static TimeSpan ArpQuickFailure = TimeSpan.FromSeconds(1.5);

        /// <summary>Tests only: this PC's adapter MACs and IPv4 addresses, in place of the real adapters.</summary>
        internal static Func<(HashSet<string> Macs, HashSet<string> Addresses)>? LocalIdentityOverride { get; set; }

        // This PC's own adapter MACs and addresses, read once per run on first use (off the UI
        // thread). SendARP for the subnet broadcast succeeds with this PC's own MAC, so an
        // answer carrying one of our MACs is only believed for one of our own addresses.
        private static Lazy<(HashSet<string> Macs, HashSet<string> Addresses)> _localIdentity = new(ReadLocalIdentity);

        private static (HashSet<string> Macs, HashSet<string> Addresses) ReadLocalIdentity()
        {
            if (LocalIdentityOverride is { } fake) return fake();
            var macs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addresses = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    byte[] bytes = adapter.GetPhysicalAddress().GetAddressBytes();
                    if (bytes.Length == 6) macs.Add(string.Join(":", bytes.Select(b => b.ToString("X2"))));
                    try
                    {
                        foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                            if (unicast.Address.AddressFamily == AddressFamily.InterNetwork) addresses.Add(unicast.Address.ToString());
                    }
                    catch (NetworkInformationException) { }
                }
            }
            catch (NetworkInformationException) { }
            return (macs, addresses);
        }

        private static bool IsOwnMacElsewhere(IPAddress address, string mac)
        {
            var local = _localIdentity.Value;
            return local.Macs.Contains(mac) && !local.Addresses.Contains(address.ToString());
        }

        /// <summary>Both liveness pings outlast one ARP retransmit (Windows retries a lost ARP
        /// after 1 second). The first wave used to wait 500 ms so a quiet range moved fast, but a
        /// ping whose ARP request the switch dropped could then never succeed in the first wave
        /// at all, and Windows filed the address as a failed neighbor, which made the later
        /// passes fail too. A quiet /24 now takes about six seconds instead of one.</summary>
        private const int FirstPingTimeoutMs = 1500, SecondPingTimeoutMs = 1200;

        /// <summary>The most silent addresses the second liveness pass asks again.</summary>
        internal static int SecondPassLimit = 1024;

        /// <summary>How many pings each liveness pass keeps in flight. Switches rate-limit
        /// broadcast storms, so a 200-wide first wave on a cold cache was 200 ARP requests in one
        /// burst, and on a /24 with 160 live hosts it lost answers: one customer's scan found 56
        /// devices from a cleared cache, and even with a paced second pass the phones and small
        /// switches whose first ARP was dropped stayed missing, because Windows had filed them
        /// as failed neighbors. Measured here, the 200-wide wave also missed a host that a
        /// 16-wide pass found. 64 keeps the switch under its limit; see FirstPingTimeoutMs for
        /// what it costs a quiet range.</summary>
        internal const int PacedPassWidth = 64;

        // Common ports to probe for device type detection
        private static readonly int[] ProbePorts = [
            22,    // SSH
            53,    // DNS
            88,    // Kerberos (Active Directory)
            80,    // HTTP
            135,   // Windows RPC
            443,   // HTTPS
            445,   // SMB
            464,   // Kerberos password service
            515,   // LPR printing
            389,   // LDAP (Active Directory)
            631,   // IPP (printers)
            636,   // LDAPS (Active Directory)
            902,   // VMware ESXi
            2179,  // Hyper-V
            3389,  // RDP
            8006,  // Proxmox
            8123,  // Home Assistant
            5000,  // Synology DSM HTTP
            5001,  // Synology DSM HTTPS
            9100,  // RAW printing
            161,   // SNMP
            8080,  // HTTP alt
            8443,  // HTTPS alt
            21,    // FTP
            23,    // Telnet
            548,   // AFP (Mac file sharing)
            5353,  // mDNS
            1900,  // SSDP/UPnP
            62078, // Apple iDevice
            139,   // NetBIOS session (SMB legacy)
            554,   // RTSP (IP cameras)
            1883,  // MQTT (IoT brokers)
            8883,  // MQTT over TLS
            5357,  // WSD (Web Services for Devices)
            32400, // Plex media server
            3268,  // Active Directory global catalog
            3269,  // Active Directory global catalog TLS
        ];

        // Ports probed to decide a host is alive when ICMP is filtered. Kept short and high-signal
        // (SMB, RDP, web, SSH) so the discovery sweep stays fast; the full ProbePorts scan runs later
        // only for hosts already found alive.
        private static readonly int[] LivenessPorts = [445, 3389, 80, 443, 22];

        // A MAC resolved for more than this many distinct IPs is treated as a next-hop/gateway artifact
        // (routed or VPN link) rather than a real per-host address, and is discarded. Real LAN hosts each
        // have a unique MAC, so a low ceiling is safe; 2 tolerates the odd multi-homed NIC.
        private const int MaxIpsPerMac = 2;

        public static void FlushLocalDnsCache() => NetworkPlatform.Current.FlushDnsCache();

        /// <summary>Stage changes. The host turns each one into text in its own language.</summary>
        public event Action<ScanStatus>? StatusChanged;
        public event Action<int>? ProgressChanged;
        public event Action<NetworkDevice>? DeviceFound;
        protected void ReportDeviceFound(NetworkDevice device) => DeviceFound?.Invoke(device);

        /// <summary>
        /// A manual device type keyed by MAC address. When it returns a value, that type wins over
        /// classification. Set once by the host; unset means no manual types.
        /// </summary>
        public static Func<string, string?>? ManualTypeLookup { get; set; }

        /// <summary>
        /// The adapter this scanner's probes are sent from, or null to let Windows pick the
        /// route. While set, targets outside the adapter's network are refused.
        /// </summary>
        /// <summary>The adapter scans go out of. A different one means a different network, so the
        /// multicast names and services a scan collected are dropped with the change.</summary>
        public ScanBinding? Binding
        {
            get => _binding;
            set { if (!Equals(_binding, value)) { _mdns = []; _ssdp = []; } _binding = value; }
        }
        private ScanBinding? _binding;

        /// <summary>Tests only: the NetBIOS port and the deep sweep's port list.</summary>
        internal static int? UdpPortOverride { get; set; }
        internal static int[]? DeepPortsOverride { get; set; }

        // Applies the binding to this run, after checking every target is on the adapter's network.
        private void BeginRun(IEnumerable<IPAddress> targets)
        {
            if (Binding is { } binding)
            {
                foreach (var address in targets)
                    if (!binding.Reaches(address, out string route))
                        throw new ArgumentException(binding.Unreachable(address, route) +
                            " A bound scan only reaches its own network and what Windows routes through it.");
            }
            ScanSource.Current = Binding;
            // Adapters come and go between runs (a VPN, a dock), so each run reads them afresh.
            _localIdentity = new(ReadLocalIdentity);
        }

        /// <summary>
        /// Runs on every finished device before it is reported, so the host can apply saved
        /// preferences such as a user-given name. Set once by the host.
        /// </summary>
        public static Action<NetworkDevice>? DeviceCompleted { get; set; }

        /// <summary>
        /// This scanner's own manual types. When set it replaces <see cref="ManualTypeLookup"/>,
        /// so hosts that keep separate saved data per site can run scanners side by side.
        /// </summary>
        public Func<string, string?>? ManualTypes { get; set; }

        /// <summary>This scanner's own completion hook. When set it replaces <see cref="DeviceCompleted"/>.</summary>
        public Action<NetworkDevice>? Completed { get; set; }

        private void Finish(NetworkDevice device)
        {
            // A scan bound to another adapter is on another network: the default adapter's gateway
            // is not its router, so no device there is made Router by that address.
            bool foreign = Binding != null && IPAddress.TryParse(GatewayIp, out var gateway) && !Binding.Contains(gateway);
            device.Recognition = AssessDevice(device, ManualTypes ?? ManualTypeLookup, foreign ? string.Empty : GatewayIp, foreign ? string.Empty : DnsIp);
            device.DeviceType = device.Recognition.Type;
            (Completed ?? DeviceCompleted)?.Invoke(device);
        }

        // Network-wide service discovery results (collected once per scan, keyed by IP).
        private Dictionary<string, MulticastDiscovery.MdnsInfo> _mdns = [];
        private Dictionary<string, string> _ssdp = [];

        /// <summary>
        /// Parse a CIDR subnet string into a list of IP addresses.
        /// </summary>
        public static List<IPAddress> GetAddressesInSubnet(string cidr)
        {
            if (string.IsNullOrWhiteSpace(cidr))
                throw new ArgumentException("An IPv4 subnet is required.", nameof(cidr));
            var parts = cidr.Trim().Split('/');
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException("The subnet must contain an IPv4 address and an optional prefix.", nameof(cidr));
            int prefixLen = 24;
            if (parts.Length == 2 && !int.TryParse(parts[1], out prefixLen))
                throw new ArgumentException("The CIDR prefix must be an integer between 0 and 32.", nameof(cidr));
            if (prefixLen < 0 || prefixLen > 32)
                throw new ArgumentOutOfRangeException(nameof(cidr), "CIDR prefix must be between 0 and 32.");
            long hostCount = prefixLen >= 31 ? 0 : (1L << (32 - prefixLen)) - 2;
            if (hostCount > ScanTargets.MaxAddresses)
                throw new ArgumentOutOfRangeException(nameof(cidr),
                    $"Subnet expands to {hostCount:N0} hosts, exceeding the {ScanTargets.MaxAddresses:N0} address limit.");
            if (hostCount == 0) return [];

            byte[] ipBytes = ip.GetAddressBytes();
            uint ipUint = (uint)(ipBytes[0] << 24 | ipBytes[1] << 16 | ipBytes[2] << 8 | ipBytes[3]);

            uint mask = prefixLen == 0 ? 0 : uint.MaxValue << (32 - prefixLen);
            uint network = ipUint & mask;
            uint broadcast = network | ~mask;

            var addresses = new List<IPAddress>();
            for (uint addr = network + 1; addr < broadcast; addr++)
            {
                addresses.Add(new IPAddress([
                    (byte)(addr >> 24), (byte)(addr >> 16),
                    (byte)(addr >> 8), (byte)addr
                ]));
            }
            return addresses;
        }

        /// <summary>
        /// Scan all hosts in the given subnet using ARP + ping combined approach.
        /// </summary>
        public virtual Task<List<NetworkDevice>> ScanSubnetAsync(string cidr, CancellationToken ct, bool fullScan = true) =>
            ScanSubnetAsync(cidr, ct, new ScanOptions(), fullScan);

        public virtual async Task<List<NetworkDevice>> ScanSubnetAsync(
            string cidr, CancellationToken ct, ScanOptions options, bool fullScan = true)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            // ScanTargets handles one or many comma-separated targets (CIDR, single host, or a
            // range) and de-duplicates overlaps. Bad input is refused before any side effect.
            var parsed = ScanTargets.Parse(cidr);
            if (!parsed.Ok)
                throw new ArgumentException(parsed.Error switch
                {
                    TargetError.Empty => "No scan target was given.",
                    TargetError.TooLarge => $"The targets cover {parsed.Detail} addresses; the ceiling is {ScanTargets.MaxAddresses:N0}.",
                    _ => $"\"{parsed.Detail}\" is not a CIDR block, host, or range."
                }, nameof(cidr));
            BeginRun(parsed.Addresses);
            ct.ThrowIfCancellationRequested();
            if (options.ReverseDns) FlushLocalDnsCache();
            _mdns = [];
            _ssdp = [];
            var addresses = parsed.Addresses;
            // Status text uses the short summary ("192.168.9.0/24 +2"), not the raw box contents,
            // so a long multi-target list does not overrun the status bar.
            string label = parsed.Summary.Length > 0 ? parsed.Summary : cidr;
            var devices = new List<NetworkDevice>();
            int completed = 0;
            int total = addresses.Count;

            // Phase 1: Fast ping sweep + ARP cache
            StatusChanged?.Invoke(new ScanStatus(ScanStage.Discovering, addresses.Count, label));
            var discoveredHosts = new System.Collections.Concurrent.ConcurrentDictionary<string, (IPAddress Addr, string Mac)>();

            // Grab existing ARP cache first (catches IoT devices). It starts arp.exe, so it runs on the
            // pool: this is still the synchronous start of the scan, on the caller's UI thread.
            IReadOnlyDictionary<string, string> arpCache = options.NeighborCache
                ? await Task.Run(GetArpCache, ct) : new Dictionary<string, string>();
            var addressSet = new HashSet<string>(addresses.Select(a => a.ToString()));
            foreach (var entry in arpCache)
            {
                if (addressSet.Contains(entry.Key))
                    discoveredHosts.TryAdd(entry.Key, (IPAddress.Parse(entry.Key), entry.Value));
            }

            // Kick off network-wide service discovery (mDNS + SSDP) to run alongside the ping sweep;
            // results are awaited before the probe phase and mapped to hosts by source IP.
            var mdnsTask = options.Mdns ? _discovery.CollectMdnsAsync(ct)
                : Task.FromResult(new Dictionary<string, MulticastDiscovery.MdnsInfo>());
            var ssdpTask = options.Ssdp ? _discovery.CollectSsdpAsync(ct)
                : Task.FromResult(new Dictionary<string, string>());

            // Liveness wave: ping, then TCP on a few ports for hosts that drop ICMP (Windows Firewall
            // default). ARP cannot reach a host across a router or VPN, so for those this is the
            // only evidence, and it is what closes the "20 found vs 200 real" gap over a VPN.
            // Paced at PacedPassWidth: this wave is what fills the neighbor cache, and a wider burst
            // is what the switch drops.
            var semaphore = new SemaphoreSlim(PacedPassWidth);
            async Task<bool> AliveAsync(IPAddress addr, int pingTimeoutMs)
            {
                if (options.Icmp && (await ScanSource.PingAsync(addr, pingTimeoutMs)).Success) return true;
                return options.TcpLiveness && await TcpAliveAsync(addr);
            }

            var scanTasks = addresses.Select(async addr =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    if (await AliveAsync(addr, FirstPingTimeoutMs))
                        discoveredHosts.TryAdd(addr.ToString(), (addr, ""));
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                finally
                {
                    semaphore.Release();
                    int done = Interlocked.Increment(ref completed);
                    ProgressChanged?.Invoke((int)(done * 25.0 / total));
                }
            });

            await Task.WhenAll(scanTasks);
            ct.ThrowIfCancellationRequested();

            // Second ARP cache read: the ping wave stimulates ARP responses from devices
            // that are too slow (or don't reply to ICMP at all). Reading the cache again
            // catches them without requiring a second full scan.
            IReadOnlyDictionary<string, string> arpCache2 = options.NeighborCache
                ? await Task.Run(GetArpCache, ct) : new Dictionary<string, string>();
            foreach (var entry in arpCache2)
            {
                if (addressSet.Contains(entry.Key))
                    discoveredHosts.TryAdd(entry.Key, (IPAddress.Parse(entry.Key), entry.Value));
            }

            // Second, paced liveness pass for every address still silent. The first wave is wide for
            // speed, and on a busy LAN that is exactly what loses hosts: with every address needing
            // a fresh ARP resolution at once, Windows' neighbor cache fills and the switch's storm
            // control trips, so answers from hosts that reply in under a millisecond never arrive.
            // This pass keeps PacedPassWidth pings in flight and waits out at least one ARP
            // retransmit (1 second), so those hosts, and a routed host whose first ping was lost
            // while its next hop resolved, answer here.
            var silentNow = addresses.Where(a => !discoveredHosts.ContainsKey(a.ToString())).ToList();
            // Which silent addresses Windows reaches directly, and so can be swept with ARP. Only
            // asked when ARP is on: one route lookup per address, off the caller's (UI) thread.
            var onLink = options.Arp
                ? await Task.Run(() => new HashSet<string>(silentNow.Where(a =>
                    { ct.ThrowIfCancellationRequested(); return IsOnLink(a); }).Select(a => a.ToString())), ct)
                : new HashSet<string>();
            if ((options.Icmp || options.TcpLiveness) && silentNow.Count > 0)
            {
                // The pass has a budget. A routed address has nothing else, since no sweep reaches
                // it, so routed addresses spend the budget first and on-link ones take what is left.
                var secondPass = silentNow.Where(a => !onLink.Contains(a.ToString()))
                    .Concat(silentNow.Where(a => onLink.Contains(a.ToString())))
                    .Take(SecondPassLimit).ToList();
                var paced = new SemaphoreSlim(PacedPassWidth);
                int pacedDone = 0;
                await Task.WhenAll(secondPass.Select(async addr =>
                {
                    await paced.WaitAsync(ct);
                    try
                    {
                        // Pings cannot be canceled; a canceled scan stops waiting for them instead.
                        if (await AwaitWithCancellation(AliveAsync(addr, SecondPingTimeoutMs), ct))
                            discoveredHosts.TryAdd(addr.ToString(), (addr, ""));
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    finally
                    {
                        paced.Release();
                        int done = Interlocked.Increment(ref pacedDone);
                        ProgressChanged?.Invoke(25 + (int)(done * 3.0 / secondPass.Count));
                    }
                }));
                ct.ThrowIfCancellationRequested();
                silentNow = silentNow.Where(a => !discoveredHosts.ContainsKey(a.ToString())).ToList();
            }

            // ARP sweep: phones, printers and locked-down devices often drop ping, expose none of the
            // liveness ports and age out of the neighbor cache before the second read. Ask every
            // remaining on-link address directly, the way an ARP scanner does. Dead addresses block
            // SendARP for a few seconds, so the sweep has its own wider pool of dedicated threads and
            // reports progress (28 to 30) as each address answers or times out. Requests start in
            // batches of ArpStartBatch with a pause between, so the whole range never goes out as
            // one broadcast burst for a switch to drop. A range with more silent addresses than
            // ArpSweepLimit is left to the checks above.
            var sweptOnly = new HashSet<string>();
            if (options.Arp)
            {
                var silent = silentNow.Where(a => onLink.Contains(a.ToString()) && !discoveredHosts.ContainsKey(a.ToString())).ToList();
                if (silent.Count <= ArpSweepLimit)
                {
                    int swept = 0;
                    var answered = new System.Collections.Concurrent.ConcurrentBag<string>();
                    var arpTasks = new List<Task>(silent.Count);
                    for (int start = 0; start < silent.Count; start += ArpStartBatch)
                    {
                        if (start > 0) await Task.Delay(ArpStartInterval, ct);
                        arpTasks.AddRange(silent.Skip(start).Take(ArpStartBatch).Select(async addr =>
                        {
                            var (mac, took) = await ResolveMacTimedAsync(addr, ArpSweepSlots, ct, dedicated: true);
                            // A dead address holds SendARP for seconds while Windows retransmits. One
                            // that fails at once never got those retransmits, so it is asked once
                            // more, after a batch interval so the retry lands outside the burst.
                            if (string.IsNullOrEmpty(mac) && took < ArpQuickFailure)
                            {
                                await Task.Delay(ArpStartInterval, ct);
                                mac = await ResolveMacAsync(addr, ArpSweepSlots, ct, dedicated: true);
                            }
                            if (!string.IsNullOrEmpty(mac) && discoveredHosts.TryAdd(addr.ToString(), (addr, mac)))
                                answered.Add(addr.ToString());
                            int done = Interlocked.Increment(ref swept);
                            ProgressChanged?.Invoke(28 + (int)(done * 2.0 / silent.Count));
                        }));
                    }
                    await Task.WhenAll(arpTasks);
                    ct.ThrowIfCancellationRequested();
                    sweptOnly.UnionWith(answered);
                }
            }

            // Last look at the neighbor cache: SendARP can give up on a host whose reply Windows
            // records a moment later, and reading the cache sends nothing.
            if (options.NeighborCache)
            {
                foreach (var entry in await Task.Run(GetArpCache, ct))
                    if (addressSet.Contains(entry.Key))
                        discoveredHosts.TryAdd(entry.Key, (IPAddress.Parse(entry.Key), entry.Value));
            }

            // Resolve MAC addresses via ARP for discovered hosts (fast, they're alive)
            StatusChanged?.Invoke(new ScanStatus(ScanStage.ResolvingMacs, discoveredHosts.Count));
            var macTasks = discoveredHosts.Keys.ToList().Select(async ip =>
            {
                var addr = IPAddress.Parse(ip);
                string mac = discoveredHosts[ip].Mac;
                if (options.Arp && string.IsNullOrEmpty(mac))
                    mac = await ResolveMacAsync(addr, ct);
                ct.ThrowIfCancellationRequested();
                discoveredHosts[ip] = (addr, mac);
            });
            await Task.WhenAll(macTasks);
            ct.ThrowIfCancellationRequested();

            // Drop next-hop / gateway MACs. ARP only resolves hosts on the local L2 segment; for an IP
            // reached over a router or a Forti/SSL VPN tunnel, SendARP returns the MAC of the next hop
            // (the VPN virtual adapter), so every remote host resolves to the SAME MAC - and its OUI
            // (e.g. Fortinet) would otherwise stamp every device with the wrong vendor and device type.
            // A real LAN gives each host a unique MAC, so a MAC shared across several IPs is an artifact:
            // blank it and let the port/fingerprint signals classify those hosts instead.
            var bogusMacs = new HashSet<string>(discoveredHosts.Values
                .Where(h => !string.IsNullOrEmpty(h.Mac))
                .GroupBy(h => h.Mac)
                .Where(g => g.Count() > MaxIpsPerMac)
                .Select(g => g.Key));
            if (bogusMacs.Count > 0)
            {
                foreach (var ip in discoveredHosts.Keys.ToList())
                {
                    var (hAddr, hMac) = discoveredHosts[ip];
                    // The gateway is the one host that really owns that MAC: off-link hosts
                    // resolve to it, so it keeps its own and the rest lose the borrowed copy.
                    if (string.IsNullOrEmpty(hMac) || !bogusMacs.Contains(hMac) || ip == GatewayIp) continue;
                    // A host the ARP sweep added had no other evidence than that shared answer
                    // (a proxy-ARP router or a TAP adapter answering for every address): not a device.
                    if (sweptOnly.Contains(ip)) discoveredHosts.TryRemove(ip, out _);
                    else discoveredHosts[ip] = (hAddr, "");
                }
            }

            // Phase 2: Probe discovered hosts for details (parallel, throttled)
            var sortedHosts = discoveredHosts.Values
                // Enumerable.Reverse SPELLED OUT, not h.Addr.GetAddressBytes().Reverse().
                // System.Text.Json 10 pulls System.Memory into this net48 project, which brings
                // MemoryExtensions.Reverse<T>(this Span<T>) into scope. A byte[] converts
                // implicitly to Span<byte>, so that overload WINS resolution over LINQ's - and it
                // reverses in place and returns void, so the spread below fails to compile
                // (CS9212). Naming Enumerable pins it to the overload this actually wants.
                .OrderBy(h => BitConverter.ToUInt32([.. Enumerable.Reverse(h.Addr.GetAddressBytes())], 0))
                .ToList();
            completed = 0;
            total = sortedHosts.Count;

            // Collect the mDNS/SSDP results before probing (best-effort; empty maps on failure).
            try { _mdns = await mdnsTask; } catch { }
            try { _ssdp = await ssdpTask; } catch { }

            if (fullScan)
            {
                StatusChanged?.Invoke(new ScanStatus(ScanStage.Probing, total));
                var probeSemaphore = new SemaphoreSlim(20);
                var probeTasks = sortedHosts.Select(async entry =>
                {
                    await probeSemaphore.WaitAsync(ct);
                    try
                    {
                        var device = await ProbeHostAsync(entry.Addr, entry.Mac, ct, options);
                        ct.ThrowIfCancellationRequested();
                        ReportDeviceFound(device);
                        int done = Interlocked.Increment(ref completed);
                        ProgressChanged?.Invoke(40 + (int)(done * 60.0 / total));
                        return device;
                    }
                    finally { probeSemaphore.Release(); }
                });

                var probeResults = await Task.WhenAll(probeTasks);
                devices.AddRange(probeResults.OrderBy(d => d.IpSortKey));
            }
            else
            {
                // Quick scan: resolve hostname and vendor in parallel, no port scan
                StatusChanged?.Invoke(new ScanStatus(ScanStage.ResolvingHosts, total));
                var quickSemaphore = new SemaphoreSlim(20);
                var quickTasks = sortedHosts.Select(async entry =>
                {
                    await quickSemaphore.WaitAsync(ct);
                    try
                    {
                        var device = new NetworkDevice
                        {
                            IpAddress = entry.Addr.ToString(),
                            MacAddress = entry.Mac,
                            Hostname = options.ReverseDns
                                ? await AwaitWithCancellation(ResolveVerifiedHostnameAsync(entry.Addr), ct) : string.Empty
                        };

                        if (!string.IsNullOrEmpty(entry.Mac))
                            device.Vendor = ResolveVendor(entry.Mac);
                        // The multicast replies were already collected above, so a quick scan
                        // uses them too: .local names and service types cost nothing more here.
                        if (options.Mdns && _mdns.TryGetValue(device.IpAddress, out var quickMd))
                        {
                            device.MdnsServices = DeviceText.Services(quickMd.Services);
                            if (string.IsNullOrEmpty(device.Hostname) && !string.IsNullOrEmpty(quickMd.Name))
                                device.Hostname = quickMd.Name;
                        }
                        if (options.Ssdp && _ssdp.TryGetValue(device.IpAddress, out var quickSrv))
                            device.SsdpServer = quickSrv;

                        ct.ThrowIfCancellationRequested();

                        // Classify even in quick scan (hostname + OUI, no ports)
                        Finish(device);

                        ReportDeviceFound(device);
                        int done = Interlocked.Increment(ref completed);
                        ProgressChanged?.Invoke(40 + (int)(done * 60.0 / total));
                        return device;
                    }
                    finally { quickSemaphore.Release(); }
                });

                var quickResults = await Task.WhenAll(quickTasks);
                devices.AddRange(quickResults.OrderBy(d => d.IpSortKey));
            }

            StatusChanged?.Invoke(new ScanStatus(ScanStage.Complete, devices.Count));
            ProgressChanged?.Invoke(100);
            return devices;
        }

        /// <summary>The neighbor (ARP) cache from the platform; a bound run reads only its own adapter's.</summary>
        private static IReadOnlyDictionary<string, string> GetArpCache() =>
            ScanSource.Current?.Neighbors() ?? NetworkPlatform.Current.ReadNeighborCache();

        /// <summary>
        /// Probe a single host for hostname, open ports, fingerprints, and device type.
        /// </summary>
        // Windows name lookups cannot be canceled and some never answer. Each lookup gets a time
        // budget, after which the device simply has no name and the scan moves on. A lookup that
        // is still running keeps its slot until it really returns, so a network full of silent
        // resolvers can tie up at most this many threads; beyond that, devices go unnamed.
        private static readonly TimeSpan HostnameBudget = TimeSpan.FromSeconds(3);
        internal const int MaxPendingHostnameLookups = 64;
        private static readonly SemaphoreSlim HostnameSlots = new(MaxPendingHostnameLookups, MaxPendingHostnameLookups);

        /// <summary>Replaces the reverse-then-forward lookup in tests.</summary>
        internal static Func<IPAddress, Task<string>>? HostnameResolverOverride { get; set; }

        private static async Task<string> ResolveVerifiedHostnameAsync(IPAddress addr)
        {
            var deadline = Task.Delay(HostnameBudget);
            var slot = HostnameSlots.WaitAsync();
            if (await Task.WhenAny(slot, deadline) != slot)
            {
                // Release a slot this abandoned wait may still be granted later.
                _ = slot.ContinueWith(_ => HostnameSlots.Release(), TaskScheduler.Default);
                return string.Empty;
            }
            var lookup = LookupHostnameAsync(addr);
            _ = lookup.ContinueWith(_ => HostnameSlots.Release(), TaskScheduler.Default);
            return await Task.WhenAny(lookup, deadline) == lookup ? await lookup : string.Empty;
        }

        private static async Task<string> LookupHostnameAsync(IPAddress addr)
        {
            try
            {
                if (HostnameResolverOverride is { } resolve) return await resolve(addr) ?? string.Empty;
                var reverse = await Dns.GetHostEntryAsync(addr);
                string hostname = reverse.HostName?.Trim() ?? string.Empty;
                if (hostname.Length == 0 || string.Equals(
                        hostname, addr.ToString(), StringComparison.OrdinalIgnoreCase))
                    return string.Empty;

                var forward = await Dns.GetHostAddressesAsync(hostname);
                return forward.Any(candidate => candidate.Equals(addr)) ? hostname : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static Task<string> ResolveMacAsync(IPAddress address, CancellationToken ct) =>
            ResolveMacAsync(address, NativeMacSlots, ct);

        private static async Task<string> ResolveMacAsync(IPAddress address, SemaphoreSlim slots, CancellationToken ct, bool dedicated = false) =>
            (await ResolveMacTimedAsync(address, slots, ct, dedicated)).Mac;

        /// <summary>The MAC and how long the native call itself took, not counting the wait for a slot.</summary>
        private static async Task<(string Mac, TimeSpan Took)> ResolveMacTimedAsync(IPAddress address, SemaphoreSlim slots, CancellationToken ct, bool dedicated = false)
        {
            await slots.WaitAsync(ct);
            // A started native call owns its slot until it actually returns, even when the
            // caller has canceled and a later scan is waiting for the same capacity.
            Func<(string, TimeSpan)> work = () =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var clock = Stopwatch.StartNew();
                    // Off-link, SendARP answers with the next hop's MAC, which belongs to the router.
                    string mac = IsOnLink(address) ? GetMacAddress(address) : string.Empty;
                    if (mac.Length > 0 && IsOwnMacElsewhere(address, mac)) mac = string.Empty;
                    return (mac, clock.Elapsed);
                }
                finally { slots.Release(); }
            };
            // The sweep's calls mostly wait out dead addresses; on their own threads they cannot
            // starve the pool the rest of the scan runs on.
            var lookup = dedicated
                ? Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                : Task.Run(work);
            return await AwaitWithCancellation(lookup, ct);
        }

        /// <summary>
        /// True when the OS reaches this address directly on a local segment. SendARP to an
        /// off-link address answers with the next hop's MAC, so only on-link addresses can be
        /// declared alive by ARP.
        /// </summary>
        private static bool IsOnLink(IPAddress address)
        {
            // A bound scan sends from its adapter, which reaches its own subnet directly even when
            // Windows' best route for that subnet goes elsewhere (a client VPN pushing the same range).
            if (ScanSource.Current is { } binding && binding.Contains(address)) return true;
            try { return NetworkPlatform.Current.BestRoute(address) is { } route && route.NextHop.Length == 0; }
            catch { return false; }
        }

        private static async Task AwaitWithCancellation(Task task, CancellationToken token)
        {
            if (task.IsCompleted)
            {
                await task;
                token.ThrowIfCancellationRequested();
                return;
            }

            if (await Task.WhenAny(task, Task.Delay(Timeout.Infinite, token)) != task)
            {
                _ = task.ObserveFault();
                token.ThrowIfCancellationRequested();
            }

            await task;
            token.ThrowIfCancellationRequested();
        }

        private static async Task<T> AwaitWithCancellation<T>(Task<T> task, CancellationToken token)
        {
            await AwaitWithCancellation((Task)task, token);
            return await task;
        }

        private async Task<NetworkDevice> ProbeHostAsync(
            IPAddress addr, string cachedMac, CancellationToken token, ScanOptions options)
        {
            token.ThrowIfCancellationRequested();
            var device = new NetworkDevice
            {
                IpAddress = addr.ToString(),
                MacAddress = cachedMac
            };

            // Resolve hostname + capture TTL (OS family hint) in parallel with port scan.
            var dnsTask = options.ReverseDns ? Task.Run(async () =>
            {
                device.Hostname = await ResolveVerifiedHostnameAsync(addr);
            }) : Task.CompletedTask;

            var ttlTask = options.Icmp ? Task.Run(async () =>
            {
                try
                {
                    var reply = await ScanSource.PingAsync(addr, 400);
                    if (reply.Success && reply.Ttl > 0) device.Ttl = reply.Ttl;
                }
                catch { }
            }) : Task.CompletedTask;

            // Port scan (parallel, short timeout)
            var portTasks = (options.Ports ? ProbePorts : Array.Empty<int>()).Select(async port =>
            {
                try
                {
                    using var client = ScanSource.Tcp();
                    var connectTask = client.ConnectAsync(addr, port).ObserveFault();
                    if (await Task.WhenAny(connectTask, Task.Delay(200, token)) == connectTask
                        && client.Connected)
                    {
                        return port;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                return -1;
            });

            var results = await AwaitWithCancellation(Task.WhenAll(portTasks), token);
            device.OpenPorts = [.. results.Where(p => p > 0).OrderBy(p => p)];

            // Wait for hostname + TTL probes to finish before fingerprinting.
            await AwaitWithCancellation(Task.WhenAll(dnsTask, ttlTask), token);

            // Look up vendor from MAC OUI (used by later probes + classifier).
            if (!string.IsNullOrEmpty(device.MacAddress))
                device.Vendor = ResolveVendor(device.MacAddress);

            // Attach network-wide discovery results + name fallback (reverse DNS -> mDNS .local name).
            if (options.Mdns && _mdns.TryGetValue(device.IpAddress, out var md))
            {
                device.MdnsServices = DeviceText.Services(md.Services);
                if (string.IsNullOrEmpty(device.Hostname) && !string.IsNullOrEmpty(md.Name))
                    device.Hostname = md.Name;
            }
            if (options.Ssdp && _ssdp.TryGetValue(device.IpAddress, out var srv))
                device.SsdpServer = srv;

            // -- Fingerprint probes: run in parallel, each is gated on relevant open ports --
            var fpTasks = new List<Task>();
            if (options.Http && device.OpenPorts.Any(p => p == 80 || p == 443 || p == 8006 || p == 8080 || p == 8443 || p == 5000 || p == 5001 || p == 8123))
                fpTasks.Add(ProbeHttpAsync(device, addr, ct: token));
            if (options.Ssh && device.OpenPorts.Contains(22))
                fpTasks.Add(ProbeSshBannerAsync(device, addr));
            if (options.Tls && HasStandardTlsPort(device))
                fpTasks.Add(ProbeTlsCertAsync(device, addr, ct: token));
            // NetBIOS + SNMP are UDP -- we probe regardless of TCP state (ports 137/161 UDP).
            if (options.Netbios) fpTasks.Add(ProbeNetbiosAsync(device, addr));
            if (options.Snmp) fpTasks.Add(ProbeSnmpAsync(device, addr));

            await AwaitWithCancellation(Task.WhenAll(fpTasks), token);

            // Last-resort name: NetBIOS computer name when reverse DNS and mDNS both came up empty.
            if (string.IsNullOrEmpty(device.Hostname) && !string.IsNullOrEmpty(device.NetbiosName))
                device.Hostname = device.NetbiosName;

            // Classify device type using weighted scoring over all signals.
            Finish(device);

            return device;
        }

        // -------------------------------------------------------------------
        // Deep single-host rescan (right-click "Rescan"): far more thorough than
        // the subnet sweep's per-host probe. Every well-known port 1-1024 plus the
        // curated high service ports, longer connect timeouts with a retry, fresh
        // MAC/hostname/TTL, then the full fingerprint + classify pass. Slow (seconds
        // per host) on purpose - it targets one host on demand, not a whole /24.
        // Reuses the last full scan's multicast (mDNS/SSDP) results for naming.
        // -------------------------------------------------------------------
        public virtual Task<NetworkDevice> DeepProbeHostAsync(
            string ip, CancellationToken ct, int portConcurrency = 256,
            bool flushLocalDnsCache = true) =>
            DeepProbeHostAsync(ip, ct, new ScanOptions(), portConcurrency, flushLocalDnsCache);

        public virtual async Task<NetworkDevice> DeepProbeHostAsync(
            string ip, CancellationToken ct, ScanOptions options, int portConcurrency = 256,
            bool flushLocalDnsCache = true)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (portConcurrency < 1)
                throw new ArgumentOutOfRangeException(nameof(portConcurrency), portConcurrency, "Port concurrency must be at least 1.");
            if (ip == null || !IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
                throw new ArgumentException($"\"{ip}\" is not an IPv4 address.", nameof(ip));
            // IPAddress.TryParse also takes shorthand such as "192.168.1" or a bare number; the record
            // and every lookup use the address that is actually probed.
            ip = addr.ToString();
            BeginRun([addr]);
            ct.ThrowIfCancellationRequested();
            if (flushLocalDnsCache && options.ReverseDns) FlushLocalDnsCache();
            var device = new NetworkDevice
            {
                IpAddress  = ip,
                // Fresh MAC via ARP.
                MacAddress = options.Arp ? await ResolveMacAsync(addr, ct) : string.Empty
            };

            // Hostname + TTL alongside the port sweep.
            var dnsTask = options.ReverseDns ? Task.Run(async () =>
            {
                device.Hostname = await ResolveVerifiedHostnameAsync(addr);
            }, ct) : Task.CompletedTask;
            var ttlTask = options.Icmp ? Task.Run(async () =>
            {
                try
                {
                    var reply = await ScanSource.PingAsync(addr, 800);
                    if (reply.Success && reply.Ttl > 0) device.Ttl = reply.Ttl;
                }
                catch { }
            }, ct) : Task.CompletedTask;

            // Exhaustive TCP sweep. Bounded concurrency keeps the open-socket count sane;
            // each port gets a generous timeout and one retry so slow/loaded hosts still answer.
            using var gate = new SemaphoreSlim(portConcurrency);
            var portTasks = (options.Ports ? DeepPortsOverride ?? DeepPortList : Array.Empty<int>()).Select(async port =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (await TryConnectAsync(addr, port, 700)) return port;
                    if (await TryConnectAsync(addr, port, 900)) return port;   // retry catches slow responders
                    return -1;
                }
                finally { gate.Release(); }
            });
            var results = await Task.WhenAll(portTasks);
            ct.ThrowIfCancellationRequested();
            device.OpenPorts = [.. results.Where(p => p > 0).OrderBy(p => p)];

            await AwaitWithCancellation(Task.WhenAll(dnsTask, ttlTask), ct);

            if (!string.IsNullOrEmpty(device.MacAddress))
                device.Vendor = ResolveVendor(device.MacAddress);

            // Reuse the last full scan's network-wide multicast results for naming.
            if (options.Mdns && _mdns.TryGetValue(ip, out var md))
            {
                device.MdnsServices = DeviceText.Services(md.Services);
                if (string.IsNullOrEmpty(device.Hostname) && !string.IsNullOrEmpty(md.Name))
                    device.Hostname = md.Name;
            }
            if (options.Ssdp && _ssdp.TryGetValue(ip, out var srv))
                device.SsdpServer = srv;

            // Full fingerprint pass. Unknown open ports stay connect-only unless the caller opts in
            // to widening HTTP/TLS to them; raw services are skipped either way.
            var fpTasks = new List<Task>();
            if (options.Netbios) fpTasks.Add(ProbeNetbiosAsync(device, addr));
            if (options.Snmp) fpTasks.Add(ProbeSnmpAsync(device, addr));
            if (device.OpenPorts.Count > 0)
            {
                bool wide = options.ProbeUnknownPorts;
                // A deep pass checks every web port, so each one has its own access evidence.
                if (options.Http) fpTasks.Add(ProbeHttpAsync(device, addr, deep: wide, everyPort: true, ct));
                if (options.Tls) fpTasks.Add(ProbeTlsCertAsync(device, addr, deep: wide, ct: ct));
            }
            if (options.Ssh && device.OpenPorts.Contains(22))
                fpTasks.Add(ProbeSshBannerAsync(device, addr));
            await AwaitWithCancellation(Task.WhenAll(fpTasks), ct);
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(device.Hostname) && !string.IsNullOrEmpty(device.NetbiosName))
                device.Hostname = device.NetbiosName;

            Finish(device);
            return device;
        }

        // Ports probed by the deep rescan: all well-known ports 1-1024 plus the curated
        // high service ports (Proxmox 8006, Plex 32400, ...). Built once at first use.
        private static readonly int[] DeepPortList = BuildDeepPortList();
        private static int[] BuildDeepPortList()
        {
            var set = new HashSet<int>();
            for (int p = 1; p <= 1024; p++) set.Add(p);
            foreach (var p in ProbePorts) set.Add(p);
            return [.. set.OrderBy(p => p)];
        }

        // Single TCP connect attempt with a timeout. True if the port accepts the connection.
        private static async Task<bool> TryConnectAsync(IPAddress addr, int port, int timeoutMs)
        {
            try
            {
                using var client = ScanSource.Tcp();
                var connect = client.ConnectAsync(addr, port).ObserveFault();
                if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) == connect && client.Connected)
                    return true;
            }
            catch { }
            return false;
        }

        // -------------------------------------------------------------------
        // Hostname keyword rules: checked BEFORE OUI/port classification.
        // Each entry maps a hostname substring (lowercase) to a device type.
        // -------------------------------------------------------------------
        private static readonly (string Pattern, string Type)[] HostnameKeywords =
        [
            ("iphone",        "iPhone"),
            ("ipad",          "iPhone"),
            ("android",       "Android"),
            ("lgwebos",       "Smart TV"),
            ("webostv",       "Smart TV"),
            ("lgtv",          "Smart TV"),
            ("roku",          "Smart TV"),
            ("firetv",        "Smart TV"),
            ("fire-tv",       "Smart TV"),
            ("appletv",       "Apple TV"),
            ("apple-tv",      "Apple TV"),
            ("chromecast",    "Smart TV"),
            ("smarttv",       "Smart TV"),
            ("tizen",         "Smart TV"),
            ("wiim",          "Media Streamer"),
            ("linkplay",      "Media Streamer"),
            ("sonos",         "Media Streamer"),
            ("heos",          "Media Streamer"),
            ("homeassistant", "Home Assistant"),
            ("home-assistant","Home Assistant"),
            ("pihole",        "DNS Server"),
            ("pi-hole",       "DNS Server"),
            ("proxmox",       "Hypervisor"),
            ("esxi",          "Hypervisor"),
            ("unifi",         "Network"),
            ("ubnt",          "Network"),
            ("synology",      "NAS"),
            ("diskstation",   "NAS"),
            ("freenas",       "NAS"),
            ("truenas",       "NAS"),
        ];

        // -------------------------------------------------------------------
        // Known-bad OUI overrides: MAC prefixes where the IEEE OUI vendor
        // name is misleading (e.g. Wiim uses Linkplay chips registered to
        // Apple). Key = first 8 chars of MAC (XX:XX:XX), value = corrected
        // vendor string used for classification (not displayed to user).
        // -------------------------------------------------------------------
        private static readonly Dictionary<string, string> OuiBadMap = new(StringComparer.OrdinalIgnoreCase)
        {
            // Linkplay / Wiim devices registered under Apple OUI blocks
            { "C4:F7:C1", "Linkplay" },
            { "58:CF:79", "Linkplay" },
        };

        // -------------------------------------------------------------------
        // Brand overrides by MAC prefix for /24 blocks IEEE lists as "Private"
        // or leaves unnamed. Unlike OuiBadMap (classification only), these
        // REPLACE the displayed vendor too. Key = first 8 chars (XX:XX:XX).
        // -------------------------------------------------------------------
        private static readonly Dictionary<string, string> VendorPrefixOverrides = new(StringComparer.OrdinalIgnoreCase)
        {
            // Govee smart lights + air purifier - registered "Private" / unlisted in the IEEE data
            { "D0:C9:07", "Govee" },
            { "98:17:3C", "Govee" },
            { "60:74:F4", "Govee" },
            { "7C:A6:B0", "Govee" },
            { "E0:72:A1", "Govee" },
        };

        /// <summary>The stored Vendor value for a locally-administered (randomized) MAC. Public so
        /// the display converter can recognise it without a second copy of the literal.</summary>
        public const string VendorRandomized = "(Randomized)";

        /// <summary>OUI vendor lookup with brand-prefix overrides applied (used for display + classification).</summary>
        private static string ResolveVendor(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return "";

            // Brand overrides for real OUI blocks IEEE lists as "Private"/unnamed.
            if (mac.Length >= 8 && VendorPrefixOverrides.TryGetValue(mac[..8].ToUpperInvariant(), out var brand))
                return brand;

            // Locally-administered (randomized/private) MAC: the first octet's bit 1 is set, so the
            // prefix is NOT a real manufacturer assignment. Label it instead of guessing a vendor.
            // iOS "Private Wi-Fi Address" and Android MAC randomization both use these.
            if (byte.TryParse(mac[..2], System.Globalization.NumberStyles.HexNumber, null, out var b0)
                && (b0 & 0x02) != 0)
                // ENGLISH, like every other stored value. This lands in NetworkDevice.Vendor,
                // which is written to CSV, used by the HTML report and matched by the CLI's
                // /vendor-filter - so translating it here would make a scan saved in German
                // filter differently from one saved in English. VendorConverter turns it into the
                // reader's language on the way to the screen, the same split device types use.
                return VendorRandomized;

            return OuiLookup.GetVendor(mac);
        }

        /// <summary>The host's default-gateway IP, set from the detected network info before a scan.
        /// The device at this IP is the router, so it's labeled Router (or Router/DNS if it IS the DNS server).</summary>
        public static string GatewayIp = "";

        /// <summary>The host's configured DNS server IP. Only this device is the actual DNS server, so a
        /// router that merely has port 53 open (forwarding) isn't mislabeled when DNS lives elsewhere (Pi-hole).</summary>
        public static string DnsIp = "";

        /// <summary>
        /// Weighted-score classifier. Each signal contributes points to candidate
        /// device types; the highest-scoring type above the threshold wins.
        /// A manual type and the default gateway role decide before scoring; a hostname
        /// keyword is a heavy hint that clearer protocol evidence can still outvote.
        ///
        /// Signal strength rough guide:
        ///   15+  strong positive (protocol banner, TLS cert, specific HTTP title)
        ///   8-12 vendor + port combo, unique signature (e.g. port 8006 = Proxmox)
        ///   4-7  weak supporting signal (TTL, generic port presence)
        ///   1-3  tie-breaker only
        /// </summary>
        public static string ClassifyDevice(NetworkDevice device) => ClassifyDevice(device, ManualTypeLookup);

        /// <summary>Classifies with the given manual types instead of <see cref="ManualTypeLookup"/>.</summary>
        public static string ClassifyDevice(NetworkDevice device, Func<string, string?>? manualTypes) =>
            AssessDevice(device, manualTypes).Type;

        /// <summary>
        /// The type, the signals behind it, close alternatives and a confidence grade. Reads only
        /// the fields already on <paramref name="device"/>: it sends nothing and changes nothing.
        /// </summary>
        public static DeviceRecognition AssessDevice(NetworkDevice device, Func<string, string?>? manualTypes) =>
            AssessDevice(device, manualTypes, GatewayIp, DnsIp);

        /// <summary>As above, with the gateway and DNS server of the network the device was scanned on.</summary>
        public static DeviceRecognition AssessDevice(NetworkDevice device, Func<string, string?>? manualTypes, string gatewayIp, string dnsIp)
        {
            var automatic = AssessAutomatic(device, gatewayIp ?? string.Empty, dnsIp ?? string.Empty);
            var manual = manualTypes?.Invoke(device.MacAddress);
            if (manual != null)
            {
                automatic.Type = manual;
                automatic.Confidence = RecognitionConfidence.Manual;
            }
            return automatic;
        }

        /// <summary>Types that are refinements of each other, so one scoring close to another is not a conflict.</summary>
        private static readonly string[][] RelatedTypes =
        [
            ["Windows", "Windows Server"],
            ["Network", "Router", "Switch/AP"],
            ["iPhone", "Apple Device", "Apple TV"],
            ["Smart TV", "Media Streamer", "Apple TV", "Apple Device"],
        ];

        private static bool Related(string a, string b) =>
            a == b || RelatedTypes.Any(group => group.Contains(a) && group.Contains(b));

        // Observed values can be long or hostile; evidence keeps a bounded, single-line copy.
        private static string Clip(string value)
        {
            var clean = new string([.. value.Select(c => char.IsControl(c) ? ' ' : c)]).Trim();
            return clean.Length <= 80 ? clean : clean[..80];
        }

        private static string PortList(params int[] ports) => string.Join(", ", ports);

        private static DeviceRecognition AssessAutomatic(NetworkDevice device, string gatewayIp, string dnsIp)
        {
            var ports = device.OpenPorts;
            string host = device.Hostname.ToLowerInvariant();
            string title = device.HttpTitle.ToLowerInvariant();
            string server = device.HttpServer.ToLowerInvariant();
            string tls = device.TlsSubject.ToLowerInvariant();
            string ssh = device.SshBanner.ToLowerInvariant();
            string snmp = device.SnmpDescr.ToLowerInvariant();
            string nbName = device.NetbiosName.ToLowerInvariant();
            string ssdp = device.SsdpServer.ToLowerInvariant();

            DeviceRecognition Decided(string type, RecognitionConfidence confidence, string source, string detail) => new()
            {
                Type = type,
                Confidence = confidence,
                AutomaticType = type,
                AutomaticConfidence = confidence,
                Signals = [new RecognitionSignal { Source = source, Detail = detail }],
            };

            // 1. The default gateway is the router. Surface that role (plus DNS when it serves it),
            //    ahead of the port heuristics that would otherwise label it just "DNS Server".
            //    Windows' own routing table says so, which is firmer than anything the device reports.
            if (!string.IsNullOrEmpty(gatewayIp) && device.IpAddress == gatewayIp)
            {
                // Only "Router/DNS" when the gateway IS the configured DNS server. A router that just
                // has port 53 open (forwarding) but points DNS at a Pi-hole stays plain "Router".
                bool isDnsServer = !string.IsNullOrEmpty(dnsIp) && device.IpAddress == dnsIp;
                return Decided(isDnsServer ? "Router/DNS" : "Router", RecognitionConfidence.High,
                    RecognitionSources.Gateway, isDnsServer ? "gateway-dns" : "gateway");
            }

            // 2. Score candidate types from all signals.
            var scores = new Dictionary<string, int>();
            var signals = new List<(string Type, RecognitionSignal Signal)>();
            void Add(string type, int s, string source, string detail)
            {
                scores[type] = scores.TryGetValue(type, out int total) ? total + s : s;
                signals.Add((type, new RecognitionSignal { Source = source, Detail = Clip(detail), Weight = s }));
            }

            // Hostname keywords are a heavy hint: a name someone chose usually says what the
            // device is, but a clear protocol answer that disagrees can still win.
            foreach (var (pattern, type) in HostnameKeywords)
            {
                if (host.Contains(pattern))
                {
                    Add(type, 20, RecognitionSources.Hostname, device.Hostname);
                    break;
                }
            }

            // OUI vendor with known-bad corrections.
            string vendor = device.Vendor.ToLowerInvariant();
            string vendorShown = device.Vendor;
            if (!string.IsNullOrEmpty(device.MacAddress) && device.MacAddress.Length >= 8)
            {
                string prefix = device.MacAddress[..8].ToUpperInvariant();
                if (OuiBadMap.TryGetValue(prefix, out var corrected))
                {
                    vendor = corrected.ToLowerInvariant();
                    vendorShown = corrected;
                }
            }

            bool hasWorkstationPorts = ports.Contains(3389) || ports.Contains(445);
            bool hasPrintProtocol = ports.Contains(9100) || ports.Contains(515) || ports.Contains(631);
            bool hasActiveDirectoryPorts = ports.Contains(88) && ports.Contains(389) && ports.Contains(445);
            const string P = RecognitionSources.Ports, V = RecognitionSources.Vendor, H = RecognitionSources.Http,
                T = RecognitionSources.Tls, S = RecognitionSources.Ssh, N = RecognitionSources.Snmp;
            string Open(params int[] wanted) => PortList([.. wanted.Where(ports.Contains)]);

            // -- Hypervisor --
            if (ports.Contains(8006)) Add("Hypervisor", 15, P, "8006");
            if (title.Contains("proxmox")) Add("Hypervisor", 15, H, device.HttpTitle);
            if (tls.Contains("vmware")) Add("Hypervisor", 15, T, device.TlsSubject);
            else if (title.Contains("vmware esxi")) Add("Hypervisor", 15, H, device.HttpTitle);
            if (ports.Contains(902) && ports.Contains(443) && !hasWorkstationPorts) Add("Hypervisor", 10, P, "902, 443");
            if (ports.Contains(2179) && (ports.Contains(5985) || ports.Contains(5986)) && !hasWorkstationPorts) Add("Hypervisor", 8, P, Open(2179, 5985, 5986));
            if (tls.Contains("xenserver")) Add("Hypervisor", 15, T, device.TlsSubject);
            else if (title.Contains("xenserver")) Add("Hypervisor", 15, H, device.HttpTitle);

            // -- Windows workstation --
            if (ports.Contains(3389) && ports.Contains(445)) Add("Windows", 6, P, "3389, 445");
            if (server.Contains("microsoft-iis")) Add("Windows", 6, H, device.HttpServer);
            if (!string.IsNullOrEmpty(nbName) && ports.Contains(445)) Add("Windows", 4, RecognitionSources.NetBios, device.NetbiosName);
            if (device.Ttl is >= 120 and <= 128 && ports.Contains(445)) Add("Windows", 3, RecognitionSources.Ttl, device.Ttl.ToString()!);

            // -- Windows Server --
            if (ports.Contains(3389) && ports.Contains(445) && (ports.Contains(80) || ports.Contains(443) || ports.Contains(53)))
                Add("Windows Server", 6, P, Open(3389, 445, 80, 443, 53));
            if (hasActiveDirectoryPorts) Add("Windows Server", 20, P, "88, 389, 445");
            if (ports.Contains(53) && ports.Contains(88) && ports.Contains(389)) Add("Windows Server", 16, P, "53, 88, 389");
            if (ports.Contains(445) && (ports.Contains(636) || ports.Contains(3268) || ports.Contains(3269)))
                Add("Windows Server", 14, P, Open(445, 636, 3268, 3269));
            if (ports.Contains(135) && ports.Contains(445) && ports.Contains(3389)) Add("Windows Server", 8, P, "135, 445, 3389");
            if (title.Contains("exchange")) Add("Windows Server", 10, H, device.HttpTitle);
            else if (server.Contains("exchange")) Add("Windows Server", 10, H, device.HttpServer);
            if (snmp.Contains("windows server")) Add("Windows Server", 15, N, device.SnmpDescr);

            // -- Linux / SSH --
            // Windows boxes with OpenSSH Server advertise "OpenSSH_for_Windows" -- score Windows, not Linux.
            bool sshIsWindows = ssh.Contains("for_windows");
            if (sshIsWindows) Add("Windows", 15, S, device.SshBanner);
            if (!sshIsWindows && (ssh.StartsWith("ssh-2.0-openssh") || ssh.StartsWith("ssh-1.99-openssh"))) Add("Linux/SSH", 8, S, device.SshBanner);
            if (device.Ttl is >= 60 and <= 64 && ports.Contains(22) && !sshIsWindows) Add("Linux/SSH", 3, RecognitionSources.Ttl, device.Ttl.ToString()!);

            // -- Network gear (switches, routers, firewalls, APs) --
            bool isNetworkVendor = vendor.Contains("cisco") || vendor.Contains("ubiquiti")
                || vendor.Contains("aruba") || vendor.Contains("ruckus") || vendor.Contains("meraki")
                || vendor.Contains("netgear") || vendor.Contains("tp-link") || vendor.Contains("fortinet")
                || vendor.Contains("juniper") || vendor.Contains("mikrotik") || vendor.Contains("gl technologies")
                || vendor.Contains("gl.inet") || vendor.Contains("draytek") || vendor.Contains("zyxel")
                || vendor.Contains("linksys") || vendor.Contains("sonicwall") || vendor.Contains("watchguard");
            if (isNetworkVendor) Add("Network", 8, V, vendorShown);
            if (ssh.Contains("cisco") || ssh.Contains("routeros") || ssh.Contains("mikrotik")) Add("Network", 15, S, device.SshBanner);
            if (title.Contains("unifi") || title.Contains("fortigate") || title.Contains("sonicwall")
                || title.Contains("pfsense") || title.Contains("opnsense") || title.Contains("mikrotik")) Add("Network", 15, H, device.HttpTitle);
            if (snmp.Contains("cisco ios") || snmp.Contains("juniper") || snmp.Contains("fortigate")) Add("Network", 12, N, device.SnmpDescr);

            // Router vs Switch/AP disambiguation within network gear.
            if (isNetworkVendor && ports.Contains(53)) Add("Router", 10, P, "53");
            if (isNetworkVendor && ports.Contains(161) && !ports.Contains(53)) Add("Switch/AP", 8, P, "161");

            // -- Printer --
            bool isPrinterVendor = vendor.Contains("canon") || vendor.Contains("epson")
                || vendor.Contains("brother") || vendor.Contains("xerox") || vendor.Contains("lexmark")
                || vendor.Contains("ricoh") || vendor.Contains("konica") || vendor.Contains("kyocera");
            // HP vendor alone is ambiguous (laptops, servers, printers). Only trust with printer ports.
            if (ports.Contains(9100)) Add("Printer", 10, P, "9100");
            if (!hasActiveDirectoryPorts && (ports.Contains(515) || ports.Contains(631))) Add("Printer", 4, P, Open(515, 631));
            if (isPrinterVendor && hasPrintProtocol) Add("Printer", 12, V, vendorShown);
            if (isPrinterVendor && ports.Count <= 3) Add("Printer", 6, V, vendorShown);
            if (vendor.Contains("hewlett packard") && ports.Contains(9100)) Add("Printer", 12, V, vendorShown);
            if (snmp.Contains("laserjet") || snmp.Contains("officejet") || snmp.Contains("printer")) Add("Printer", 15, N, device.SnmpDescr);
            if (title.Contains("embedded web server") || title.Contains("web image monitor")) Add("Printer", 10, H, device.HttpTitle);

            // -- NAS --
            if (vendor.Contains("synology") || vendor.Contains("qnap") || vendor.Contains("asustor")
                || vendor.Contains("drobo") || vendor.Contains("buffalo") || vendor.Contains("terramaster"))
                Add("NAS", 12, V, vendorShown);
            if (title.Contains("diskstation") || title.Contains("synology") || title.Contains("dsm "))
                Add("NAS", 15, H, device.HttpTitle);
            if (title.Contains("qts") || title.Contains("qnap") || title.Contains("truenas") || title.Contains("freenas"))
                Add("NAS", 15, H, device.HttpTitle);
            if (ports.Contains(548)) Add("NAS", 4, P, "548");

            // -- Apple iDevice (iPhone / iPad) --
            bool isApple = vendor.Contains("apple");
            // Port 62078 is the Apple iDevice sync/tether port -- exclusively iPhones and iPads.
            // Score regardless of OUI so randomized-MAC iDevices are still caught.
            if (ports.Contains(62078)) Add("iPhone", 15, P, "62078");
            if (isApple && ports.Contains(62078)) Add("iPhone", 5, V, vendorShown);  // OUI bonus
            if (isApple && ports.Count <= 2) Add("iPhone", 6, V, vendorShown);
            string? FirstSvc(params string[] wanted) => device.MdnsServices.FirstOrDefault(s => wanted.Any(s.Contains));
            if (FirstSvc("_airplay", "_raop", "_airport") is { } appleSvc)
                Add("Apple Device", 10, RecognitionSources.Mdns, appleSvc);

            // -- Android / Mobile --
            bool isMobileVendor = vendor.Contains("samsung") || vendor.Contains("oneplus")
                || vendor.Contains("xiaomi") || vendor.Contains("huawei") || vendor.Contains("motorola")
                || vendor.Contains("oppo") || vendor.Contains("vivo") || vendor.Contains("zte")
                || vendor.Contains("lg electronics") || vendor.Contains("google")
                || vendor.Contains("bbk electronics") || vendor.Contains("realme")
                || vendor.Contains("nothing technology") || vendor.Contains("fairphone");
            if (isMobileVendor && ports.Count <= 3) Add("Android", 8, V, vendorShown);

            // -- Surveillance camera --
            if (vendor.Contains("hikvision") || vendor.Contains("dahua") || vendor.Contains("axis")
                || vendor.Contains("amcrest") || vendor.Contains("reolink") || vendor.Contains("foscam"))
                Add("Camera", 12, V, vendorShown);
            if (title.Contains("hikvision") || title.Contains("dahua") || title.Contains("camera")
                || title.Contains("dvr") || title.Contains("nvr") || title.Contains("ipcam"))
                Add("Camera", 12, H, device.HttpTitle);
            if (ports.Contains(554)) Add("Camera", 4, P, "554");

            // -- IoT / smart home --
            if (vendor.Contains("espressif") || vendor.Contains("tuya") || vendor.Contains("sonoff")
                || vendor.Contains("shelly") || vendor.Contains("nest") || vendor.Contains("ecobee")
                || vendor.Contains("signify") || vendor.Contains("lutron") || vendor.Contains("wemo")
                || vendor.Contains("wyze") || vendor.Contains("aqara") || vendor.Contains("linkplay")
                || vendor.Contains("wiim") || vendor.Contains("govee"))
                Add("IoT", 10, V, vendorShown);

            // -- Home Assistant --
            if (ports.Contains(8123)) Add("Home Assistant", 12, P, "8123");
            if (title.Contains("home assistant")) Add("Home Assistant", 15, H, device.HttpTitle);

            // -- DNS server (Pi-hole, AdGuard, BIND) --
            if (title.Contains("pi-hole") || title.Contains("pihole")) Add("DNS Server", 15, H, device.HttpTitle);
            if (title.Contains("adguard")) Add("DNS Server", 15, H, device.HttpTitle);
            if (ports.Contains(53) && ports.Contains(80) && !isNetworkVendor) Add("DNS Server", 6, P, "53, 80");

            // -- mDNS (Bonjour) service types: the strongest, most specific signals --
            const string M = RecognitionSources.Mdns;
            if (FirstSvc("_googlecast") is { } cast) Add("Smart TV", 14, M, cast);
            if (FirstSvc("_printer", "_ipp", "_pdl-datastream") is { } print) Add("Printer", 14, M, print);
            if (FirstSvc("_sonos", "_spotify-connect") is { } speaker) Add("Media Streamer", 12, M, speaker);
            if (FirstSvc("_airplay", "_raop") is { } airplay) Add("Media Streamer", 8, M, airplay);
            if (FirstSvc("_hap", "_homekit", "_hue") is { } home) Add("IoT", 10, M, home);

            // -- SSDP / UPnP SERVER string --
            const string U = RecognitionSources.Ssdp;
            if (ssdp.Contains("roku")) Add("Smart TV", 12, U, device.SsdpServer);
            if (ssdp.Contains("synology") || ssdp.Contains("qnap")) Add("NAS", 12, U, device.SsdpServer);
            if (ssdp.Contains("plex")) Add("Media Streamer", 12, U, device.SsdpServer);
            if (ssdp.Contains("sonos") || ssdp.Contains("dlna") || ssdp.Contains("mediaserver")) Add("Media Streamer", 8, U, device.SsdpServer);
            if (ssdp.Contains("samsung") && ssdp.Contains("tv")) Add("Smart TV", 10, U, device.SsdpServer);

            // -- New ports --
            if (ports.Contains(32400)) Add("Media Streamer", 12, P, "32400");          // Plex
            if (ports.Contains(1883) || ports.Contains(8883)) Add("IoT", 6, P, Open(1883, 8883)); // MQTT brokers

            // -- Generic web device (catch-all) --
            if (ports.Contains(80) || ports.Contains(443) || ports.Contains(8080)) Add("Web Device", 2, P, Open(80, 443, 8080));

            // Pick highest-scoring candidate above threshold. Ties go to the type scored first,
            // which is the order of the rules above, so the result does not depend on hashing.
            var ranked = scores.Select((pair, order) => (pair.Key, pair.Value, order))
                .OrderByDescending(entry => entry.Value).ThenBy(entry => entry.order).ToList();
            if (ranked.Count > 0 && ranked[0].Value >= 6)
            {
                var (winner, score, _) = ranked[0];
                // Generic "Network" gear with no Router/Switch disambiguation is most likely a switch/AP.
                string shown = winner == "Network" ? "Switch/AP" : winner;
                // Signals for a refinement of the winner support it too: the network-vendor hint
                // behind "Router" counts toward Network gear.
                var support = signals.Where(s => Related(s.Type == "Network" ? "Switch/AP" : s.Type, shown)).Select(s => s.Signal)
                    .OrderByDescending(s => s.Weight).ToList();
                var others = ranked.Skip(1).Where(entry => entry.Value >= 6 && entry.Key != "Web Device")
                    .Select(entry => new RecognitionCandidate { Type = entry.Key == "Network" ? "Switch/AP" : entry.Key, Score = entry.Value })
                    .Where(candidate => candidate.Type != shown).Take(3).ToList();
                bool conflict = others.Any(candidate => !Related(candidate.Type, shown) && candidate.Score * 3 >= score * 2);

                int protocols = support.Select(s => s.Source).Where(RecognitionSources.IsProtocol).Distinct().Count();
                var confidence = protocols >= 2 ? RecognitionConfidence.High
                    : protocols == 1 || score >= 15 ? RecognitionConfidence.Medium
                    : RecognitionConfidence.Low;
                // A close, unrelated rival takes one step off: the evidence disagrees with itself.
                if (conflict && confidence != RecognitionConfidence.Low) confidence--;

                return new DeviceRecognition
                {
                    Type = shown,
                    Confidence = confidence,
                    AutomaticType = shown,
                    AutomaticConfidence = confidence,
                    Signals = support,
                    Alternatives = others,
                    Conflict = conflict,
                };
            }

            // Nothing cleared the threshold: fall back to a broad guess, always Low.
            DeviceRecognition Guess(string type, string rule)
            {
                var guess = Decided(type, RecognitionConfidence.Low, RecognitionSources.Fallback, rule);
                guess.Signals.AddRange(signals.Select(s => s.Signal).OrderByDescending(s => s.Weight));
                return guess;
            }

            // Randomized / locally-administered MAC with no responsive ports is almost
            // always a phone or tablet using iOS/Android private Wi-Fi addressing.
            bool localAdminMac = false;
            if (!string.IsNullOrEmpty(device.MacAddress) && device.MacAddress.Length >= 2
                && byte.TryParse(device.MacAddress[..2], System.Globalization.NumberStyles.HexNumber, null, out var firstByte))
            {
                localAdminMac = (firstByte & 0x02) != 0;
            }
            if (localAdminMac && ports.Count <= 3) return Guess("Mobile", RecognitionRules.PrivateMacFewPorts);

            // Fallback heuristics when no candidate cleared threshold.
            if (ports.Contains(22)) return Guess("Linux/SSH", RecognitionRules.SshPort);
            if (ports.Contains(445) || ports.Contains(3389)) return Guess("Windows", RecognitionRules.WindowsPorts);
            if (ports.Contains(80) || ports.Contains(443)) return Guess("Web Device", RecognitionRules.WebPort);
            // Known PC/workstation makers with no open ports are idle/standby computers, not IoT.
            // (A business micro in modern standby still answers ARP but blocks every inbound TCP port.)
            bool isPcVendor = vendor.Contains("dell") || vendor.Contains("hewlett") || vendor.Contains("hp inc")
                || vendor.Contains("lenovo") || vendor.Contains("micro-star") || vendor.Contains("asustek")
                || vendor.Contains("asrock") || vendor.Contains("gigabyte") || vendor.Contains("giga-byte")
                || vendor.Contains("framework") || vendor.Contains("fujitsu") || vendor.Contains("acer")
                || vendor.Contains("clevo");
            if (ports.Count == 0 && isPcVendor) return Guess("Windows", RecognitionRules.PcVendorNoPorts);

            // Responds to ARP/ping but exposes zero TCP ports -- almost always a
            // smart bulb, plug, sensor, or other IoT endpoint. Even with a blank
            // OUI (Govee and similar use unregistered prefixes), IoT beats Unknown.
            if (ports.Count == 0 && !string.IsNullOrEmpty(device.MacAddress)) return Guess("IoT", RecognitionRules.NoPortsWithMac);
            if (ports.Count == 0) return Guess("Unknown", RecognitionRules.NoEvidence);
            return Guess("Other", RecognitionRules.OpenPortsOnly);
        }

        // ===================================================================
        // Active fingerprint probes. Each populates a field on NetworkDevice;
        // failures are swallowed -- missing data just means weaker scoring.
        // ===================================================================

        /// <summary>
        /// Fetch HTTP title + Server header from the first responsive web port.
        /// </summary>
        // Ports where a deep rescan should speak TLS rather than plain HTTP when it finds
        // a web service on a non-standard port.
        private static readonly int[] TlsLikelyPorts = [443, 8443, 8006, 902, 5001, 9443, 4443, 10000, 8834];

        // Services that act on whatever bytes arrive: JetDirect raw printing (9100-9103) and
        // LPD (515) print an HTTP GET or ClientHello as a page. Deep fingerprinting never sends
        // payloads to them, matching Nmap's default exclusion of 9100.
        private static readonly int[] RawServicePorts = [515, 9100, 9101, 9102, 9103];

        /// <summary>True when a deep fingerprint may send HTTP or TLS bytes to this extra port.</summary>
        public static bool IsPayloadSafe(int port) => !RawServicePorts.Contains(port);

        public static Task ProbeHttpAsync(NetworkDevice device, IPAddress addr, bool deep = false, CancellationToken ct = default) =>
            ProbeHttpAsync(device, addr, deep, everyPort: false, ct);

        /// <summary>
        /// Fetches the root page of the device's web ports. The first useful title and Server
        /// header feed classification. Every response is also recorded in
        /// <see cref="NetworkDevice.Access"/>, with whether it asked for a login. Normal scans stop
        /// at the first useful page; with <paramref name="everyPort"/> (deep scans) every web
        /// port is tried so each gets its own evidence. No credentials are ever sent.
        /// </summary>
        internal static async Task ProbeHttpAsync(NetworkDevice device, IPAddress addr, bool deep, bool everyPort, CancellationToken ct)
        {
            (int port, bool https)[] standard =
            [
                (80, false), (8080, false), (5000, false), (8123, false),
                (443, true), (8443, true), (8006, true), (5001, true),
            ];

            // Normal scan: only the known web ports. Deep rescan: also treat every other open
            // port as a possible web endpoint (HTTPS on TLS-likely ports, HTTP otherwise), so a
            // panel on a non-standard port still yields a title/Server. Standard ports go first
            // for stable results; extras are capped to keep the on-demand probe bounded.
            var candidates = standard.Where(c => device.OpenPorts.Contains(c.port)).ToList();
            if (deep)
            {
                candidates.AddRange(device.OpenPorts
                    .Where(p => !standard.Any(s => s.port == p) && IsPayloadSafe(p))
                    .Take(12)
                    .Select(p => (port: p, https: TlsLikelyPorts.Contains(p))));
            }

            using var handler = CreateFingerprintHandler();
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(deep ? 1000 : 1500),
                MaxResponseContentBufferSize = 256 * 1024
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KillerScan/1.3");

            foreach (var (port, https) in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var scheme = https ? "https" : "http";
                    var (resp, final, redirectStatus) = await GetDeviceResponseAsync(client, new Uri($"{scheme}://{addr}:{port}/"), addr, ct);
                    // A redirect is a server answering at this endpoint. What it led to is
                    // recorded under the endpoint that gave that answer, never under this one.
                    if (redirectStatus is { } moved) RecordWeb(device, scheme, port, moved, AccessLogin.Unknown, string.Empty);
                    if (resp == null) continue;
                    using (resp)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        ct.ThrowIfCancellationRequested();
                        int status = (int)resp.StatusCode;
                        string authScheme = resp.Headers.WwwAuthenticate.Select(a => a.Scheme).FirstOrDefault(IsTokenSafe) ?? string.Empty;
                        var login = status == 401 ? AccessLogin.Requested : HasLoginForm(body) ? AccessLogin.LoginForm : AccessLogin.Unknown;
                        // A same-address redirect to another root path still answers for that endpoint.
                        RecordWeb(device, final.Scheme, final.Port, status, login, authScheme);

                        // Classification reads the first useful page; later ports only add evidence.
                        if (device.HttpTitle.Length == 0 && device.HttpServer.Length == 0)
                        {
                            if (resp.Headers.TryGetValues("Server", out var serverVals))
                                device.HttpServer = string.Join(", ", serverVals).Trim();
                            // Bounded like HasLoginForm: the body is the device's, up to 256 KB, and a
                            // run of "<title" with no ">" backtracks for seconds on .NET Framework.
                            try
                            {
                                var m = Regex.Match(body, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase, HtmlScanLimit);
                                if (m.Success)
                                    device.HttpTitle = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
                            }
                            catch (RegexMatchTimeoutException) { }
                        }
                    }

                    if (!everyPort && (device.HttpTitle.Length > 0 || device.HttpServer.Length > 0))
                        return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
            ct.ThrowIfCancellationRequested();
        }

        // Direct (no proxy, which would answer in the device's place), no automatic redirects,
        // and, for a bound run, connections made from the adapter's address.
        private static HttpMessageHandler CreateFingerprintHandler()
        {
#if NET
            if (ScanSource.Current is { } binding)
            {
                var local = binding.LocalAddress;
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
                    ConnectCallback = async (context, token) =>
                    {
                        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        try
                        {
                            socket.Bind(new IPEndPoint(local, 0));
                            await socket.ConnectAsync(context.DnsEndPoint, token);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch { socket.Dispose(); throw; }
                    }
                };
            }
#endif
            return new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                AllowAutoRedirect = false,
                // A system or PAC proxy would answer in the device's place.
                UseProxy = false,
            };
        }

        private static readonly object AccessLock = new();

        private static void RecordWeb(NetworkDevice device, string scheme, int port, int status, AccessLogin login, string authScheme)
        {
            lock (AccessLock)
            {
                device.Access ??= [];
                device.Access.RemoveAll(e => e.Protocol == scheme && e.Port == port);
                device.Access.Add(new AccessEndpoint
                {
                    Protocol = scheme, Port = port, Evidence = AccessEvidence.HttpResponse, HttpStatus = status,
                    Login = login, AuthScheme = authScheme, ObservedAt = DateTimeOffset.UtcNow,
                });
            }
        }

        // An auth scheme is a short token such as Basic, Digest, NTLM or Negotiate.
        private static bool IsTokenSafe(string scheme) =>
            scheme.Length is > 0 and <= 32 && scheme.All(c => c < 128 && (char.IsLetterOrDigit(c) || c is '-' or '_'));

        private static readonly TimeSpan HtmlScanLimit = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// True when the page has a form with a password field: a sign-in page, which says a
        /// login exists, not that one would work. Comments, scripts and styles are ignored, and
        /// a login built by JavaScript or single sign-on is missed.
        /// </summary>
        internal static bool HasLoginForm(string html)
        {
            try
            {
                string page = Regex.Replace(html, @"<!--.*?-->|<(script|style)\b[^>]*>.*?</\1\s*>", " ",
                    RegexOptions.Singleline | RegexOptions.IgnoreCase, HtmlScanLimit);
                return Regex.Matches(page, @"<form\b[^>]*>(.*?)</form\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase, HtmlScanLimit)
                    .Cast<Match>()
                    .Any(form => Regex.IsMatch(form.Groups[1].Value, @"<input\b[^>]*\btype\s*=\s*[""']?password\b",
                        RegexOptions.Singleline | RegexOptions.IgnoreCase, HtmlScanLimit));
            }
            catch (RegexMatchTimeoutException) { return false; }
        }

        /// <summary>
        /// The device's own response, following up to two redirects that stay on the same
        /// address. <c>Final</c> is where the response came from, which a redirect can move to
        /// another port or scheme. <c>RedirectStatus</c> is the first redirect's status, if any.
        /// A redirect it will not follow returns no response.
        /// </summary>
        private static async Task<(HttpResponseMessage? Response, Uri Final, int? RedirectStatus)> GetDeviceResponseAsync(HttpClient client, Uri uri, IPAddress address, CancellationToken ct)
        {
            int? firstRedirect = null;
            for (int hop = 0; hop <= 2; hop++)
            {
                ct.ThrowIfCancellationRequested();
                HttpResponseMessage response;
                try { response = await SendFingerprintAsync(client, uri, ct); }
                catch (Exception) when (firstRedirect != null && !ct.IsCancellationRequested)
                {
                    // The redirect was an answer at this endpoint, even when the hop it pointed to
                    // fails or times out (HttpClient reports its own timeout as a cancellation).
                    return (null, uri, firstRedirect);
                }
                int status = (int)response.StatusCode;
                if (status < 300 || status >= 400) return (response, uri, firstRedirect);
                firstRedirect ??= status;
                Uri? location = response.Headers.Location;
                response.Dispose();
                if (hop == 2 || status is not (301 or 302 or 303 or 307 or 308) || location == null ||
                    !Uri.TryCreate(uri, location, out var next) ||
                    (next.Scheme != Uri.UriSchemeHttp && next.Scheme != Uri.UriSchemeHttps) ||
                    next.UserInfo.Length != 0 || !IPAddress.TryParse(next.Host, out var target) || !target.Equals(address) ||
                    // A redirect to a raw print port would make the GET print, like any payload there.
                    !IsPayloadSafe(next.Port))
                    return (null, uri, firstRedirect);
                uri = next;
            }
            return (null, uri, firstRedirect);
        }

#if NETFRAMEWORK
        // .NET Framework connects through a service point shared by every request to the same
        // scheme, host and port, and its bind delegate is read when the connection opens. One
        // lock per stripe of endpoints keeps a bound and an unbound request to the same endpoint
        // from swapping each other's delegate in between.
        private static readonly SemaphoreSlim[] ServicePointLocks = [.. Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1))];
#endif

        /// <summary>
        /// One fingerprint request. It closes its connection afterwards, so a connection opened
        /// from one adapter is never reused by a later request that should go from another.
        /// </summary>
        private static async Task<HttpResponseMessage> SendFingerprintAsync(HttpClient client, Uri uri, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.ConnectionClose = true;
#if NETFRAMEWORK
            var gate = ServicePointLocks[(uri.Scheme + uri.Authority).GetHashCode() & 31];
            await gate.WaitAsync(ct);
            try
            {
                var local = ScanSource.Current?.LocalAddress;
                ServicePointManager.FindServicePoint(uri).BindIPEndPointDelegate =
                    local == null ? null : (_, _, retry) => BindOnce(local, retry);
                return await client.SendAsync(request, ct);
            }
            finally { gate.Release(); }
#else
            return await client.SendAsync(request, ct);
#endif
        }

#if NETFRAMEWORK
        // The service point calls the delegate again after each failed bind. Retrying the same
        // address would spin, and returning null would let Windows pick another adapter, so a
        // second call fails the request instead.
        private static IPEndPoint BindOnce(IPAddress local, int retry) =>
            retry == 0 ? new IPEndPoint(local, 0) : throw new InvalidOperationException("The bound adapter's address " + local + " is no longer available.");
#endif

        /// <summary>
        /// Read SSH server banner (first line after TCP connect) from port 22.
        /// Returns strings like "SSH-2.0-OpenSSH_9.2p1" or "SSH-2.0-Cisco-1.25".
        /// </summary>
        private static async Task ProbeSshBannerAsync(NetworkDevice device, IPAddress addr)
        {
            try
            {
                using var tcp = ScanSource.Tcp();
                var connect = tcp.ConnectAsync(addr, 22).ObserveFault();
                if (await Task.WhenAny(connect, Task.Delay(1000)) != connect || !tcp.Connected)
                    return;
                using var stream = tcp.GetStream();
                stream.ReadTimeout = 1500;
                var buf = new byte[256];
                var readTask = stream.ReadAsync(buf, 0, buf.Length).ObserveFault();
                if (await Task.WhenAny(readTask, Task.Delay(1500)) != readTask) return;
                int n = readTask.Result;
                if (n <= 0) return;
                // RFC 4253 4.2 lets a server send other lines before its version line.
                string? banner = Encoding.ASCII.GetString(buf, 0, n)
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("SSH-", StringComparison.Ordinal));
                if (banner != null) device.SshBanner = banner;
            }
            catch { }
        }

        /// <summary>The ports a normal scan reads certificates from, 5001 being Synology DSM HTTPS.</summary>
        private static readonly int[] StandardTlsPorts = [443, 8443, 8006, 902, 5001];

        /// <summary>Whether a normal scan should run the TLS probe at all. It reads the same list
        /// the probe walks, so the gate cannot skip a port the probe would have read.</summary>
        internal static bool HasStandardTlsPort(NetworkDevice device) =>
            device.OpenPorts.Any(StandardTlsPorts.Contains);

        /// <summary>
        /// Pull TLS certificate Subject from the first responsive TLS port.
        /// </summary>
        public static async Task ProbeTlsCertAsync(NetworkDevice device, IPAddress addr, bool deep = false, CancellationToken ct = default)
        {
            int[] standardTls = StandardTlsPorts;

            // Normal scan: the known TLS ports only. Deep rescan: attempt a TLS handshake on every
            // open port (a non-TLS port just fails fast), so certs on non-standard ports are caught.
            int[] tlsPorts = deep
                ? [.. standardTls.Where(device.OpenPorts.Contains)
                    .Concat(device.OpenPorts.Where(p => !standardTls.Contains(p) && IsPayloadSafe(p)).Take(12))]
                : standardTls;
            foreach (var port in tlsPorts)
            {
                ct.ThrowIfCancellationRequested();
                if (!device.OpenPorts.Contains(port)) continue;
                try
                {
                    using var tcp = ScanSource.Tcp();
                    using var cancellation = ct.Register(() => tcp.Dispose());
                    var connect = tcp.ConnectAsync(addr, port).ObserveFault();
                    if (await AwaitWithCancellation(Task.WhenAny(connect, Task.Delay(1500, ct)), ct) != connect)
                        continue;
                    await connect;
                    ct.ThrowIfCancellationRequested();
                    using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                    var auth = ssl.AuthenticateAsClientAsync(addr.ToString(), null, SslProtocols.None, false).ObserveFault();
                    if (await AwaitWithCancellation(Task.WhenAny(auth, Task.Delay(1500, ct)), ct) != auth) continue;
                    await auth;
                    ct.ThrowIfCancellationRequested();
                    if (ssl.RemoteCertificate != null)
                    {
                        device.TlsSubject = ssl.RemoteCertificate.Subject;
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
            ct.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// UDP NetBIOS name service query (NBSTAT, port 137). Returns the machine's
        /// NetBIOS name for any Windows host that answers NBT, even when SMB is locked down.
        /// </summary>
        public static Task ProbeNetbiosAsync(NetworkDevice device, IPAddress addr) => ProbeNetbiosAsync(device, addr, UdpPortOverride ?? 137);

        // Windows NetBT owns UDP 137 on this machine, so fixtures pass their own port.
        internal static async Task ProbeNetbiosAsync(NetworkDevice device, IPAddress addr, int port)
        {
            // NBSTAT query for the wildcard name "*" (encoded as 32-byte level-2 name).
            byte[] query =
            [
                0x00, 0x00, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x20,
                0x43, 0x4B, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                0x00,
                0x00, 0x21,
                0x00, 0x01,
            ];

            // A random transaction ID plus a connected socket ties the reply to this query and host.
            var identity = new byte[2];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(identity);
            query[0] = identity[0];
            query[1] = identity[1];
            int transactionId = (identity[0] << 8) | identity[1];
            try
            {
                using var udp = ScanSource.Udp();
                udp.Client.SendTimeout = 500;
                udp.Client.ReceiveTimeout = 800;
                udp.Connect(addr, port);
                await udp.SendAsync(query, query.Length);

                var recvTask = udp.ReceiveAsync().ObserveFault();
                if (await Task.WhenAny(recvTask, Task.Delay(800)) != recvTask) return;
                if (NetbiosResponse.TryDecode(recvTask.Result.Buffer, transactionId, out var name))
                    device.NetbiosName = name;
            }
            catch { }
        }

        /// <summary>
        /// SNMPv1 GET of sysDescr.0 (1.3.6.1.2.1.1.1.0) with community "public".
        /// Works on most network gear, printers, and UPSes that expose SNMP.
        /// </summary>
        public static async Task ProbeSnmpAsync(NetworkDevice device, IPAddress addr)
        {
            // SNMPv1 GetRequest for sysDescr.0, community "public".
            byte[] query =
            [
                0x30, 0x29,
                0x02, 0x01, 0x00,
                0x04, 0x06, 0x70, 0x75, 0x62, 0x6C, 0x69, 0x63,
                0xA0, 0x1C,
                0x02, 0x04, 0x7F, 0x8B, 0x2C, 0x1D,
                0x02, 0x01, 0x00,
                0x02, 0x01, 0x00,
                0x30, 0x0E,
                0x30, 0x0C,
                0x06, 0x08, 0x2B, 0x06, 0x01, 0x02, 0x01, 0x01, 0x01, 0x00,
                0x05, 0x00,
            ];

            var identity = new byte[4];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(identity);
            identity[0] = (byte)((identity[0] & 0x3f) | 0x40);
            Array.Copy(identity, 0, query, 17, identity.Length);
            int requestId = (identity[0] << 24) | (identity[1] << 16) | (identity[2] << 8) | identity[3];
            try
            {
                using var udp = ScanSource.Udp();
                udp.Client.SendTimeout = 500;
                udp.Client.ReceiveTimeout = 1000;
                udp.Connect(addr, 161);
                await udp.SendAsync(query, query.Length);

                var recvTask = udp.ReceiveAsync().ObserveFault();
                if (await Task.WhenAny(recvTask, Task.Delay(1000)) != recvTask) return;
                var resp = recvTask.Result.Buffer;
                if (SnmpResponse.TryDecode(resp, requestId, out var description)) device.SnmpDescr = description;
            }
            catch { }
        }

        /// <summary>
        /// Quick liveness check for hosts that don't answer ICMP: try to open a TCP connection to a few
        /// common ports in parallel and report alive on the first that connects. Catches firewalled-but-
        /// serving hosts, and remote hosts over a VPN where ping/ARP don't reach. Each attempt is bounded
        /// by a short delay race; disposing the client aborts any still-pending connect, so nothing leaks.
        /// </summary>
        private static async Task<bool> TcpAliveAsync(IPAddress addr)
        {
            var tasks = LivenessPorts.Select(async port =>
            {
                try
                {
                    using var client = ScanSource.Tcp();
                    var connect = client.ConnectAsync(addr, port).ObserveFault();
                    if (await Task.WhenAny(connect, Task.Delay(300)) == connect && client.Connected)
                        return true;
                }
                catch { }
                return false;
            });

            var results = await Task.WhenAll(tasks);
            return results.Any(r => r);
        }

        /// <summary>
        /// Get MAC address of a host using ARP.
        /// </summary>
        private static string GetMacAddress(IPAddress addr) => ScanSource.ResolveMac(addr);
    }
}
