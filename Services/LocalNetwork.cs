using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KillerScan.Services
{
    /// <summary>What the active interface is and what it is attached to.</summary>
    internal sealed class LocalNet
    {
        /// <summary>The attached network in CIDR form, e.g. "192.168.1.0/24".</summary>
        internal string Subnet = "";
        internal string LocalIp = "";
        internal string Gateway = "";
        internal string Dns = "";
        /// <summary>"Wi-Fi", "Ethernet", or the raw interface type for anything else.</summary>
        internal string InterfaceLabel = "";
        internal bool Wireless;
        /// <summary>The adapter's hardware description, e.g. "Realtek PCIe GbE Family Controller".</summary>
        internal string AdapterName = "";
        /// <summary>Current link speed in bits per second, or 0 when Windows does not report one.</summary>
        internal long LinkSpeed;

        /// <summary>Link speed for display, e.g. "1 Gbps" or "300 Mbps". Empty when unknown.</summary>
        internal string LinkSpeedText => LinkSpeed <= 0 ? ""
            : LinkSpeed >= 1000000000 ? $"{LinkSpeed / 1000000000d:0.##} Gbps"
            : $"{LinkSpeed / 1000000d:0.##} Mbps";
    }

    /// <summary>
    /// Picks the interface a scan should default to and works out its subnet. Shared by the window,
    /// which pre-fills the subnet box from it, and the command line, which uses it when /scan is
    /// given no targets.
    /// </summary>
    internal static class LocalNetwork
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
        internal static LocalNet? Detect()
        {
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
                    var unicast = props.UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (unicast == null) continue;

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
                        NetworkScanner.GatewayIp = net.Gateway;   // so the classifier flags the router
                    }

                    var dns = props.DnsAddresses
                        .FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork);
                    if (dns != null)
                    {
                        net.Dns = dns.ToString();
                        // Distinguishes a real DNS server from a router that just forwards.
                        NetworkScanner.DnsIp = net.Dns;
                    }

                    return net;
                }
            }
            catch { }
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
