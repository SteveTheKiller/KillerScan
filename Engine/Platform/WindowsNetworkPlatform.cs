using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KillerScan.Engine
{
    /// <summary>Windows implementation: arp.exe, iphlpapi and dnsapi.</summary>
#if NET
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
    public sealed class WindowsNetworkPlatform : INetworkPlatform
    {
        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        private static extern int SendARP(int destIp, int srcIp, byte[] macAddr, ref int macLen);

        [DllImport("dnsapi.dll", ExactSpelling = true)]
        private static extern bool DnsFlushResolverCache();

        [StructLayout(LayoutKind.Sequential)]
        private struct ForwardRow
        {
            public uint Destination, Mask, Policy, NextHop, InterfaceIndex, Type, Protocol,
                Age, NextHopAs, Metric1, Metric2, Metric3, Metric4, Metric5;
        }

        [DllImport("iphlpapi.dll")]
        private static extern uint GetBestRoute(uint destination, uint source, out ForwardRow route);

        /// <summary>Reads the system ARP cache via arp -a.</summary>
        public IReadOnlyDictionary<string, string> ReadNeighborCache()
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
                return ArpOutput.Parse(output);
            }
            catch { }
            return cache;
        }

        public string ResolveMac(IPAddress address)
        {
            try
            {
                byte[] mac = new byte[6];
                int macLen = mac.Length;
                int ipInt = BitConverter.ToInt32(address.GetAddressBytes(), 0);
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

        public void FlushDnsCache()
        {
            try { DnsFlushResolverCache(); }
            catch { }
        }

        public (string Interface, string NextHop)? BestRoute(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return null;
            if (GetBestRoute(BitConverter.ToUInt32(address.GetAddressBytes(), 0), 0, out var route) != 0) return null;
            var iface = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            {
                try { return n.GetIPProperties().GetIPv4Properties()?.Index == route.InterfaceIndex; }
                catch (NetworkInformationException) { return false; }
            });
            return (iface?.Name ?? route.InterfaceIndex.ToString(),
                route.NextHop == 0 ? string.Empty : new IPAddress(route.NextHop).ToString());
        }
    }

    /// <summary>Parses arp -a output lines such as "192.168.1.1  00-00-5e-00-53-01  dynamic".</summary>
    internal static class ArpOutput
    {
        internal static Dictionary<string, string> Parse(string output)
        {
            var cache = new Dictionary<string, string>();
            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;

                var parts = trimmed.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    string ip = parts[0];
                    string mac = parts[1].Replace('-', ':').ToUpperInvariant();
                    if (IPAddress.TryParse(ip, out _) && mac.Length == 17 && mac.Contains(':'))
                        cache[ip] = mac;
                }
            }
            return cache;
        }
    }
}
