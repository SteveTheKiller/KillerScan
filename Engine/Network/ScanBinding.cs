using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KillerScan.Engine
{
    /// <summary>
    /// An adapter a scan is bound to: every probe is sent from <see cref="LocalAddress"/>, so it
    /// leaves through that adapter, and targets must be on its own network (on-link). Routed and
    /// VPN targets are refused while bound rather than sent out whichever adapter Windows picks.
    /// Name lookups still use the system resolver, which talks to the DNS server, not the targets.
    /// </summary>
    public sealed record ScanBinding(string InterfaceName, IPAddress LocalAddress, int PrefixLength)
    {
        /// <summary>True when <paramref name="address"/> is on this adapter's network.</summary>
        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork || LocalAddress.AddressFamily != AddressFamily.InterNetwork) return false;
            uint mask = PrefixLength <= 0 ? 0 : uint.MaxValue << (32 - Math.Min(32, PrefixLength));
            return (ToUInt(address) & mask) == (ToUInt(LocalAddress) & mask);
        }

        /// <summary>The adapter's network in CIDR form, such as 192.168.1.0/24.</summary>
        public string Network
        {
            get
            {
                uint mask = PrefixLength <= 0 ? 0 : uint.MaxValue << (32 - Math.Min(32, PrefixLength));
                uint network = ToUInt(LocalAddress) & mask;
                return new IPAddress([(byte)(network >> 24), (byte)(network >> 16), (byte)(network >> 8), (byte)network]) + "/" + PrefixLength;
            }
        }

        private static uint ToUInt(IPAddress address)
        {
            byte[] b = address.GetAddressBytes();
            return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }

        /// <summary>Every adapter that is up and has an IPv4 address, loopback and tunnels excluded.</summary>
        public static IReadOnlyList<ScanBinding> Available()
        {
            var bindings = new List<ScanBinding>();
            NetworkInterface[] adapters;
            try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (NetworkInformationException) { return bindings; }
            foreach (var adapter in adapters)
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties properties;
                try { properties = adapter.GetIPProperties(); }
                catch (NetworkInformationException) { continue; }
                foreach (var unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(unicast.Address)) continue;
                    int prefix = PrefixOf(unicast);
                    if (prefix > 0) bindings.Add(new ScanBinding(adapter.Name, unicast.Address, prefix));
                }
            }
            return bindings;
        }

        /// <summary>The adapter with this Windows name or IPv4 address, or null when none is up.</summary>
        public static ScanBinding? Find(string nameOrAddress)
        {
            string wanted = nameOrAddress.Trim();
            var available = Available();
            return IPAddress.TryParse(wanted, out var address)
                ? available.FirstOrDefault(b => b.LocalAddress.Equals(address))
                : available.FirstOrDefault(b => string.Equals(b.InterfaceName, wanted, StringComparison.OrdinalIgnoreCase));
        }

        private static int PrefixOf(UnicastIPAddressInformation unicast)
        {
#if NETFRAMEWORK
            byte[]? mask = unicast.IPv4Mask?.GetAddressBytes();
            return mask == null ? 0 : mask.Sum(b => Convert.ToString(b, 2).Count(c => c == '1'));
#else
            return unicast.PrefixLength;
#endif
        }
    }
}
