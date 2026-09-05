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
            InterfaceIcon.Text = InterfaceGlyph(net.Wireless);

            if (net.Gateway.Length > 0) GatewayLabel.Text = net.Gateway;
            if (net.Dns.Length > 0)     DnsLabel.Text     = net.Dns;

            FooterNetLabel.Text  = net.LocalIp;
            FooterNetDetail.Text = ComposeFooterNet(net.AdapterName, net.LinkSpeedText);
        }

        /// <summary>Wired or wireless glyph for the interface, shared with the footer and demo mode.</summary>
        internal static string InterfaceGlyph(bool wireless) => wireless ? "\uE701" : "\uE839";

        /// <summary>
        /// The part of the footer line that follows the address: the adapter and its link speed.
        /// Anything Windows did not report is left out rather than shown empty.
        /// </summary>
        private static string ComposeFooterNet(string adapter, string speed)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(adapter)) parts.Add(adapter);
            if (!string.IsNullOrWhiteSpace(speed))   parts.Add(speed);
            return parts.Count == 0 ? string.Empty : "  ·  " + string.Join("  ·  ", parts);
        }
    }
}
