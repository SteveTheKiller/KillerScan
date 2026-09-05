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

            FooterNetLabel.Text   = net.LocalIp;
            FooterNetAdapter.Text = Segment(net.AdapterName);
            FooterNetSpeed.Text   = Segment(net.LinkSpeedText);
        }

        /// <summary>
        /// Re-states the status line and the device count in the current language. The messages
        /// are composed and stored as finished strings, so a locale swap cannot translate the one
        /// already on screen; what it can do is say the same thing again from the state the
        /// workspace is actually in. A transient line, an export confirmation say, is replaced by
        /// the general status rather than being left in the old language.
        /// </summary>
        internal void RefreshLocalizedText()
        {
            RefreshDeviceCount();
            if (IsScanning) return;

            string status = _scanCompleted
                ? string.Format(Loc("Str_St_ScanComplete"), _active.Devices.Count)
                : Loc("Str_St_Ready");
            _active.Status = status;
            StatusText.Text = status;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Wired or wireless glyph for the interface, shared with the footer and demo mode.</summary>
        internal static string InterfaceGlyph(bool wireless) => wireless ? "\uE701" : "\uE839";

        /// <summary>
        /// One footer segment, carrying its own separator so the pieces can be shown and hidden
        /// independently. Empty for anything Windows did not report, which then takes no space.
        /// </summary>
        internal static string Segment(string value) =>
            string.IsNullOrWhiteSpace(value) ? string.Empty : "  ·  " + value;
    }
}
