using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KillerScan.Engine
{
    // Network-wide service discovery, run once per scan. mDNS (Bonjour) and SSDP (UPnP) are
    // multicast, so a few queries reach the whole LAN at once; responses are mapped back to hosts
    // by source IP. Everything is best-effort and wrapped in try/catch - a parse error or a blocked
    // socket just yields no data, never a crash.
    public static class MulticastDiscovery
    {
        public sealed class MdnsInfo
        {
            public string Name = "";                 // device's .local name (for the Hostname fallback)
            public readonly HashSet<string> Services = new(StringComparer.OrdinalIgnoreCase);
        }

        // Service types we explicitly ask about (responders also volunteer extras).
        private static readonly string[] MdnsQueries =
        [
            "_services._dns-sd._udp.local",
            "_googlecast._tcp.local", "_airplay._tcp.local", "_raop._tcp.local",
            "_ipp._tcp.local", "_ipps._tcp.local", "_printer._tcp.local", "_pdl-datastream._tcp.local",
            "_sonos._tcp.local", "_spotify-connect._tcp.local", "_hap._tcp.local",
            "_homekit._tcp.local", "_hue._tcp.local", "_companion-link._tcp.local",
            "_device-info._tcp.local", "_smb._tcp.local", "_afpovertcp._tcp.local",
        ];

        /// <summary>Multicast-query mDNS, collect responses for ~listenMs, map source IP -> services + name.</summary>
        // Sends this socket's multicast queries out of the adapter that owns the address.
        private static void SendOn(UdpClient udp, IPAddress local)
        {
            try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes()); }
            catch (SocketException) { }
        }

        public static async Task<Dictionary<string, MdnsInfo>> CollectMdnsAsync(CancellationToken ct, int listenMs = 1500)
        {
            var map = new Dictionary<string, MdnsInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
                var group = IPAddress.Parse("224.0.0.251");
                // A bound scan asks and listens on its own adapter only.
                if (ScanSource.Current is { } binding)
                {
                    SendOn(udp, binding.LocalAddress);
                    try { udp.JoinMulticastGroup(group, binding.LocalAddress); } catch { }
                }
                else try { udp.JoinMulticastGroup(group); } catch { }
                var dest = new IPEndPoint(group, 5353);

                foreach (var q in MdnsQueries)
                {
                    var packet = BuildQuery(q);
                    try { await udp.SendAsync(packet, packet.Length, dest); } catch { }
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(listenMs);
                var recv = udp.ReceiveAsync().ObserveFault();
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    if (await Task.WhenAny(recv, Task.Delay(300)) != recv) continue;
                    UdpReceiveResult r;
                    try { r = recv.Result; } catch { break; }
                    // The socket listens on every adapter; a bound scan keeps only its own network's replies.
                    if (ScanSource.Current is not { } bound || bound.Contains(r.RemoteEndPoint.Address))
                        try { ParseResponse(r.Buffer, r.RemoteEndPoint.Address.ToString(), map); } catch { }
                    recv = udp.ReceiveAsync().ObserveFault();
                }
            }
            catch { }
            return map;
        }

        /// <summary>SSDP M-SEARCH, collect SERVER headers, map source IP -> server string.</summary>
        public static async Task<Dictionary<string, string>> CollectSsdpAsync(CancellationToken ct, int listenMs = 1200)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                var local = ScanSource.Current?.LocalAddress;
                udp.Client.Bind(new IPEndPoint(local ?? IPAddress.Any, 0));
                if (local != null) SendOn(udp, local);
                var dest = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                var msearch = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: ssdp:all\r\n\r\n");
                try { await udp.SendAsync(msearch, msearch.Length, dest); } catch { }
                try { await udp.SendAsync(msearch, msearch.Length, dest); } catch { }

                var deadline = DateTime.UtcNow.AddMilliseconds(listenMs);
                var recv = udp.ReceiveAsync().ObserveFault();
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    if (await Task.WhenAny(recv, Task.Delay(300)) != recv) continue;
                    UdpReceiveResult r;
                    try { r = recv.Result; } catch { break; }
                    recv = udp.ReceiveAsync().ObserveFault();
                    string ip = r.RemoteEndPoint.Address.ToString();
                    if (map.ContainsKey(ip)) continue;
                    string text = Encoding.ASCII.GetString(r.Buffer);
                    string server = ExtractHeader(text, "SERVER");
                    if (string.IsNullOrEmpty(server)) server = ExtractHeader(text, "USN");
                    if (!string.IsNullOrEmpty(server)) map[ip] = server.Trim();
                }
            }
            catch { }
            return map;
        }

        private static string ExtractHeader(string text, string header)
        {
            foreach (var line in text.Split('\n'))
            {
                int c = line.IndexOf(':');
                if (c > 0 && line[..c].Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
                    return line[(c + 1)..].Trim();
            }
            return "";
        }

        // ---- minimal mDNS/DNS packet building + parsing ----

        private static byte[] BuildQuery(string name)
        {
            using var ms = new MemoryStream();
            ms.Write([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0], 0, 12); // id+flags, qd=1
            foreach (var label in name.Split('.'))
            {
                var lb = Encoding.UTF8.GetBytes(label);
                ms.WriteByte((byte)lb.Length);
                ms.Write(lb, 0, lb.Length);
            }
            ms.WriteByte(0);
            ms.Write([0, 12, 0, 1], 0, 4); // QTYPE=PTR, QCLASS=IN
            return ms.ToArray();
        }

        /// <summary>
        /// Adds one mDNS packet to <paramref name="map"/>. Only responses count: another host's query
        /// can carry known-answer records about someone else. A packet can describe several hosts,
        /// because a reflector or repeater (UniFi, Avahi, a Bonjour gateway) relays other devices'
        /// answers, so each service goes to the host its SRV record names, at the address that
        /// host's A record gives. A record that names no host belongs to the sender when the sender
        /// is the host in question, to the packet's one host when it describes one, and otherwise
        /// to nobody: it is dropped rather than guessed onto a device.
        /// </summary>
        internal static void ParseResponse(byte[] buf, string srcIp, Dictionary<string, MdnsInfo> map)
        {
            if (buf.Length < 12 || (buf[2] & 0x80) == 0) return;
            int qd = (buf[4] << 8) | buf[5];
            int total = ((buf[6] << 8) | buf[7]) + ((buf[8] << 8) | buf[9]) + ((buf[10] << 8) | buf[11]);
            int pos = 12;
            for (int i = 0; i < qd; i++) { ReadName(buf, ref pos); pos += 4; if (pos > buf.Length) return; }

            // The whole packet is read before anything is attributed, since records come in any order.
            var hosts = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);    // from A records
            var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);        // service instance to host, from SRV records
            var services = new List<(string Service, string Instance)>();
            for (int i = 0; i < total; i++)
            {
                string name = ReadName(buf, ref pos);
                if (pos + 10 > buf.Length) break;
                int type = (buf[pos] << 8) | buf[pos + 1];
                int rdlen = (buf[pos + 8] << 8) | buf[pos + 9];
                pos += 10;
                int rdStart = pos;
                if (rdStart + rdlen > buf.Length) break;

                string lname = name.ToLowerInvariant();
                string instance = lname;                          // the instance this record speaks for

                if (type == 1 && rdlen == 4)                      // A record -> where a host is
                {
                    if (name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                    {
                        string host = name[..^6];
                        if (!hosts.TryGetValue(host, out var known)) hosts[host] = known = [];
                        string address = new IPAddress(new[] { buf[rdStart], buf[rdStart + 1], buf[rdStart + 2], buf[rdStart + 3] }).ToString();
                        if (!known.Contains(address)) known.Add(address);
                    }
                }
                else if (type == 12)                              // PTR -> service instance
                {
                    int p = rdStart;
                    string ptr = ReadName(buf, ref p).ToLowerInvariant();
                    if (IsService(ptr)) { services.Add((ptr, ptr)); instance = ptr; }
                }
                else if (type == 33 && rdlen >= 6)                // SRV -> which host runs the instance
                {
                    int p = rdStart + 6;
                    string target = ReadName(buf, ref p);
                    if (target.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) targets[lname] = target[..^6];
                }
                if (IsService(lname)) services.Add((lname, instance));

                pos = rdStart + rdlen;
            }

            var addresses = hosts.Values.SelectMany(known => known).Distinct().ToList();
            string? sender = addresses.Count == 0 || addresses.Contains(srcIp) ? srcIp : hosts.Count == 1 ? addresses[0] : null;
            string Address(List<string> known) => known.Contains(srcIp) ? srcIp : known[0];
            // An SRV names its host. With no address for that host here, it can only be the sender,
            // and only in a packet that gives no addresses to say otherwise.
            string? Owner(string instance) => !targets.TryGetValue(instance, out var host) ? sender
                : hosts.TryGetValue(host, out var known) ? Address(known) : addresses.Count == 0 ? srcIp : null;
            MdnsInfo Entry(string ip) => map.TryGetValue(ip, out var found) ? found : map[ip] = new MdnsInfo();

            foreach (var (service, instance) in services)
                if (Owner(instance) is { } owner) Entry(owner).Services.Add(service);
            foreach (var host in hosts)
            {
                var info = Entry(Address(host.Value));
                if (info.Name.Length == 0) info.Name = host.Key;
            }
            if (addresses.Count == 0)
                foreach (string host in targets.Values)
                {
                    var info = Entry(srcIp);
                    if (info.Name.Length == 0) info.Name = host;
                }
        }

        private static bool IsService(string name) => name.Contains("._tcp.") || name.Contains("._udp.");

        // Reads a DNS name, following 0xC0 compression pointers; leaves pos just past the name.
        private static string ReadName(byte[] buf, ref int pos)
        {
            var sb = new StringBuilder();
            int safety = 0, returnPos = -1;
            while (pos < buf.Length)
            {
                byte len = buf[pos];
                if (len == 0) { pos++; break; }
                if ((len & 0xC0) == 0xC0)
                {
                    if (pos + 1 >= buf.Length) break;
                    int ptr = ((len & 0x3F) << 8) | buf[pos + 1];
                    if (returnPos < 0) returnPos = pos + 2;
                    pos = ptr;
                    if (++safety > 128) break;
                    continue;
                }
                pos++;
                if (pos + len > buf.Length) break;
                if (sb.Length > 0) sb.Append('.');
                sb.Append(Encoding.UTF8.GetString(buf, pos, len));
                pos += len;
                if (++safety > 128) break;
            }
            if (returnPos >= 0) pos = returnPos;
            return sb.ToString();
        }
    }
}
