using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KillerScan.Engine
{
    /// <summary>What the active interface is and what it is attached to.</summary>
    public sealed class LocalNet
    {
        /// <summary>The attached network in CIDR form, e.g. "192.168.1.0/24".</summary>
        public string Subnet = "";
        public string LocalIp = "";
        public string Gateway = "";
        public string Dns = "";
        /// <summary>"Wi-Fi", "Ethernet", or the raw interface type for anything else.</summary>
        public string InterfaceLabel = "";
        public bool Wireless;
        /// <summary>The adapter's hardware description, e.g. "Realtek PCIe GbE Family Controller".</summary>
        public string AdapterName = "";
        /// <summary>Current link speed in bits per second, or 0 when Windows does not report one.</summary>
        public long LinkSpeed;

        /// <summary>Link speed for display, e.g. "1 Gbps" or "300 Mbps". Empty when unknown.</summary>
        public string LinkSpeedText => LinkSpeed <= 0 ? ""
            : LinkSpeed >= 1000000000 ? $"{LinkSpeed / 1000000000d:0.##} Gbps"
            : $"{LinkSpeed / 1000000d:0.##} Mbps";
    }

    /// <summary>
    /// Picks the interface a scan should default to and works out its subnet. Shared by the window,
    /// which pre-fills the subnet box from it, and the command line, which uses it when /scan is
    /// given no targets.
    /// </summary>
    public static class LocalNetwork
    {
        /// <summary>
        /// The best candidate interface, or null if none could be read. Preference order: has an
        /// IPv4 gateway (a real network rather than a VPN or virtual adapter), then Ethernet, then
        /// Wi-Fi.
        ///
        /// Also publishes the gateway and DNS addresses to <see cref="NetworkScanner"/>. The
        /// classifier needs both to tell a router from a host and a real DNS server from a router
        /// that just forwards, so every caller wants them set; a detect that left them unset would
        /// silently mis-classify.
        /// </summary>
        public static LocalNet? Detect()
        {
            NetworkInterface[] adapters;
            try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
            catch { return null; }

            var net = Pick(adapters.Select(Read).Where(c => c != null).Select(c => c!));
            if (net == null) return null;
            // Always the current network's, empty included, so a router from an earlier network is
            // never classified onto whatever device now has its address.
            NetworkScanner.GatewayIp = net.Gateway;   // so the classifier flags the router
            // Distinguishes a real DNS server from a router that just forwards.
            NetworkScanner.DnsIp = net.Dns;
            return net;
        }

        /// <summary>What <see cref="Pick"/> needs from one adapter, read up front so a bad adapter
        /// is dropped on its own instead of aborting the whole choice.</summary>
        internal sealed class Candidate
        {
            public NetworkInterfaceType Type;
            public IPAddress Address = IPAddress.None;
            public IPAddress? Mask;
            public string Gateway = "";
            public string Dns = "";
            public string Description = "";
            public long Speed;
        }

        /// <summary>One adapter as a candidate, or null when it is down, a loopback or tunnel, has
        /// no IPv4 address, or Windows throws while describing it.</summary>
        private static Candidate? Read(NetworkInterface iface)
        {
            try
            {
                if (iface.OperationalStatus != OperationalStatus.Up) return null;
                if (iface.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) return null;
                var props = iface.GetIPProperties();
                var unicast = props.UnicastAddresses
                    .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (unicast == null) return null;
                return new Candidate
                {
                    Type = iface.NetworkInterfaceType,
                    Address = unicast.Address,
                    Mask = unicast.IPv4Mask,
                    Gateway = props.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "",
                    Dns = props.DnsAddresses
                        .FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork)?.ToString() ?? "",
                    // Description is the hardware name Windows shows in Network Connections;
                    // Name is whatever the adapter has been renamed to locally.
                    Description = iface.Description ?? "",
                    Speed = SafeSpeed(iface),
                };
            }
            catch { return null; }
        }

        /// <summary>The best candidate as a <see cref="LocalNet"/>: one with a gateway first, then
        /// Ethernet, then Wi-Fi. A candidate without a usable mask (some virtual, Hyper-V and WSL
        /// adapters) is skipped so the next one still gets its turn.</summary>
        internal static LocalNet? Pick(IEnumerable<Candidate> candidates)
        {
            var ordered = candidates
                .OrderByDescending(c => c.Gateway.Length > 0)
                .ThenByDescending(c => c.Type is NetworkInterfaceType.Ethernet)
                .ThenByDescending(c => c.Type is NetworkInterfaceType.Wireless80211);

            foreach (var c in ordered)
            {
                var ipBytes = c.Address.GetAddressBytes();
                var maskBytes = c.Mask?.GetAddressBytes();
                if (ipBytes.Length != 4 || maskBytes is not { Length: 4 }) continue;
                var netBytes = new byte[4];
                for (int i = 0; i < 4; i++)
                    netBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
                int prefix = 0;
                foreach (byte b in maskBytes)
                    for (int bit = 7; bit >= 0; bit--)
                        if ((b & (1 << bit)) != 0) prefix++;
                        else goto done;
                done:
                // A 0.0.0.0 mask is no network at all, so the next adapter gets its turn.
                if (prefix == 0) continue;

                return new LocalNet
                {
                    Subnet   = $"{new IPAddress(netBytes)}/{prefix}",
                    LocalIp  = c.Address.ToString(),
                    Gateway  = c.Gateway,
                    Dns      = c.Dns,
                    Wireless = c.Type == NetworkInterfaceType.Wireless80211,
                    AdapterName = c.Description,
                    LinkSpeed   = c.Speed,
                    InterfaceLabel = c.Type switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                        NetworkInterfaceType.Ethernet      => "Ethernet",
                        _                                  => c.Type.ToString()
                    },
                };
            }
            return null;
        }

        /// <summary>
        /// Speed throws on some virtual and Wi-Fi adapters rather than returning a value, and a
        /// missing link speed is not a reason to lose the rest of the network information.
        /// </summary>
        private static long SafeSpeed(NetworkInterface iface)
        {
            try { return iface.Speed; }
            catch { return 0; }
        }
    }
}
