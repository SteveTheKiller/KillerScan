using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace KillerScan
{
    // Scanner core: detect the active interface and pre-fill the local subnet/IP/gateway/DNS.
    public partial class MainWindow
    {
        private void PopulateNetworkInfo()
        {
            try
            {
                // Prefer interfaces that have a gateway (real network, not VPN/virtual)
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
                    string subnet = $"{new IPAddress(netBytes)}/{prefix}";
                    SubnetInput.Text = subnet;
                    LocalIpLabel.Text = ip.ToString();
                    InterfaceLabel.Text = iface.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                        NetworkInterfaceType.Ethernet => "Ethernet",
                        _ => iface.NetworkInterfaceType.ToString()
                    };
                    // RJ-45 glyph for wired, Wi-Fi glyph for wireless.
                    InterfaceIcon.Text = iface.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "" : "";
                    var gw = props.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (gw != null)
                        GatewayLabel.Text = gw.Address.ToString();
                    var dns = props.DnsAddresses
                        .FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork);
                    if (dns != null)
                        DnsLabel.Text = dns.ToString();
                    break;
                }
            }
            catch { }
        }
    }
}
