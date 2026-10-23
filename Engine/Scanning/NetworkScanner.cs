using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace KillerScan.Engine
{
    public partial class NetworkScanner
    {
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

        /// <summary>
        /// A manual device type keyed by MAC address. When it returns a value, that type wins over
        /// classification. Set once by the host; unset means no manual types.
        /// </summary>
        public static Func<string, string?>? ManualTypeLookup { get; set; }

        /// <summary>
        /// Runs on every finished device before it is reported, so the host can apply saved
        /// preferences such as a user-given name. Set once by the host.
        /// </summary>
        public static Action<NetworkDevice>? DeviceCompleted { get; set; }

        /// <summary>Per-scanner host preferences; unset retains the static API defaults.</summary>
        public Func<string, string?>? ManualTypeResolver { get; set; }
        public Action<NetworkDevice>? DeviceFinalizer { get; set; }

        // Network-wide service discovery results (collected once per scan, keyed by IP).
        private Dictionary<string, MulticastDiscovery.MdnsInfo> _mdns = [];
        private Dictionary<string, string> _ssdp = [];
        private DateTimeOffset? _mdnsObservedAtUtc, _ssdpObservedAtUtc;

        /// <summary>
        /// Parse a CIDR subnet string into a list of IP addresses.
        /// </summary>
        public static List<IPAddress> GetAddressesInSubnet(string cidr)
        {
            var parts = cidr.Trim().Split('/');
            var ip = IPAddress.Parse(parts[0]);
            int prefixLen = parts.Length > 1 ? int.Parse(parts[1]) : 24;
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
        public Task<List<NetworkDevice>> ScanSubnetAsync(string cidr, CancellationToken ct, bool fullScan = true) =>
            ScanSubnetAsync(cidr, ct, new ScanOptions(), fullScan);

        public async Task<List<NetworkDevice>> ScanSubnetAsync(
            string cidr, CancellationToken ct, ScanOptions options, bool fullScan = true)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            using var run = new ScanRun(_binding, ct);
            try { return await ScanSubnetCoreAsync(cidr, run.Token, options, fullScan, run, ProbesFor(run)); }
            catch (OperationCanceledException) when (run.ConnectionChanged) { throw run.Changed(); }
        }

        private async Task<List<NetworkDevice>> ScanSubnetCoreAsync(string cidr, CancellationToken ct, ScanOptions options, bool fullScan, ScanRun run, ScanProbes probes)
        {
            ct.ThrowIfCancellationRequested();
            if (options.ReverseDns) probes.FlushDns();
            // Clear previous enrichment even if a disabled or failed collector returns no data.
            _mdns = []; _mdnsObservedAtUtc = null;
            _ssdp = []; _ssdpObservedAtUtc = null;
            // ScanTargets handles one or many comma-separated targets (CIDR, single host, or a
            // range) and de-duplicates overlaps. The UI validates the same string before getting
            // here, so a parse failure at this point yields an empty list rather than throwing.
            var parsed = ScanTargets.Parse(cidr);
            var addresses = parsed.Addresses;
            foreach (var address in addresses) run.CheckTarget(address);
            // Status text uses the short summary ("192.168.9.0/24 +2"), not the raw box contents,
            // so a long multi-target list does not overrun the status bar.
            string label = parsed.Summary.Length > 0 ? parsed.Summary : cidr;
            var devices = new List<NetworkDevice>();
            int completed = 0;
            int total = addresses.Count;

            // Phase 1: Fast ping sweep + ARP cache
            StatusChanged?.Invoke(new ScanStatus(ScanStage.Discovering, addresses.Count, label));
            var discoveredHosts = new System.Collections.Concurrent.ConcurrentDictionary<string, (IPAddress Addr, string Mac)>();

            // Grab existing ARP cache first (instant, catches IoT devices)
            var arpCache = options.NeighborCache ? probes.ReadNeighbors() : new Dictionary<string, string>();
            var addressSet = new HashSet<string>(addresses.Select(a => a.ToString()));
            foreach (var entry in arpCache)
            {
                if (addressSet.Contains(entry.Key))
                    discoveredHosts.TryAdd(entry.Key, (IPAddress.Parse(entry.Key), entry.Value));
            }

            // Kick off network-wide service discovery (mDNS + SSDP) to run alongside the ping sweep;
            // results are awaited before the probe phase and mapped to hosts by source IP.
            var mdnsTask = options.Mdns ? probes.Mdns(ct) : Task.FromResult(new Dictionary<string, MulticastDiscovery.MdnsInfo>());
            var ssdpTask = options.Ssdp ? probes.Ssdp(ct) : Task.FromResult(new Dictionary<string, string>());

            // Fast parallel ping sweep (async, no blocking ARP calls)
            var semaphore = new SemaphoreSlim(200);
            var scanTasks = addresses.Select(async addr =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    bool alive = options.Icmp && (await probes.Ping(addr, 500)).HasValue;
                    // ICMP is commonly blocked (Windows Firewall default), and ARP can't reach a host
                    // across a routed/VPN link, so a silent host isn't necessarily down. Fall back to a
                    // quick TCP connect on a few common ports - anything that answers is alive even when
                    // it drops ping. This is what closes the "20 found vs 200 real" gap over a VPN.
                    if (!alive && options.TcpLiveness)
                        alive = await probes.TcpAlive(addr);
                    if (alive)
                        discoveredHosts.TryAdd(addr.ToString(), (addr, ""));
                }
                catch (NetworkBindingException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch { }
                finally
                {
                    semaphore.Release();
                    int done = Interlocked.Increment(ref completed);
                    ProgressChanged?.Invoke((int)(done * 30.0 / total));
                }
            });

            await Task.WhenAll(scanTasks);
            ct.ThrowIfCancellationRequested();

            // Second ARP cache read: the ping wave stimulates ARP responses from devices
            // that are too slow (or don't reply to ICMP at all). Reading the cache again
            // catches them without requiring a second full scan.
            var arpCache2 = options.NeighborCache ? probes.ReadNeighbors() : new Dictionary<string, string>();
            foreach (var entry in arpCache2)
            {
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
                    mac = await AwaitWithCancellation(Task.Run(() => probes.ResolveMac(addr), ct), ct);
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
                    if (!string.IsNullOrEmpty(hMac) && bogusMacs.Contains(hMac))
                        discoveredHosts[ip] = (hAddr, "");
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
            try { _mdns = await mdnsTask; _mdnsObservedAtUtc = DateTimeOffset.UtcNow; } catch (NetworkBindingException) { throw; } catch { }
            try { _ssdp = await ssdpTask; _ssdpObservedAtUtc = DateTimeOffset.UtcNow; } catch (NetworkBindingException) { throw; } catch { }

            if (fullScan)
            {
                StatusChanged?.Invoke(new ScanStatus(ScanStage.Probing, total));
                var probeSemaphore = new SemaphoreSlim(20);
                var probeTasks = sortedHosts.Select(async entry =>
                {
                    await probeSemaphore.WaitAsync(ct);
                    try
                    {
                        var device = await ProbeHostAsync(entry.Addr, entry.Mac, ct, options, probes);
                        ct.ThrowIfCancellationRequested();
                        DeviceFound?.Invoke(device);
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
                            ObservationStartedAtUtc = DateTimeOffset.UtcNow,
                            IpAddress = entry.Addr.ToString(),
                            MacAddress = entry.Mac,
                            HostnameSource = options.ReverseDns ? "DNS" : "",
                            Hostname = options.ReverseDns ? await AwaitWithCancellation(probes.Hostname(entry.Addr), ct) : string.Empty
                        };

                        if (!string.IsNullOrEmpty(entry.Mac))
                            device.Vendor = ResolveVendor(entry.Mac);

                        ct.ThrowIfCancellationRequested();

                        // Classify even in quick scan (hostname + OUI, no ports)
                        device.DeviceType = ClassifyForScan(device, options, "quick");
                        (DeviceFinalizer ?? DeviceCompleted)?.Invoke(device);

                        DeviceFound?.Invoke(device);
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

        /// <summary>The neighbor (ARP) cache from the platform.</summary>
        private static IReadOnlyDictionary<string, string> GetArpCache() =>
            NetworkPlatform.Current.ReadNeighborCache();

        /// <summary>
        /// Probe a single host for hostname, open ports, fingerprints, and device type.
        /// </summary>
        private static async Task<string> ResolveVerifiedHostnameAsync(IPAddress addr)
        {
            try
            {
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
                _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
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

        private async Task<NetworkDevice> ProbeHostAsync(IPAddress addr, string cachedMac, CancellationToken token, ScanOptions options, ScanProbes probes)
        {
            token.ThrowIfCancellationRequested();
            var device = new NetworkDevice
            {
                ObservationStartedAtUtc = DateTimeOffset.UtcNow,
                IpAddress = addr.ToString(),
                MacAddress = cachedMac
            };

            var dnsTask = options.ReverseDns ? ResolveName() : Task.CompletedTask;
            async Task ResolveName() { device.Hostname = await probes.Hostname(addr); if (device.Hostname.Length > 0) device.HostnameSource = "DNS"; }
            var ttlTask = options.Icmp ? ResolveTtl() : Task.CompletedTask;
            async Task ResolveTtl() => device.Ttl = await probes.Ping(addr, 400);

            var portTasks = (options.Ports ? ProbePorts : Array.Empty<int>()).Select(async port =>
            {
                token.ThrowIfCancellationRequested();
                return await probes.Connect(addr, port, 200) ? port : -1;
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
                device.MdnsServices = [.. md.Services]; device.MdnsObservedAtUtc = _mdnsObservedAtUtc;
                if (string.IsNullOrEmpty(device.Hostname) && !string.IsNullOrEmpty(md.Name))
                { device.Hostname = md.Name; device.HostnameSource = "mDNS"; }
            }
            if (options.Ssdp && _ssdp.TryGetValue(device.IpAddress, out var srv))
                { device.SsdpServer = srv; device.SsdpObservedAtUtc = _ssdpObservedAtUtc; }

            // -- Fingerprint probes: run in parallel, each is gated on relevant open ports --
            var fpTasks = new List<Task>();
            if (options.Http && device.OpenPorts.Any(p => DeviceAccess.SuggestedWebProtocol(p) != null))
                fpTasks.Add(probes.Http(device, addr, false, token));
            if (options.Ssh && device.OpenPorts.Contains(22))
                fpTasks.Add(probes.Ssh(device, addr));
            if (options.Tls && device.OpenPorts.Any(p => p == 443 || p == 8443 || p == 8006 || p == 902))
                fpTasks.Add(probes.Tls(device, addr, false));
            // NetBIOS + SNMP are UDP -- we probe regardless of TCP state (ports 137/161 UDP).
            if (options.Netbios) fpTasks.Add(probes.Netbios(device, addr));
            if (options.Snmp) fpTasks.Add(probes.Snmp(device, addr));

            await AwaitWithCancellation(Task.WhenAll(fpTasks), token);

            // Last-resort name: NetBIOS computer name when reverse DNS and mDNS both came up empty.
            if (string.IsNullOrEmpty(device.Hostname) && !string.IsNullOrEmpty(device.NetbiosName))
            { device.Hostname = device.NetbiosName; device.HostnameSource = "NetBIOS"; }

            // Classify device type using weighted scoring over all signals.
            device.DeviceType = ClassifyForScan(device, options, "full");
            (DeviceFinalizer ?? DeviceCompleted)?.Invoke(device);

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
        public Task<NetworkDevice> DeepProbeHostAsync(
            string ip, CancellationToken ct, int portConcurrency = 256,
            bool flushLocalDnsCache = true) =>
            DeepProbeHostAsync(ip, ct, new ScanOptions(), portConcurrency, flushLocalDnsCache);

        public async Task<NetworkDevice> DeepProbeHostAsync(
            string ip, CancellationToken ct, ScanOptions options, int portConcurrency = 256,
            bool flushLocalDnsCache = true)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            using var run = new ScanRun(_binding, ct);
            try { return await DeepProbeCoreAsync(ip, run.Token, options, portConcurrency, flushLocalDnsCache, run, ProbesFor(run)); }
            catch (OperationCanceledException) when (run.ConnectionChanged) { throw run.Changed(); }
        }

        private async Task<NetworkDevice> DeepProbeCoreAsync(string ip, CancellationToken ct, ScanOptions options, int portConcurrency, bool flushLocalDnsCache, ScanRun run, ScanProbes probes)
        {
            ct.ThrowIfCancellationRequested();
            if (options.ReverseDns && flushLocalDnsCache) probes.FlushDns();
            var addr = IPAddress.Parse(ip);
            run.CheckTarget(addr);
            var device = new NetworkDevice
            {
                ObservationStartedAtUtc = DateTimeOffset.UtcNow,
                IpAddress  = ip,
                // Fresh MAC via ARP.
                MacAddress = options.Arp ? await AwaitWithCancellation(Task.Run(() => probes.ResolveMac(addr), ct), ct) : string.Empty
            };

            var dnsTask = options.ReverseDns ? ResolveName() : Task.CompletedTask;
            async Task ResolveName() { device.Hostname = await probes.Hostname(addr); if (device.Hostname.Length > 0) device.HostnameSource = "DNS"; }
            var ttlTask = options.Icmp ? ResolveTtl() : Task.CompletedTask;
            async Task ResolveTtl() => device.Ttl = await probes.Ping(addr, 800);

            // Exhaustive TCP sweep. Bounded concurrency keeps the open-socket count sane;
            // each port gets a generous timeout and one retry so slow/loaded hosts still answer.
            using var gate = new SemaphoreSlim(portConcurrency);
            var portTasks = (options.Ports ? DeepPortList : Array.Empty<int>()).Select(async port =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (await probes.Connect(addr, port, 700)) return port;
                    if (await probes.Connect(addr, port, 900)) return port;   // retry catches slow responders
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
                device.MdnsServices = [.. md.Services]; device.MdnsObservedAtUtc = _mdnsObservedAtUtc;
                if (string.IsNullOrEmpty(device.Hostname) && !string.IsNullOrEmpty(md.Name))
                { device.Hostname = md.Name; device.HostnameSource = "mDNS"; }
            }
            if (options.Ssdp && _ssdp.TryGetValue(ip, out var srv))
                { device.SsdpServer = srv; device.SsdpObservedAtUtc = _ssdpObservedAtUtc; }

            // Full fingerprint pass. Deep mode widens HTTP/TLS to every open port (not just the
            // standard web/TLS ones), so services on non-standard ports still get fingerprinted.
            var fpTasks = new List<Task>();
            if (options.Netbios) fpTasks.Add(probes.Netbios(device, addr));
            if (options.Snmp) fpTasks.Add(probes.Snmp(device, addr));
            if (device.OpenPorts.Count > 0)
            {
                if (options.Http) fpTasks.Add(probes.Http(device, addr, true, ct));
                if (options.Tls) fpTasks.Add(probes.Tls(device, addr, true));
            }
            if (options.Ssh && device.OpenPorts.Contains(22))
                fpTasks.Add(probes.Ssh(device, addr));
            await AwaitWithCancellation(Task.WhenAll(fpTasks), ct);
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(device.Hostname) && !string.IsNullOrEmpty(device.NetbiosName))
            { device.Hostname = device.NetbiosName; device.HostnameSource = "NetBIOS"; }

            device.DeviceType = ClassifyForScan(device, options, "deep");
            (DeviceFinalizer ?? DeviceCompleted)?.Invoke(device);
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
                using var client = new TcpClient();
                var connect = client.ConnectAsync(addr, port);
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
        /// Records source evidence; names are weak hints and manual assignments remain separate.
        ///
        /// Signal strength rough guide:
        ///   15+  strong positive (protocol banner, TLS cert, specific HTTP title)
        ///   8-12 vendor + port combo, unique signature (e.g. port 8006 = Proxmox)
        ///   4-7  weak supporting signal (TTL, generic port presence)
        ///   1-3  tie-breaker only
        /// </summary>
        public static string ClassifyDevice(NetworkDevice device) =>
            ClassifyDeviceForNetwork(device, GatewayIp, DnsIp, ManualTypeLookup);

        /// <summary>Assess supplied observations without probing, global routing hints or saved manual preferences.</summary>
        public static DeviceIdentification IdentifyDevice(NetworkDevice device)
        {
            device.DeviceType = ClassifyDeviceForNetwork(device, "", "", null);
            return device.Identification!;
        }

        internal static string ClassifyDeviceForNetwork(NetworkDevice device, string gatewayIp, string dnsIp, Func<string, string?>? manualLookup)
        {
            // 1. Manual override always wins.
            var manual = manualLookup?.Invoke(device.MacAddress);
            var assessment = new RecognitionAssessment(device, manual);

            var ports = device.OpenPorts;
            string host = RecognitionAssessment.Bounded(device.Hostname).ToLowerInvariant();
            string title = RecognitionAssessment.Bounded(device.HttpTitle).ToLowerInvariant();
            string server = RecognitionAssessment.Bounded(device.HttpServer).ToLowerInvariant();
            string tls = RecognitionAssessment.Bounded(device.TlsSubject).ToLowerInvariant();
            string ssh = RecognitionAssessment.Bounded(device.SshBanner).ToLowerInvariant();
            string snmp = RecognitionAssessment.Bounded(device.SnmpDescr).ToLowerInvariant();
            string nbName = RecognitionAssessment.Bounded(device.NetbiosName).ToLowerInvariant();
            string ssdp = RecognitionAssessment.Bounded(device.SsdpServer).ToLowerInvariant();

            // 1b. The default gateway is the router. Surface that role (plus DNS when it serves it),
            //     ahead of the port heuristics that would otherwise label it just "DNS Server".
            if (!string.IsNullOrEmpty(gatewayIp) && device.IpAddress == gatewayIp)
            {
                // Only "Router/DNS" when the gateway IS the configured DNS server. A router that just
                // has port 53 open (forwarding) but points DNS at a Pi-hole stays plain "Router".
                bool isDnsServer = !string.IsNullOrEmpty(dnsIp) && device.IpAddress == dnsIp;
                assessment.Route(gatewayIp, dnsIp); return assessment.Finish(isDnsServer ? "Router/DNS" : "Router");
            }

            // 2. Names are weak hints, not a reason to ignore contradictory protocol evidence.
            foreach (var (pattern, type) in HostnameKeywords)
            {
                if (host.Contains(pattern))
                    assessment.Add(type, 6, "Name");
            }

            // 3. OUI vendor with known-bad corrections.
            string vendor = RecognitionAssessment.Bounded(device.Vendor).ToLowerInvariant();
            if (!string.IsNullOrEmpty(device.MacAddress) && device.MacAddress.Length >= 8)
            {
                string prefix = device.MacAddress[..8].ToUpperInvariant();
                if (OuiBadMap.TryGetValue(prefix, out var corrected))
                { vendor = corrected.ToLowerInvariant(); assessment.Vendor(corrected); }
            }

            // 4. Score candidate types from observed signals.
            void Add(string type, int weight, params string[] sources) => assessment.Add(type, weight, sources);

            bool hasWorkstationPorts = ports.Contains(3389) || ports.Contains(445);
            bool hasPrintProtocol = ports.Contains(9100) || ports.Contains(515) || ports.Contains(631);
            bool hasActiveDirectoryPorts = ports.Contains(88) && ports.Contains(389) && ports.Contains(445);

            // -- Hypervisor --
            if (ports.Contains(8006)) Add("Hypervisor", 15, "TCP");
            if (title.Contains("proxmox")) Add("Hypervisor", 15, "HTTP");
            if (tls.Contains("vmware")) Add("Hypervisor", 15, "TLS");
            if (title.Contains("vmware esxi")) Add("Hypervisor", 15, "HTTP");
            if (ports.Contains(902) && ports.Contains(443) && !hasWorkstationPorts) Add("Hypervisor", 10, "TCP");
            if (ports.Contains(2179) && (ports.Contains(5985) || ports.Contains(5986)) && !hasWorkstationPorts) Add("Hypervisor", 8, "TCP");
            if (tls.Contains("xenserver")) Add("Hypervisor", 15, "TLS");
            if (title.Contains("xenserver")) Add("Hypervisor", 15, "HTTP");

            // -- Windows workstation --
            if (ports.Contains(3389) && ports.Contains(445)) Add("Windows", 6, "TCP");
            if (server.Contains("microsoft-iis")) Add("Windows", 6, "HTTP");
            if (!string.IsNullOrEmpty(nbName) && ports.Contains(445)) Add("Windows", 4, "NetBIOS", "TCP");
            if (device.Ttl is >= 120 and <= 128 && ports.Contains(445)) Add("Windows", 3, "TCP", "ICMP");

            // -- Windows Server --
            if (ports.Contains(3389) && ports.Contains(445) && (ports.Contains(80) || ports.Contains(443) || ports.Contains(53)))
                Add("Windows Server", 6, "TCP");
            if (hasActiveDirectoryPorts) Add("Windows Server", 20, "TCP");
            if (ports.Contains(53) && ports.Contains(88) && ports.Contains(389)) Add("Windows Server", 16, "TCP");
            if (ports.Contains(445) && (ports.Contains(636) || ports.Contains(3268) || ports.Contains(3269)))
                Add("Windows Server", 14, "TCP");
            if (ports.Contains(135) && ports.Contains(445) && ports.Contains(3389)) Add("Windows Server", 8, "TCP");
            if (title.Contains("exchange") || server.Contains("exchange")) Add("Windows Server", 10, "HTTP");
            if (snmp.Contains("windows server")) Add("Windows Server", 15, "SNMP");

            // -- Linux / SSH --
            // Windows boxes with OpenSSH Server advertise "OpenSSH_for_Windows" -- score Windows, not Linux.
            bool sshIsWindows = ssh.Contains("for_windows");
            if (sshIsWindows) Add("Windows", 15, "SSH");
            if (!sshIsWindows && (ssh.StartsWith("ssh-2.0-openssh") || ssh.StartsWith("ssh-1.99-openssh"))) Add("Linux/SSH", 8, "SSH");
            if (device.Ttl is >= 60 and <= 64 && ports.Contains(22) && !sshIsWindows) Add("Linux/SSH", 3, "TCP", "ICMP");

            // -- Network gear (switches, routers, firewalls, APs) --
            bool isNetworkVendor = vendor.Contains("cisco") || vendor.Contains("ubiquiti")
                || vendor.Contains("aruba") || vendor.Contains("ruckus") || vendor.Contains("meraki")
                || vendor.Contains("netgear") || vendor.Contains("tp-link") || vendor.Contains("fortinet")
                || vendor.Contains("juniper") || vendor.Contains("mikrotik") || vendor.Contains("gl technologies")
                || vendor.Contains("gl.inet") || vendor.Contains("draytek") || vendor.Contains("zyxel")
                || vendor.Contains("linksys") || vendor.Contains("sonicwall") || vendor.Contains("watchguard");
            if (isNetworkVendor) Add("Network", 8, "OUI");
            if (ssh.Contains("cisco") || ssh.Contains("routeros") || ssh.Contains("mikrotik")) Add("Network", 15, "SSH");
            if (title.Contains("unifi") || title.Contains("fortigate") || title.Contains("sonicwall")
                || title.Contains("pfsense") || title.Contains("opnsense") || title.Contains("mikrotik")) Add("Network", 15, "HTTP");
            if (snmp.Contains("cisco ios") || snmp.Contains("juniper") || snmp.Contains("fortigate")) Add("Network", 12, "SNMP");

            // Router vs Switch/AP disambiguation within network gear.
            if (isNetworkVendor && ports.Contains(53)) Add("Router", 10, "TCP", "OUI");
            if (isNetworkVendor && ports.Contains(161) && !ports.Contains(53)) Add("Switch/AP", 8, "TCP", "OUI");

            // -- Printer --
            bool isPrinterVendor = vendor.Contains("canon") || vendor.Contains("epson")
                || vendor.Contains("brother") || vendor.Contains("xerox") || vendor.Contains("lexmark")
                || vendor.Contains("ricoh") || vendor.Contains("konica") || vendor.Contains("kyocera");
            // HP vendor alone is ambiguous (laptops, servers, printers). Only trust with printer ports.
            if (ports.Contains(9100)) Add("Printer", 10, "TCP");
            if (!hasActiveDirectoryPorts && (ports.Contains(515) || ports.Contains(631))) Add("Printer", 4, "TCP");
            if (isPrinterVendor && hasPrintProtocol) Add("Printer", 12, "TCP", "OUI");
            if (isPrinterVendor && ports.Count <= 3) Add("Printer", 6, "OUI");
            if (vendor.Contains("hewlett packard") && ports.Contains(9100)) Add("Printer", 12, "TCP", "OUI");
            if (snmp.Contains("laserjet") || snmp.Contains("officejet") || snmp.Contains("printer")) Add("Printer", 15, "SNMP");
            if (title.Contains("embedded web server") || title.Contains("web image monitor")) Add("Printer", 10, "HTTP");

            // -- NAS --
            if (vendor.Contains("synology") || vendor.Contains("qnap") || vendor.Contains("asustor")
                || vendor.Contains("drobo") || vendor.Contains("buffalo") || vendor.Contains("terramaster"))
                Add("NAS", 12, "OUI");
            if (title.Contains("diskstation") || title.Contains("synology") || title.Contains("dsm "))
                Add("NAS", 15, "HTTP");
            if (title.Contains("qts") || title.Contains("qnap") || title.Contains("truenas") || title.Contains("freenas"))
                Add("NAS", 15, "HTTP");
            if (ports.Contains(548)) Add("NAS", 4, "TCP");

            // -- Apple iDevice (iPhone / iPad) --
            bool isApple = vendor.Contains("apple");
            // Port 62078 is the Apple iDevice sync/tether port -- exclusively iPhones and iPads.
            // Score regardless of OUI so randomized-MAC devices can still be suggested.
            if (ports.Contains(62078)) Add("iPhone", 15, "TCP");
            if (isApple && ports.Contains(62078)) Add("iPhone", 5, "TCP", "OUI");  // OUI bonus
            if (device.MdnsServices.Take(16).Select(value => RecognitionAssessment.Bounded(value).ToLowerInvariant()).Any(s => s.Contains("_airplay") || s.Contains("_raop") || s.Contains("_airport")))
                Add("Apple Device", 10, "mDNS");

            // -- Surveillance camera --
            if (vendor.Contains("hikvision") || vendor.Contains("dahua") || vendor.Contains("axis")
                || vendor.Contains("amcrest") || vendor.Contains("reolink") || vendor.Contains("foscam"))
                Add("Camera", 12, "OUI");
            if (title.Contains("hikvision") || title.Contains("dahua") || title.Contains("camera")
                || title.Contains("dvr") || title.Contains("nvr") || title.Contains("ipcam"))
                Add("Camera", 12, "HTTP");
            if (ports.Contains(554)) Add("Camera", 4, "TCP");

            // -- IoT / smart home --
            if (vendor.Contains("espressif") || vendor.Contains("tuya") || vendor.Contains("sonoff")
                || vendor.Contains("shelly") || vendor.Contains("nest") || vendor.Contains("ecobee")
                || vendor.Contains("signify") || vendor.Contains("lutron") || vendor.Contains("wemo")
                || vendor.Contains("wyze") || vendor.Contains("aqara") || vendor.Contains("linkplay")
                || vendor.Contains("wiim") || vendor.Contains("govee"))
                Add("IoT", 10, "OUI");

            // -- Home Assistant --
            if (ports.Contains(8123)) Add("Home Assistant", 12, "TCP");
            if (title.Contains("home assistant")) Add("Home Assistant", 15, "HTTP");
            if (host.Contains("homeassistant") || host.Contains("home-assistant")) Add("Home Assistant", 6, "Name");

            // -- DNS server (Pi-hole, AdGuard, BIND) --
            if (title.Contains("pi-hole") || title.Contains("pihole")) Add("DNS Server", 15, "HTTP");
            if (title.Contains("adguard")) Add("DNS Server", 15, "HTTP");
            if (ports.Contains(53) && ports.Contains(80) && !isNetworkVendor) Add("DNS Server", 6, "TCP");

            // -- mDNS (Bonjour) service types: the strongest, most specific signals --
            bool HasSvc(string s) => device.MdnsServices.Take(16).Select(value => RecognitionAssessment.Bounded(value).ToLowerInvariant()).Any(x => x.Contains(s));
            if (HasSvc("_googlecast")) Add("Smart TV", 14, "mDNS");
            if (HasSvc("_printer") || HasSvc("_ipp") || HasSvc("_pdl-datastream")) Add("Printer", 14, "mDNS");
            if (HasSvc("_sonos") || HasSvc("_spotify-connect")) Add("Media Streamer", 12, "mDNS");
            if (HasSvc("_airplay") || HasSvc("_raop")) Add("Media Streamer", 8, "mDNS");
            if (HasSvc("_hap") || HasSvc("_homekit") || HasSvc("_hue")) Add("IoT", 10, "mDNS");

            // -- SSDP / UPnP SERVER string --
            if (ssdp.Contains("roku")) Add("Smart TV", 12, "SSDP");
            if (ssdp.Contains("synology") || ssdp.Contains("qnap")) Add("NAS", 12, "SSDP");
            if (ssdp.Contains("plex")) Add("Media Streamer", 12, "SSDP");
            if (ssdp.Contains("sonos") || ssdp.Contains("dlna") || ssdp.Contains("mediaserver")) Add("Media Streamer", 8, "SSDP");
            if (ssdp.Contains("samsung") && ssdp.Contains("tv")) Add("Smart TV", 10, "SSDP");

            // -- New ports --
            if (ports.Contains(32400)) Add("Media Streamer", 12, "TCP");          // Plex
            if (ports.Contains(1883) || ports.Contains(8883)) Add("IoT", 6, "TCP"); // MQTT brokers

            // -- Generic web device (catch-all) --
            if (ports.Contains(80) || ports.Contains(443) || ports.Contains(8080)) Add("Web Device", 2, "TCP");

            return assessment.Finish();
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

        private static Task ProbeHttpAsync(NetworkDevice device, IPAddress addr, bool deep = false) =>
            ProbeHttpWithCancellationAsync(device, addr, deep, CancellationToken.None);

        internal static async Task ProbeHttpWithCancellationAsync(NetworkDevice device, IPAddress addr, bool deep, CancellationToken token)
        {
            (int port, bool https)[] standard =
            [
                (80, false), (8080, false), (5000, false), (8123, false),
                (443, true), (8443, true), (8006, true), (5001, true), (5357, false), (32400, false),
            ];

            // Normal scan: only the known web ports. Deep rescan: also treat every other open
            // port as a possible web endpoint (HTTPS on TLS-likely ports, HTTP otherwise), so a
            // panel on a non-standard port still yields a title/Server. Standard ports go first
            // for stable results; extras are capped to keep the on-demand probe bounded.
            var candidates = standard.Where(c => device.OpenPorts.Contains(c.port)).ToList();
            if (deep)
            {
                candidates.AddRange(device.OpenPorts
                    .Where(p => !standard.Any(s => s.port == p))
                    .Take(12)
                    .SelectMany(p => new[] { (port: p, https: TlsLikelyPorts.Contains(p)), (port: p, https: !TlsLikelyPorts.Contains(p)) }));
            }

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                AllowAutoRedirect = false, UseProxy = false, UseCookies = false,
                MaxAutomaticRedirections = 2,
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(deep ? 1000 : 1500),
                MaxResponseContentBufferSize = 256 * 1024
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KillerScan/1.3");

            foreach (var (port, https) in candidates)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (device.Access?.Any(item => item.Port == port && item.Verification == "HttpResponse" && item.HttpStatus is not (400 or 426)) == true) continue;
                    var scheme = https ? "https" : "http";
                    using var resp = await client.GetAsync($"{scheme}://{addr}:{port}/", HttpCompletionOption.ResponseHeadersRead, token);
                    HttpAccessObservation.Record(device, port, https, (int)resp.StatusCode, resp.Headers.ToString(), "");

                    var body = await HttpAccessObservation.ReadBody(resp.Content, deep ? 1000 : 1500, token);
                    HttpAccessObservation.Record(device, port, https, (int)resp.StatusCode, resp.Headers.ToString(), body);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch { }
            }
        }

        /// <summary>
        /// Read SSH server banner (first line after TCP connect) from port 22.
        /// Returns strings like "SSH-2.0-OpenSSH_9.2p1" or "SSH-2.0-Cisco-1.25".
        /// </summary>
        private static async Task ProbeSshBannerAsync(NetworkDevice device, IPAddress addr)
        {
            try
            {
                using var tcp = new TcpClient();
                var connect = tcp.ConnectAsync(addr, 22);
                if (await Task.WhenAny(connect, Task.Delay(1000)) != connect || !tcp.Connected)
                    return;
                using var stream = tcp.GetStream();
                stream.ReadTimeout = 1500;
                var buf = new byte[256];
                var readTask = stream.ReadAsync(buf, 0, buf.Length);
                if (await Task.WhenAny(readTask, Task.Delay(1500)) != readTask) return;
                int n = readTask.Result;
                if (n <= 0) return;
                string banner = Encoding.ASCII.GetString(buf, 0, n).Trim();
                if (banner.StartsWith("SSH-"))
                {
                    int nl = banner.IndexOfAny(['\r', '\n']);
                    device.SshBanner = nl > 0 ? banner[..nl] : banner;
                }
            }
            catch { }
        }

        /// <summary>
        /// Pull TLS certificate Subject from the first responsive TLS port.
        /// </summary>
        private static async Task ProbeTlsCertAsync(NetworkDevice device, IPAddress addr, bool deep = false)
        {
            int[] standardTls = [443, 8443, 8006, 902, 5001];

            // Normal scan: the known TLS ports only. Deep rescan: attempt a TLS handshake on every
            // open port (a non-TLS port just fails fast), so certs on non-standard ports are caught.
            int[] tlsPorts = deep
                ? [.. standardTls.Where(device.OpenPorts.Contains)
                    .Concat(device.OpenPorts.Where(p => !standardTls.Contains(p)).Take(12))]
                : standardTls;
            foreach (var port in tlsPorts)
            {
                if (!device.OpenPorts.Contains(port)) continue;
                try
                {
                    using var tcp = new TcpClient();
                    var connect = tcp.ConnectAsync(addr, port);
                    if (await Task.WhenAny(connect, Task.Delay(1500)) != connect || !tcp.Connected)
                        continue;
                    using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                    var auth = ssl.AuthenticateAsClientAsync(addr.ToString(), null, SslProtocols.None, false);
                    if (await Task.WhenAny(auth, Task.Delay(1500)) != auth) continue;
                    if (ssl.RemoteCertificate != null)
                    {
                        device.TlsSubject = ssl.RemoteCertificate.Subject;
                        return;
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// UDP NetBIOS name service query (NBSTAT, port 137). Returns the machine's
        /// NetBIOS name for any Windows host that answers NBT, even when SMB is locked down.
        /// </summary>
        private static async Task ProbeNetbiosAsync(NetworkDevice device, IPAddress addr, ScanRun? run = null)
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

            try
            {
                run?.CheckTarget(addr);
                using var udp = run?.Udp() ?? new UdpClient();
                udp.Client.SendTimeout = 500;
                udp.Client.ReceiveTimeout = 800;
                await udp.SendAsync(query, query.Length, new IPEndPoint(addr, 137));

                var recvTask = ReceiveProbeAsync(udp, addr, 137, run);
                if (await Task.WhenAny(recvTask, Task.Delay(800)) != recvTask) return;
                var resp = recvTask.Result.Buffer;
                if (resp.Length < 57) return;

                int numNames = resp[56];
                for (int i = 0; i < numNames && 57 + (i * 18) + 18 <= resp.Length; i++)
                {
                    int off = 57 + (i * 18);
                    byte suffix = resp[off + 15];
                    if (suffix == 0x00 || suffix == 0x20)
                    {
                        string name = Encoding.ASCII.GetString(resp, off, 15).TrimEnd(' ', '\0');
                        if (!string.IsNullOrWhiteSpace(name) && !name.Contains('\x01') && !name.Contains('\x02'))
                        {
                            device.NetbiosName = name;
                            return;
                        }
                    }
                }
            }
            catch (NetworkBindingException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        /// <summary>
        /// SNMPv1 GET of sysDescr.0 (1.3.6.1.2.1.1.1.0) with community "public".
        /// Works on most network gear, printers, and UPSes that expose SNMP.
        /// </summary>
        private static async Task ProbeSnmpAsync(NetworkDevice device, IPAddress addr, ScanRun? run = null)
        {
            // Precomputed SNMPv1 GetRequest for sysDescr.0, community "public".
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

            try
            {
                run?.CheckTarget(addr);
                using var udp = run?.Udp() ?? new UdpClient();
                udp.Client.SendTimeout = 500;
                udp.Client.ReceiveTimeout = 1000;
                await udp.SendAsync(query, query.Length, new IPEndPoint(addr, 161));

                var recvTask = ReceiveProbeAsync(udp, addr, 161, run);
                if (await Task.WhenAny(recvTask, Task.Delay(1000)) != recvTask) return;
                var resp = recvTask.Result.Buffer;
                if (resp.Length < 30) return;

                // The response's last OCTET STRING tag is sysDescr. Walk backward for
                // a 0x04 tag whose payload is long enough to plausibly be a description
                // and whose content doesn't match "public" (the community echo).
                for (int i = resp.Length - 2; i >= 0; i--)
                {
                    if (resp[i] != 0x04) continue;
                    int len = resp[i + 1];
                    if (len < 5 || i + 2 + len > resp.Length) continue;
                    string s = Encoding.UTF8.GetString(resp, i + 2, len);
                    if (s.Equals("public", StringComparison.Ordinal)) continue;
                    if (s.Any(c => c < 0x20 && c != '\r' && c != '\n' && c != '\t')) continue;
                    device.SnmpDescr = s.Trim();
                    return;
                }
            }
            catch (NetworkBindingException) { throw; }
            catch (OperationCanceledException) { throw; }
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
                    using var client = new TcpClient();
                    var connect = client.ConnectAsync(addr, port);
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
        private static string GetMacAddress(IPAddress addr) => NetworkPlatform.Current.ResolveMac(addr);
    }
}
