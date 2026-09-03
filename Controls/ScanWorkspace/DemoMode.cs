using System;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private readonly bool DemoMode;

        private readonly Random _demoRng = new();

        private void GenerateDemoScan()
        {
            var demo = DemoData.Generate(_demoRng);

            SubnetInput.Text    = demo.Subnet;
            GatewayLabel.Text   = demo.Gateway;
            DnsLabel.Text       = demo.Gateway;
            LocalIpLabel.Text   = demo.LocalIp;
            InterfaceLabel.Text = demo.Wireless ? "Wi-Fi" : "Ethernet";


            _active.Devices.Clear();
            foreach (var d in demo.Devices) _active.Devices.Add(d);

            _active.ScannedSubnet = demo.Subnet;
            RefreshDeviceCount();
            var done = string.Format(Loc("Str_St_ScanComplete"), _active.Devices.Count);
            _active.Status = done;
            StatusText.Text = done;
            ExportButton.IsEnabled = true;
        }
    }
}
