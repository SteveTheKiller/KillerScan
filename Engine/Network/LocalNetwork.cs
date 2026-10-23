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
        public string AdapterId = "";
        public int InterfaceIndex;
        public string ConnectionIdentity = "";
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
            var net = Enumerate().FirstOrDefault();
            if (net != null)
            {
                NetworkScanner.GatewayIp = net.Gateway;
                NetworkScanner.DnsIp = net.Dns;
            }
            return net;
        }

        /// <summary>One entry per active IPv4 address. Does not change classifier globals.</summary>
        public static IReadOnlyList<LocalNet> Enumerate()
        {
            var result = new List<LocalNet>();
            try
            {
                var candidates = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up)
                    .Where(i => i.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                    .Where(i => i.GetIPProperties().UnicastAddresses
                        .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    .OrderByDescending(i => i.GetIPProperties().GatewayAddresses
                        .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                    .ThenByDescending(i => i.NetworkInterfaceType is NetworkInterfaceType.Ethernet)
                    .ThenByDescending(i => i.NetworkInterfaceType is NetworkInterfaceType.Wireless80211);

                foreach (var iface in candidates)
                {
                    var props = iface.GetIPProperties();
                    foreach (var unicast in props.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    {

                        var ip = unicast.Address;
                        var mask = unicast.IPv4Mask;
                        var ipBytes = ip.GetAddressBytes();
                        var maskBytes = mask.GetAddressBytes();
                        var netBytes = new byte[4];
                        for (int i = 0; i < 4; i++)
                            netBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
                        int prefix = 0;
                        foreach (byte b in maskBytes)
                            for (int bit = 7; bit >= 0; bit--)
                                if ((b & (1 << bit)) != 0) prefix++;
                                else goto done;
                        done:

                        bool wireless = iface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                        var net = new LocalNet
                        {
                            Subnet   = $"{new IPAddress(netBytes)}/{prefix}",
                            LocalIp  = ip.ToString(),
                            AdapterId = iface.Id,
                            InterfaceIndex = props.GetIPv4Properties()?.Index ?? 0,
                            Wireless = wireless,
                            // Description is the hardware name Windows shows in Network Connections;
                            // Name is whatever the adapter has been renamed to locally.
                            AdapterName = iface.Description ?? "",
                            LinkSpeed   = SafeSpeed(iface),
                            InterfaceLabel = iface.NetworkInterfaceType switch
                            {
                                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                                NetworkInterfaceType.Ethernet      => "Ethernet",
                                _                                  => iface.NetworkInterfaceType.ToString()
                            },
                        };

                        var gw = props.GatewayAddresses
                            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                        if (gw != null)
                        {
                            net.Gateway = gw.Address.ToString();
                        }

                        var dns = props.DnsAddresses
                            .FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork);
                        if (dns != null)
                        {
                            net.Dns = dns.ToString();
                            // Distinguishes a real DNS server from a router that just forwards.
                        }

                        // SSID/BSSID is required for explicitly bound Wi-Fi scans, so a same-address
                        // reconnection to a different network cannot look like the original link.
                        net.ConnectionIdentity = wireless ? WindowsLinkIdentity.Read(iface.Id) : "wired";
                        result.Add(net);
                    }
                }
            }
            catch { }
            return result;
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
