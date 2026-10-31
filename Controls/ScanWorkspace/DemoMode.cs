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
            // Split the same way the real detect does. Setting the whole line here left the real
            // adapter sitting in the detail label beside the fabricated one, so the footer listed
            // two network cards.
            FooterNetLabel.Text   = demo.LocalIp;
            FooterNetAdapter.Text = Segment(demo.AdapterName);
            FooterNetSpeed.Text   = Segment(demo.LinkSpeedText);

            _active.Devices.Clear();
            foreach (var d in demo.Devices) _active.Devices.Add(d);

            _active.ScannedSubnet = demo.Subnet;
            _active.ScannedAt = DateTimeOffset.Now;
            _active.Completion = ScanCompletion.Complete;
            _active.Refresh = null;
            RefreshDeviceCount();

            // Everything the other views read comes from the same fabricated network, so Topology,
            // Services, history and profiles all have something to show. All of it stays in memory:
            // the services refuse to touch their real files while demo mode is on.
            // Profiles are already fabricated: ScanProfiles.Load ran at startup, after the launch
            // flag was read, so the sidebar has them without being reloaded here.
            _location.History.SeedDemo(DemoData.History(demo));
            HistoryRecorded?.Invoke(this, EventArgs.Empty);

            // A baseline with a few strangers left out of it, so the new-device alert has a
            // number, and only some of the known devices approved, so all three marks show.
            var known = _active.Devices.Take(Math.Max(1, _active.Devices.Count - 3)).ToList();
            _location.Devices.Observe(known);
            _location.Devices.TrustAll(known.Take(Math.Max(1, known.Count - 3)));
            _active.NewIdentities = new(_active.Devices.Except(known).Select(DeviceIdentity.For), StringComparer.OrdinalIgnoreCase);

            _scanCompleted = true;
            var done = string.Format(Loc(ScanStatusText.Counted("Str_St_ScanComplete", _active.Devices.Count)), _active.Devices.Count);
            _active.Status = done;
            StatusText.Text = done;
            UpdateExportButtonAvailability();
        }
    }
}
