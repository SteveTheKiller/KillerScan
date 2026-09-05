using System;
using System.Linq;
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
            InterfaceLabel.Text = demo.InterfaceLabelText;
            InterfaceIcon.Text  = InterfaceGlyph(demo.Wireless);
            FooterNetLabel.Text = string.Join("  ·  ", demo.LocalIp, demo.AdapterName, demo.LinkSpeedText);

            _active.Devices.Clear();
            foreach (var d in demo.Devices) _active.Devices.Add(d);

            _active.ScannedSubnet = demo.Subnet;
            RefreshDeviceCount();

            // Everything the other views read comes from the same fabricated network, so Topology,
            // Services, history and profiles all have something to show. All of it stays in memory:
            // the services refuse to touch their real files while demo mode is on.
            // Profiles are already fabricated: ScanProfiles.Load ran at startup, after the launch
            // flag was read, so the sidebar has them without being reloaded here.
            ScanHistory.SeedDemo(DemoData.History(demo));
            HistoryRecorded?.Invoke(this, EventArgs.Empty);

            // A baseline with a few strangers left out of it, so the unknown-device alert has a
            // number and the trusted flag is not uniform across the table.
            DevicePreferences.TrustAll(_active.Devices.Take(Math.Max(1, _active.Devices.Count - 3)));

            _scanCompleted = true;
            var done = string.Format(Loc("Str_St_ScanComplete"), _active.Devices.Count);
            _active.Status = done;
            StatusText.Text = done;
            ExportButton.IsEnabled = true;
        }
    }
}
