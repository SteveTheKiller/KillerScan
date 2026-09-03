using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private void PopulateNetworkInfo()
        {
            var net = LocalNetwork.Detect();
            if (net is null) return;

            SubnetInput.Text    = net.Subnet;
            LocalIpLabel.Text   = net.LocalIp;
            InterfaceLabel.Text = net.InterfaceLabel switch
            {
                "Wi-Fi"    => Loc("Str_Iface_WiFi"),
                "Ethernet" => Loc("Str_Iface_Ethernet"),
                _          => net.InterfaceLabel
            };
            InterfaceIcon.Text = net.Wireless ? "" : "";

            if (net.Gateway.Length > 0) GatewayLabel.Text = net.Gateway;
            if (net.Dns.Length > 0)     DnsLabel.Text     = net.Dns;
        }
    }
}
