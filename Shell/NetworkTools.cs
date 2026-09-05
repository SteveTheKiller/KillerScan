using System.Windows;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        /// <summary>
        /// Keep Alive owns both the ping history and the one-off checks. Diagnostics used to be
        /// a second workspace of its own; it is now the details pane beside the cards, so F3 and
        /// the device menu land here instead of opening a separate view.
        /// </summary>
        private NetworkToolsWindow? _watchWorkspace;

        private static readonly int[] CommonPorts = [22, 80, 443, 445, 3389];

        private void Watch_Click(object sender, RoutedEventArgs e)
        {
            if (_watchWorkspace == null)
            {
                var local = LocalNetwork.Detect();
                var targets = DemoData.Enabled
                    ? DemoWatchTargets()
                    : string.Join(", ", new[] { local?.Gateway, local?.Dns, GetSelectedDevice()?.IpAddress }
                        .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct());
                _watchWorkspace = new NetworkToolsWindow(targets, _appScale);
                _watchWorkspace.DiagnoseRequested += address => ShowDeviceDetails(address);
                var bar = _watchWorkspace.DetachToolbar();
                bar.Margin = new Thickness(8, 0, 0, 0);
                RegisterViewToolbar("watch", bar);
            }
            ShowWorkspaceContent(_watchWorkspace, "watch");
            // Demo mode watches straight away rather than waiting for Start, so the view is never
            // an empty pane in a screenshot.
            if (DemoData.Enabled) _watchWorkspace.RestartWith(DemoWatchTargets());
        }

        /// <summary>
        /// Four addresses from the fabricated network: its gateway and three of its devices. The
        /// mix of answering and silent hosts is decided by the addresses themselves, so a re-rolled
        /// scan produces a different combination of healthy and dead cards.
        /// </summary>
        private static string DemoWatchTargets()
        {
            if (DemoData.Current is not { } demo) return string.Empty;
            var targets = new[] { demo.Gateway }
                .Concat(demo.Devices.Select(d => d.IpAddress).Where(ip => ip != demo.Gateway).Take(3))
                .Distinct();
            return string.Join(", ", targets);
        }

        private void Diagnose_Click(object sender, RoutedEventArgs e)
        {
            var device = GetSelectedDevice();
            if (device == null) { StatusText.Text = Loc("Str_Diag_Select"); return; }
            OpenNetworkTool(device, true);
        }

        private void OpenNetworkTool(Models.NetworkDevice device, bool diagnostics)
        {
            Watch_Click(this, new RoutedEventArgs());
            if (diagnostics) _watchWorkspace!.ShowDetails(device.IpAddress, device.OpenPorts.Concat(CommonPorts));
            else _watchWorkspace!.IncludeTarget(device.IpAddress);
        }

        /// <summary>
        /// Points the details pane at an address, carrying the ports a scan already found open
        /// on it so the checks probe what is really there rather than only the common few.
        /// </summary>
        private void ShowDeviceDetails(string address)
        {
            var known = (_scanWorkspace?.FindName("ResultsGrid") as System.Windows.Controls.DataGrid)?
                .Items.OfType<Models.NetworkDevice>().FirstOrDefault(d => d.IpAddress == address);
            _watchWorkspace?.ShowDetails(address, known?.OpenPorts.Concat(CommonPorts) ?? CommonPorts);
        }
    }
}
