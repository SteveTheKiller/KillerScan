using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KillerScan.Engine
{
    /// <summary>
    /// An adapter a scan is bound to: every probe is sent from <see cref="LocalAddress"/>, so it
    /// leaves through that adapter. A target is reachable when it is on the adapter's own network,
    /// or when Windows' best route to it already goes through this adapter, such as hosts behind
    /// a VPN adapter. Anything Windows would send out another adapter is refused rather than
    /// forced through this one. Name lookups still use the system resolver.
    /// </summary>
    public sealed record ScanBinding(string InterfaceName, IPAddress LocalAddress, int PrefixLength)
    {
        /// <summary>
        /// True when a bound scan may send to <paramref name="address"/>: it is on-link, or
        /// Windows routes it through this adapter. <paramref name="routeInterface"/> names the
        /// adapter Windows would use, empty when no route is known.
        /// </summary>
        public bool Reaches(IPAddress address, out string routeInterface)
        {
            routeInterface = string.Empty;
            if (Contains(address)) return true;
            routeInterface = NetworkPlatform.Current.BestRoute(address)?.Interface ?? string.Empty;
            return string.Equals(routeInterface, InterfaceName, StringComparison.OrdinalIgnoreCase);
        }

        public bool Reaches(IPAddress address) => Reaches(address, out _);

        /// <summary>Why a bound scan cannot reach a target, in plain English for logs and the CLI.</summary>
        public string Unreachable(IPAddress address, string routeInterface) =>
            routeInterface.Length == 0
                ? $"{address} is not on {InterfaceName}'s network ({Network}) and Windows has no route to it."
                : $"{address} is not on {InterfaceName}'s network ({Network}), and Windows routes it through {routeInterface}, not {InterfaceName}.";

        /// <summary>True when <paramref name="address"/> is on this adapter's network.</summary>
        public bool Contains(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork || LocalAddress.AddressFamily != AddressFamily.InterNetwork) return false;
            uint mask = PrefixLength <= 0 ? 0 : uint.MaxValue << (32 - Math.Min(32, PrefixLength));
            return (ToUInt(address) & mask) == (ToUInt(LocalAddress) & mask);
        }

        /// <summary>This adapter's IPv4 default gateway, or empty when it has none or has gone.</summary>
        public string Gateway() => Adapter()?.GetIPProperties().GatewayAddresses
            .FirstOrDefault(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? string.Empty;

        /// <summary>The ID Windows keeps for this adapter when it is renamed, or empty when it has gone.</summary>
        public string AdapterId() => Adapter()?.Id ?? string.Empty;

        private NetworkInterface? Adapter()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(adapter => string.Equals(adapter.Name, InterfaceName, StringComparison.OrdinalIgnoreCase));
            }
            catch (NetworkInformationException) { return null; }
        }

        /// <summary>
        /// The neighbor (ARP) cache as this adapter sees it, so another adapter carrying the same
        /// subnet cannot answer for it.
        /// </summary>
        public IReadOnlyDictionary<string, string> Neighbors()
        {
            if (NetworkPlatform.Current is WindowsNetworkPlatform windows
                && System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
            {
#pragma warning disable CA1416
                return windows.ReadNeighborCacheOn(LocalAddress);
#pragma warning restore CA1416
            }
            return NetworkPlatform.Current.ReadNeighborCache();
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
