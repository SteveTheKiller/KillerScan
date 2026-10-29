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

        public WindowsNetworkPlatform()
        {
            // An adapter renamed, or an index reused after a VPN reconnects, would otherwise keep
            // its old name here and make a bound scan refuse a target it can reach.
            NetworkChange.NetworkAddressChanged += (_, _) => _interfaceNames.Clear();
        }

        /// <summary>Reads the system ARP cache via arp -a.</summary>
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => RunArp("-a");

        /// <summary>
        /// The ARP cache of the one adapter with this address (arp -a -N). A bound scan seeds
        /// hosts only from its own adapter, even when another adapter carries the same subnet.
        /// </summary>
        internal IReadOnlyDictionary<string, string> ReadNeighborCacheOn(IPAddress local) => RunArp("-a -N " + local);

        /// <summary>
        /// The arp.exe start info. The full System32 path and working directory matter: a bare name
        /// makes Windows look in the application folder and the current folder first, so a portable
        /// copy run from Downloads would start any arp.exe that happened to sit beside it.
        /// </summary>
        internal static ProcessStartInfo ArpStart(string arguments)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return new ProcessStartInfo(Path.Combine(system, "arp.exe"), arguments)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = system
            };
        }

        private static IReadOnlyDictionary<string, string> RunArp(string arguments)
        {
            try
            {
                using var proc = Process.Start(ArpStart(arguments));
                if (proc == null) return new Dictionary<string, string>();
                // Read asynchronously so the deadline bounds a stalled arp.exe instead of the read.
                var output = proc.StandardOutput.ReadToEndAsync().ObserveFault();
                if (!proc.WaitForExit(3000))
                {
                    try { proc.Kill(); } catch { }
                    return new Dictionary<string, string>();
                }
                return output.Wait(1000) ? ArpOutput.Parse(output.Result) : new Dictionary<string, string>();
            }
            catch { }
            return new Dictionary<string, string>();
        }

        public string ResolveMac(IPAddress address) => ResolveMacFrom(address, null);

        /// <summary>ARP from a given local address, which makes Windows ask on that adapter.</summary>
        internal string ResolveMacFrom(IPAddress address, IPAddress? source)
        {
            try
            {
                byte[] mac = new byte[6];
                int macLen = mac.Length;
                int ipInt = BitConverter.ToInt32(address.GetAddressBytes(), 0);
                int sourceInt = source == null ? 0 : BitConverter.ToInt32(source.GetAddressBytes(), 0);
                int result = SendARP(ipInt, sourceInt, mac, ref macLen);
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
            return (InterfaceName(route.InterfaceIndex),
                route.NextHop == 0 ? string.Empty : new IPAddress(route.NextHop).ToString());
        }

        // A bound scan asks for the route of every target, so adapter names are cached by index
        // and the adapter list is only reread when an index is not in the cache.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> _interfaceNames = new();

        private string InterfaceName(uint index)
        {
            if (_interfaceNames.TryGetValue(index, out var name)) return name;
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    if (adapter.GetIPProperties().GetIPv4Properties() is { } v4) _interfaceNames[(uint)v4.Index] = adapter.Name;
                }
                catch (NetworkInformationException) { }
            }
            return _interfaceNames.TryGetValue(index, out name) ? name : index.ToString();
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
                    // The group bit marks broadcast (FF:FF:...) and multicast (01:00:5E:...) rows,
                    // which arp -a always lists but which are never devices.
                    if (IPAddress.TryParse(ip, out _) && mac.Length == 17 && mac.Contains(':')
                        && int.TryParse(mac.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out int first)
                        && (first & 1) == 0 && mac != "00:00:00:00:00:00")   // an unresolved, "invalid" row
                        cache[ip] = mac;
                }
            }
            return cache;
        }
    }
}
