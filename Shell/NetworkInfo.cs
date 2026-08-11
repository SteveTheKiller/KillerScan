using KillerScan.Services;

namespace KillerScan.Shell
{
    // Scanner core, window half: show the detected interface and pre-fill the subnet box. The
    // detection itself is Services/LocalNetwork.cs, which the command line shares.
    public partial class MainWindow
    {
        private void PopulateNetworkInfo()
        {
            var net = LocalNetwork.Detect();
            if (net is null) return;

            SubnetInput.Text    = net.Subnet;
            LocalIpLabel.Text   = net.LocalIp;
            InterfaceLabel.Text = net.InterfaceLabel;
            // RJ-45 glyph for wired, Wi-Fi glyph for wireless.
            InterfaceIcon.Text = net.Wireless ? "" : "";

            // Blank when the interface has no gateway or no DNS of its own; the labels stay
            // empty rather than showing a stale value from a previous adapter.
            if (net.Gateway.Length > 0) GatewayLabel.Text = net.Gateway;
            if (net.Dns.Length > 0)     DnsLabel.Text     = net.Dns;
        }
    }
}
