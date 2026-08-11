using System;
using KillerScan.Services;

namespace KillerScan.Shell
{
    // Screenshot / demo mode, window half. Launch with `KillerScan.exe --demo` (or /demo): the grid
    // fills with a fabricated network and every press of Scan re-rolls a fresh one. The network
    // itself is invented in Services/DemoData.cs; all this does is paint one into the window.
    public partial class MainWindow
    {
        /// <summary>Set from the launch arguments (App.xaml.cs) and read by the scan button and the
        /// constructor. The flag lives in the service so the About card can preview its signed state
        /// without Features reaching back into the window.</summary>
        private static bool DemoMode => DemoData.Enabled;

        private readonly Random _demoRng = new();

        // Re-rolls the whole demo network. Wired to Scan (Scanning.cs) and the initial load.
        private void GenerateDemoScan()
        {
            var demo = DemoData.Generate(_demoRng);

            SubnetInput.Text    = demo.Subnet;
            GatewayLabel.Text   = demo.Gateway;
            DnsLabel.Text       = demo.Gateway;
            LocalIpLabel.Text   = demo.LocalIp;
            InterfaceLabel.Text = demo.Wireless ? "Wi-Fi" : "Ethernet";
            _portableBadge.Visibility = System.Windows.Visibility.Collapsed;

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
