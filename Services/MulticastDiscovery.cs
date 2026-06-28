using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KillerScan.Services
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
        {
            "_services._dns-sd._udp.local",
            "_googlecast._tcp.local", "_airplay._tcp.local", "_raop._tcp.local",
            "_ipp._tcp.local", "_ipps._tcp.local", "_printer._tcp.local", "_pdl-datastream._tcp.local",
            "_sonos._tcp.local", "_spotify-connect._tcp.local", "_hap._tcp.local",
            "_homekit._tcp.local", "_hue._tcp.local", "_companion-link._tcp.local",
            "_device-info._tcp.local", "_smb._tcp.local", "_afpovertcp._tcp.local",
        };

        /// <summary>Multicast-query mDNS, collect responses for ~listenMs, map source IP -> services + name.</summary>
        public static async Task<Dictionary<string, MdnsInfo>> CollectMdnsAsync(CancellationToken ct, int listenMs = 1500)
        {
            var map = new Dictionary<string, MdnsInfo>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
                var group = IPAddress.Parse("224.0.0.251");
                try { udp.JoinMulticastGroup(group); } catch { }
                var dest = new IPEndPoint(group, 5353);

                foreach (var q in MdnsQueries)
                {
                    var packet = BuildQuery(q);
                    try { await udp.SendAsync(packet, packet.Length, dest); } catch { }
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(listenMs);
                var recv = udp.ReceiveAsync();
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    if (await Task.WhenAny(recv, Task.Delay(300)) != recv) continue;
                    UdpReceiveResult r;
                    try { r = recv.Result; } catch { break; }
                    try { ParseResponse(r.Buffer, r.RemoteEndPoint.Address.ToString(), map); } catch { }
                    recv = udp.ReceiveAsync();
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
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                var dest = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                var msearch = Encoding.ASCII.GetBytes(
                    "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: ssdp:all\r\n\r\n");
                try { await udp.SendAsync(msearch, msearch.Length, dest); } catch { }
                try { await udp.SendAsync(msearch, msearch.Length, dest); } catch { }

                var deadline = DateTime.UtcNow.AddMilliseconds(listenMs);
                var recv = udp.ReceiveAsync();
                while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
                {
                    if (await Task.WhenAny(recv, Task.Delay(300)) != recv) continue;
                    UdpReceiveResult r;
                    try { r = recv.Result; } catch { break; }
                    recv = udp.ReceiveAsync();
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
                if (c > 0 && line.Substring(0, c).Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(c + 1).Trim();
            }
            return "";
        }

        // ---- minimal mDNS/DNS packet building + parsing ----

        private static byte[] BuildQuery(string name)
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 12); // id+flags, qd=1
            foreach (var label in name.Split('.'))
            {
                var lb = Encoding.UTF8.GetBytes(label);
                ms.WriteByte((byte)lb.Length);
                ms.Write(lb, 0, lb.Length);
            }
            ms.WriteByte(0);
            ms.Write(new byte[] { 0, 12, 0, 1 }, 0, 4); // QTYPE=PTR, QCLASS=IN
            return ms.ToArray();
        }

        private static void ParseResponse(byte[] buf, string srcIp, Dictionary<string, MdnsInfo> map)
        {
            if (buf.Length < 12) return;
            int qd = (buf[4] << 8) | buf[5];
            int total = ((buf[6] << 8) | buf[7]) + ((buf[8] << 8) | buf[9]) + ((buf[10] << 8) | buf[11]);
            int pos = 12;
            for (int i = 0; i < qd; i++) { ReadName(buf, ref pos); pos += 4; if (pos > buf.Length) return; }

            var info = map.TryGetValue(srcIp, out var ex) ? ex : new MdnsInfo();
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
                if (lname.Contains("._tcp.") || lname.Contains("._udp."))
                    info.Services.Add(lname);

                if (type == 1 && rdlen == 4)                      // A record -> hostname
                {
                    if (info.Name.Length == 0 && name.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                        info.Name = name.Substring(0, name.Length - 6);
                }
                else if (type == 12)                              // PTR -> service instance
                {
                    int p = rdStart;
                    string ptr = ReadName(buf, ref p).ToLowerInvariant();
                    if (ptr.Contains("._tcp.") || ptr.Contains("._udp.")) info.Services.Add(ptr);
                }
                else if (type == 33 && rdlen >= 6)                // SRV -> target host
                {
                    int p = rdStart + 6;
                    string target = ReadName(buf, ref p);
                    if (info.Name.Length == 0 && target.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
                        info.Name = target.Substring(0, target.Length - 6);
                }

                pos = rdStart + rdlen;
            }
            map[srcIp] = info;
        }

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
