using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using KillerScan.Models;

namespace KillerScan.Services
{
    public class NetworkScanner
    {
        // Common ports to probe for device type detection
        private static readonly int[] ProbePorts = {
            22,    // SSH
            53,    // DNS
            80,    // HTTP
            443,   // HTTPS
            445,   // SMB
            515,   // LPR printing
            631,   // IPP (printers)
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
        };

        // ARP table import for MAC address resolution
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int macLen);

        public event Action<string>? StatusChanged;
        public event Action<int>? ProgressChanged;
        public event Action<NetworkDevice>? DeviceFound;

        /// <summary>
        /// Parse a CIDR subnet string into a list of IP addresses.
        /// </summary>
        public static List<IPAddress> GetAddressesInSubnet(string cidr)
        {
            var parts = cidr.Trim().Split('/');
            var ip = IPAddress.Parse(parts[0]);
            int prefixLen = parts.Length > 1 ? int.Parse(parts[1]) : 24;

            byte[] ipBytes = ip.GetAddressBytes();
            uint ipUint = (uint)(ipBytes[0] << 24 | ipBytes[1] << 16 | ipBytes[2] << 8 | ipBytes[3]);

            uint mask = prefixLen == 0 ? 0 : uint.MaxValue << (32 - prefixLen);
            uint network = ipUint & mask;
            uint broadcast = network | ~mask;

            var addresses = new List<IPAddress>();
            for (uint addr = network + 1; addr < broadcast; addr++)
            {
                addresses.Add(new IPAddress(new byte[] {
                    (byte)(addr >> 24), (byte)(addr >> 16),
                    (byte)(addr >> 8), (byte)addr
                }));
            }
            return addresses;
        }

        /// <summary>
        /// Scan all hosts in the given subnet using ARP + ping combined approach.
        /// </summary>
        public async Task<List<NetworkDevice>> ScanSubnetAsync(string cidr, CancellationToken ct, bool fullScan = true)
        {
            var addresses = GetAddressesInSubnet(cidr);
            var devices = new List<NetworkDevice>();
            int completed = 0;
            int total = addresses.Count;

            // Phase 1: Fast ping sweep + ARP cache
            StatusChanged?.Invoke($"Discovering hosts on {cidr}...");
            var discoveredHosts = new System.Collections.Concurrent.ConcurrentDictionary<string, (IPAddress Addr, string Mac)>();

            // Grab existing ARP cache first (instant, catches IoT devices)
            var arpCache = GetArpCache();
            var addressSet = new HashSet<string>(addresses.Select(a => a.ToString()));
            foreach (var entry in arpCache)
            {
                if (addressSet.Contains(entry.Key))
                    discoveredHosts.TryAdd(entry.Key, (IPAddress.Parse(entry.Key), entry.Value));
            }

            // Fast parallel ping sweep (async, no blocking ARP calls)
            var semaphore = new SemaphoreSlim(200);
            var scanTasks = addresses.Select(async addr =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(addr, 500);
                    if (reply.Status == IPStatus.Success)
                        discoveredHosts.TryAdd(addr.ToString(), (addr, ""));
                }
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

            // Resolve MAC addresses via ARP for discovered hosts (fast, they're alive)
            StatusChanged?.Invoke($"Resolving {discoveredHosts.Count} MAC addresses...");
            var macTasks = discoveredHosts.Keys.ToList().Select(async ip =>
            {
                var addr = IPAddress.Parse(ip);
                string mac = discoveredHosts[ip].Mac;
                if (string.IsNullOrEmpty(mac))
                    mac = await Task.Run(() => GetMacAddress(addr));
                discoveredHosts[ip] = (addr, mac);
            });
            await Task.WhenAll(macTasks);
            ct.ThrowIfCancellationRequested();

            // Phase 2: Probe discovered hosts for details (parallel, throttled)
            var sortedHosts = discoveredHosts.Values
                .OrderBy(h => BitConverter.ToUInt32(h.Addr.GetAddressBytes().Reverse().ToArray(), 0))
                .ToList();
            completed = 0;
            total = sortedHosts.Count;

            if (fullScan)
            {
                StatusChanged?.Invoke($"Probing {total} alive hosts...");
                var probeSemaphore = new SemaphoreSlim(20);
                var probeTasks = sortedHosts.Select(async entry =>
                {
                    await probeSemaphore.WaitAsync(ct);
                    try
                    {
                        var device = await ProbeHostAsync(entry.Addr, entry.Mac);
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
                StatusChanged?.Invoke($"Resolving {total} hosts...");
                var quickSemaphore = new SemaphoreSlim(20);
                var quickTasks = sortedHosts.Select(async entry =>
                {
                    await quickSemaphore.WaitAsync(ct);
                    try
                    {
                        var device = new NetworkDevice
                        {
                            IpAddress = entry.Addr.ToString(),
                            MacAddress = entry.Mac
                        };

                        try
                        {
                            var dns = await Dns.GetHostEntryAsync(entry.Addr);
                            device.Hostname = dns.HostName;
                        }
                        catch { }

                        if (!string.IsNullOrEmpty(entry.Mac))
                            device.Vendor = OuiLookup.GetVendor(entry.Mac);

                        // Classify even in quick scan (hostname + OUI, no ports)
                        device.DeviceType = ClassifyDevice(device);

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

            StatusChanged?.Invoke($"Scan complete -- {devices.Count} devices found");
            ProgressChanged?.Invoke(100);
            return devices;
        }

        /// <summary>
        /// Read the system ARP cache via arp -a.
        /// </summary>
        private static Dictionary<string, string> GetArpCache()
        {
            var cache = new Dictionary<string, string>();
            try
            {
                var psi = new ProcessStartInfo("arp", "-a")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc == null) return cache;

                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(3000);

                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed)) continue;

                    // Parse lines like: 192.168.8.1     94-83-c4-a4-78-82     dynamic
                    var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        string ip = parts[0];
                        string mac = parts[1].Replace('-', ':').ToUpperInvariant();
                        if (IPAddress.TryParse(ip, out _) && mac.Length == 17 && mac.Contains(':'))
                            cache[ip] = mac;
                    }
                }
            }
            catch { }
            return cache;
        }

        /// <summary>
        /// Probe a single host for hostname, open ports, fingerprints, and device type.
        /// </summary>
        private async Task<NetworkDevice> ProbeHostAsync(IPAddress addr, string cachedMac)
        {
            var device = new NetworkDevice
            {
                IpAddress = addr.ToString(),
                MacAddress = cachedMac
            };

            // Resolve hostname + capture TTL (OS family hint) in parallel with port scan.
            var dnsTask = Task.Run(async () =>
            {
                try
                {
                    var entry = await Dns.GetHostEntryAsync(addr);
                    device.Hostname = entry.HostName;
                }
                catch { }
            });

            var ttlTask = Task.Run(async () =>
            {
                try
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(addr, 400);
                    if (reply.Status == IPStatus.Success && reply.Options != null)
                        device.Ttl = reply.Options.Ttl;
                }
                catch { }
            });

            // Port scan (parallel, short timeout)
            var portTasks = ProbePorts.Select(async port =>
            {
                try
                {
                    using var client = new TcpClient();
                    var connectTask = client.ConnectAsync(addr, port);
                    if (await Task.WhenAny(connectTask, Task.Delay(200)) == connectTask
                        && client.Connected)
                    {
                        return port;
                    }
                }
                catch { }
                return -1;
            });

            var results = await Task.WhenAll(portTasks);
            device.OpenPorts = results.Where(p => p > 0).OrderBy(p => p).ToList();

            // Wait for hostname + TTL probes to finish before fingerprinting.
            await Task.WhenAll(dnsTask, ttlTask);

            // Look up vendor from MAC OUI (used by later probes + classifier).
            if (!string.IsNullOrEmpty(device.MacAddress))
                device.Vendor = OuiLookup.GetVendor(device.MacAddress);

            // -- Fingerprint probes: run in parallel, each is gated on relevant open ports --
            var fpTasks = new List<Task>();
            if (device.OpenPorts.Any(p => p == 80 || p == 443 || p == 8006 || p == 8080 || p == 8443 || p == 5000 || p == 5001 || p == 8123))
                fpTasks.Add(ProbeHttpAsync(device, addr));
            if (device.OpenPorts.Contains(22))
                fpTasks.Add(ProbeSshBannerAsync(device, addr));
            if (device.OpenPorts.Any(p => p == 443 || p == 8443 || p == 8006 || p == 902))
                fpTasks.Add(ProbeTlsCertAsync(device, addr));
            // NetBIOS + SNMP are UDP -- we probe regardless of TCP state (ports 137/161 UDP).
            fpTasks.Add(ProbeNetbiosAsync(device, addr));
            fpTasks.Add(ProbeSnmpAsync(device, addr));

            await Task.WhenAll(fpTasks);

            // Classify device type using weighted scoring over all signals.
            device.DeviceType = ClassifyDevice(device);

            return device;
        }

        // -------------------------------------------------------------------
        // Hostname keyword rules: checked BEFORE OUI/port classification.
        // Each entry maps a hostname substring (lowercase) to a device type.
        // -------------------------------------------------------------------
        private static readonly (string Pattern, string Type)[] HostnameKeywords =
        {
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
        };

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

        /// <summary>
        /// Weighted-score classifier. Each signal contributes points to candidate
        /// device types; the highest-scoring type above the threshold wins.
        /// Hostname and manual overrides short-circuit before scoring.
        ///
        /// Signal strength rough guide:
        ///   15+  strong positive (protocol banner, TLS cert, specific HTTP title)
        ///   8-12 vendor + port combo, unique signature (e.g. port 8006 = Proxmox)
        ///   4-7  weak supporting signal (TTL, generic port presence)
        ///   1-3  tie-breaker only
        /// </summary>
        public static string ClassifyDevice(NetworkDevice device)
        {
            // 1. Manual override always wins.
            var manual = DeviceOverrides.Get(device.MacAddress);
            if (manual != null)
                return manual;

            var ports = device.OpenPorts;
            string host = device.Hostname.ToLowerInvariant();
            string title = device.HttpTitle.ToLowerInvariant();
            string server = device.HttpServer.ToLowerInvariant();
            string tls = device.TlsSubject.ToLowerInvariant();
            string ssh = device.SshBanner.ToLowerInvariant();
            string snmp = device.SnmpDescr.ToLowerInvariant();
            string nbName = device.NetbiosName.ToLowerInvariant();

            // 2. Hostname keyword short-circuit (explicit user-set suffix beats scoring).
            foreach (var (pattern, type) in HostnameKeywords)
            {
                if (host.Contains(pattern))
                    return type;
            }

            // 3. OUI vendor with known-bad corrections.
            string vendor = device.Vendor.ToLowerInvariant();
            if (!string.IsNullOrEmpty(device.MacAddress) && device.MacAddress.Length >= 8)
            {
                string prefix = device.MacAddress[..8].ToUpperInvariant();
                if (OuiBadMap.TryGetValue(prefix, out var corrected))
                    vendor = corrected.ToLowerInvariant();
            }

            // 4. Score candidate types from all signals.
            var scores = new Dictionary<string, int>();
            void Add(string type, int s)
            {
                if (scores.ContainsKey(type))
                    scores[type] += s;
                else
                    scores[type] = s;
            }

            bool hasWorkstationPorts = ports.Contains(3389) || ports.Contains(445);

            // -- Hypervisor --
            if (ports.Contains(8006)) Add("Hypervisor", 15);
            if (title.Contains("proxmox")) Add("Hypervisor", 15);
            if (tls.Contains("vmware") || title.Contains("vmware esxi")) Add("Hypervisor", 15);
            if (ports.Contains(902) && ports.Contains(443) && !hasWorkstationPorts) Add("Hypervisor", 10);
            if (ports.Contains(2179) && (ports.Contains(5985) || ports.Contains(5986)) && !hasWorkstationPorts) Add("Hypervisor", 8);
            if (tls.Contains("xenserver") || title.Contains("xenserver")) Add("Hypervisor", 15);

            // -- Windows workstation --
            if (ports.Contains(3389) && ports.Contains(445)) Add("Windows", 6);
            if (server.Contains("microsoft-iis")) Add("Windows", 6);
            if (!string.IsNullOrEmpty(nbName) && ports.Contains(445)) Add("Windows", 4);
            if (device.Ttl is >= 120 and <= 128 && ports.Contains(445)) Add("Windows", 3);

            // -- Windows Server --
            if (ports.Contains(3389) && ports.Contains(445) && (ports.Contains(80) || ports.Contains(443) || ports.Contains(53)))
                Add("Windows Server", 6);
            if (title.Contains("exchange") || server.Contains("exchange")) Add("Windows Server", 10);
            if (snmp.Contains("windows server")) Add("Windows Server", 15);

            // -- Linux / SSH --
            // Windows boxes with OpenSSH Server advertise "OpenSSH_for_Windows" -- score Windows, not Linux.
            bool sshIsWindows = ssh.Contains("for_windows");
            if (sshIsWindows) Add("Windows", 15);
            if (!sshIsWindows && (ssh.StartsWith("ssh-2.0-openssh") || ssh.StartsWith("ssh-1.99-openssh"))) Add("Linux/SSH", 8);
            if (device.Ttl is >= 60 and <= 64 && ports.Contains(22) && !sshIsWindows) Add("Linux/SSH", 3);

            // -- Network gear (switches, routers, firewalls, APs) --
            bool isNetworkVendor = vendor.Contains("cisco") || vendor.Contains("ubiquiti")
                || vendor.Contains("aruba") || vendor.Contains("ruckus") || vendor.Contains("meraki")
                || vendor.Contains("netgear") || vendor.Contains("tp-link") || vendor.Contains("fortinet")
                || vendor.Contains("juniper") || vendor.Contains("mikrotik") || vendor.Contains("gl technologies")
                || vendor.Contains("gl.inet") || vendor.Contains("draytek") || vendor.Contains("zyxel")
                || vendor.Contains("linksys") || vendor.Contains("sonicwall") || vendor.Contains("watchguard");
            if (isNetworkVendor) Add("Network", 8);
            if (ssh.Contains("cisco") || ssh.Contains("routeros") || ssh.Contains("mikrotik")) Add("Network", 15);
            if (title.Contains("unifi") || title.Contains("fortigate") || title.Contains("sonicwall")
                || title.Contains("pfsense") || title.Contains("opnsense") || title.Contains("mikrotik")) Add("Network", 15);
            if (snmp.Contains("cisco ios") || snmp.Contains("juniper") || snmp.Contains("fortigate")) Add("Network", 12);

            // Router vs Switch/AP disambiguation within network gear.
            if (isNetworkVendor && ports.Contains(53)) Add("Router", 10);
            if (isNetworkVendor && ports.Contains(161) && !ports.Contains(53)) Add("Switch/AP", 8);

            // -- Printer --
            bool isPrinterVendor = vendor.Contains("canon") || vendor.Contains("epson")
                || vendor.Contains("brother") || vendor.Contains("xerox") || vendor.Contains("lexmark")
                || vendor.Contains("ricoh") || vendor.Contains("konica") || vendor.Contains("kyocera");
            // HP vendor alone is ambiguous (laptops, servers, printers). Only trust with printer ports.
            if (ports.Contains(9100) || ports.Contains(515) || ports.Contains(631)) Add("Printer", 8);
            if (isPrinterVendor && (ports.Contains(9100) || ports.Contains(515) || ports.Contains(631))) Add("Printer", 10);
            if (isPrinterVendor && ports.Count <= 3) Add("Printer", 6);
            if (vendor.Contains("hewlett packard") && ports.Contains(9100)) Add("Printer", 12);
            if (snmp.Contains("laserjet") || snmp.Contains("officejet") || snmp.Contains("printer")) Add("Printer", 15);
            if (title.Contains("embedded web server") || title.Contains("web image monitor")) Add("Printer", 10);

            // -- NAS --
            if (vendor.Contains("synology") || vendor.Contains("qnap") || vendor.Contains("asustor")
                || vendor.Contains("drobo") || vendor.Contains("buffalo") || vendor.Contains("terramaster"))
                Add("NAS", 12);
            if (title.Contains("diskstation") || title.Contains("synology") || title.Contains("dsm "))
                Add("NAS", 15);
            if (title.Contains("qts") || title.Contains("qnap") || title.Contains("truenas") || title.Contains("freenas"))
                Add("NAS", 15);
            if (ports.Contains(548)) Add("NAS", 4);

            // -- Apple device --
            bool isApple = vendor.Contains("apple");
            if (isApple && ports.Contains(62078)) Add("Apple Device", 12);
            if (isApple && ports.Count <= 2) Add("Apple Device", 6);
            if (device.MdnsServices.Any(s => s.Contains("_airplay") || s.Contains("_raop") || s.Contains("_airport")))
                Add("Apple Device", 10);

            // -- Mobile (phones, tablets) --
            bool isMobileVendor = vendor.Contains("samsung") || vendor.Contains("oneplus")
                || vendor.Contains("xiaomi") || vendor.Contains("huawei") || vendor.Contains("motorola")
                || vendor.Contains("oppo") || vendor.Contains("vivo") || vendor.Contains("zte")
                || vendor.Contains("lg electronics");
            if (isMobileVendor && ports.Count <= 2) Add("Mobile", 8);

            // -- Surveillance camera --
            if (vendor.Contains("hikvision") || vendor.Contains("dahua") || vendor.Contains("axis")
                || vendor.Contains("amcrest") || vendor.Contains("reolink") || vendor.Contains("foscam"))
                Add("Camera", 12);
            if (title.Contains("hikvision") || title.Contains("dahua") || title.Contains("camera")
                || title.Contains("dvr") || title.Contains("nvr") || title.Contains("ipcam"))
                Add("Camera", 12);
            if (ports.Contains(554)) Add("Camera", 4);

            // -- IoT / smart home --
            if (vendor.Contains("espressif") || vendor.Contains("tuya") || vendor.Contains("sonoff")
                || vendor.Contains("shelly") || vendor.Contains("nest") || vendor.Contains("ecobee")
                || vendor.Contains("signify") || vendor.Contains("lutron") || vendor.Contains("wemo")
                || vendor.Contains("wyze") || vendor.Contains("aqara") || vendor.Contains("linkplay")
                || vendor.Contains("wiim"))
                Add("IoT", 10);

            // -- Home Assistant --
            if (ports.Contains(8123)) Add("Home Assistant", 12);
            if (title.Contains("home assistant") || host.Contains("homeassistant") || host.Contains("home-assistant"))
                Add("Home Assistant", 15);

            // -- DNS server (Pi-hole, AdGuard, BIND) --
            if (title.Contains("pi-hole") || title.Contains("pihole")) Add("DNS Server", 15);
            if (title.Contains("adguard")) Add("DNS Server", 15);
            if (ports.Contains(53) && ports.Contains(80) && !isNetworkVendor) Add("DNS Server", 6);

            // -- Generic web device (catch-all) --
            if (ports.Contains(80) || ports.Contains(443) || ports.Contains(8080)) Add("Web Device", 2);

            // Pick highest-scoring candidate above threshold.
            if (scores.Count > 0)
            {
                var winner = scores.OrderByDescending(kvp => kvp.Value).First();
                if (winner.Value >= 6)
                    return winner.Key;
            }

            // Randomized / locally-administered MAC with no responsive ports is almost
            // always a phone or tablet using iOS/Android private Wi-Fi addressing.
            bool localAdminMac = false;
            if (!string.IsNullOrEmpty(device.MacAddress) && device.MacAddress.Length >= 2
                && byte.TryParse(device.MacAddress[..2], System.Globalization.NumberStyles.HexNumber, null, out var firstByte))
            {
                localAdminMac = (firstByte & 0x02) != 0;
            }
            if (localAdminMac && ports.Count == 0) return "Mobile";

            // Fallback heuristics when no candidate cleared threshold.
            if (ports.Contains(22)) return "Linux/SSH";
            if (ports.Contains(445) || ports.Contains(3389)) return "Windows";
            if (ports.Contains(80) || ports.Contains(443)) return "Web Device";
            // Responds to ARP/ping but exposes zero TCP ports -- almost always a
            // smart bulb, plug, sensor, or other IoT endpoint. Even with a blank
            // OUI (Govee and similar use unregistered prefixes), IoT beats Unknown.
            if (ports.Count == 0 && !string.IsNullOrEmpty(device.MacAddress)) return "IoT";
            if (ports.Count == 0) return "Unknown";
            return "Other";
        }

        // ===================================================================
        // Active fingerprint probes. Each populates a field on NetworkDevice;
        // failures are swallowed -- missing data just means weaker scoring.
        // ===================================================================

        /// <summary>
        /// Fetch HTTP title + Server header from the first responsive web port.
        /// </summary>
        private static async Task ProbeHttpAsync(NetworkDevice device, IPAddress addr)
        {
            (int port, bool https)[] candidates =
            {
                (80, false), (8080, false), (5000, false), (8123, false),
                (443, true), (8443, true), (8006, true), (5001, true),
            };

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 2,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(1500) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("KillerScan/1.2");

            foreach (var (port, https) in candidates)
            {
                if (!device.OpenPorts.Contains(port)) continue;
                try
                {
                    var scheme = https ? "https" : "http";
                    using var resp = await client.GetAsync($"{scheme}://{addr}:{port}/");

                    if (resp.Headers.TryGetValues("Server", out var serverVals))
                        device.HttpServer = string.Join(", ", serverVals).Trim();

                    var body = await resp.Content.ReadAsStringAsync();
                    var m = Regex.Match(body, @"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase);
                    if (m.Success)
                        device.HttpTitle = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();

                    if (device.HttpTitle.Length > 0 || device.HttpServer.Length > 0)
                        return;
                }
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
                    int nl = banner.IndexOfAny(new[] { '\r', '\n' });
                    device.SshBanner = nl > 0 ? banner[..nl] : banner;
                }
            }
            catch { }
        }

        /// <summary>
        /// Pull TLS certificate Subject from the first responsive TLS port.
        /// </summary>
        private static async Task ProbeTlsCertAsync(NetworkDevice device, IPAddress addr)
        {
            int[] tlsPorts = { 443, 8443, 8006, 902, 5001 };
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
        private static async Task ProbeNetbiosAsync(NetworkDevice device, IPAddress addr)
        {
            // NBSTAT query for the wildcard name "*" (encoded as 32-byte level-2 name).
            byte[] query =
            {
                0x00, 0x00, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x20,
                0x43, 0x4B, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
                0x00,
                0x00, 0x21,
                0x00, 0x01,
            };

            try
            {
                using var udp = new UdpClient();
                udp.Client.SendTimeout = 500;
                udp.Client.ReceiveTimeout = 800;
                await udp.SendAsync(query, query.Length, new IPEndPoint(addr, 137));

                var recvTask = udp.ReceiveAsync();
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
            catch { }
        }

        /// <summary>
        /// SNMPv1 GET of sysDescr.0 (1.3.6.1.2.1.1.1.0) with community "public".
        /// Works on most network gear, printers, and UPSes that expose SNMP.
        /// </summary>
        private static async Task ProbeSnmpAsync(NetworkDevice device, IPAddress addr)
        {
            // Precomputed SNMPv1 GetRequest for sysDescr.0, community "public".
            byte[] query =
            {
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
            };

            try
            {
                using var udp = new UdpClient();
                udp.Client.SendTimeout = 500;
                udp.Client.ReceiveTimeout = 1000;
                await udp.SendAsync(query, query.Length, new IPEndPoint(addr, 161));

                var recvTask = udp.ReceiveAsync();
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
            catch { }
        }

        /// <summary>
        /// Get MAC address of a host using ARP.
        /// </summary>
        private static string GetMacAddress(IPAddress addr)
        {
            try
            {
                byte[] mac = new byte[6];
                int macLen = mac.Length;
                int ipInt = BitConverter.ToInt32(addr.GetAddressBytes(), 0);
                int result = SendARP(ipInt, 0, mac, ref macLen);
                if (result == 0)
                {
                    string macStr = string.Join(":", mac.Select(b => b.ToString("X2")));
                    if (macStr != "00:00:00:00:00:00")
                        return macStr;
                }
            }
            catch { }
            return string.Empty;
        }
    }
}
