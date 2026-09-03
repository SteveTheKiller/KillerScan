using System.Windows;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private NetworkToolsWindow? _watchWorkspace;
        private NetworkToolsWindow? _diagnosticsWorkspace;

        private void Watch_Click(object sender, RoutedEventArgs e)
        {
            if (_watchWorkspace == null)
            {
                var local = LocalNetwork.Detect();
                var targets = new[] { local?.Gateway, local?.Dns, GetSelectedDevice()?.IpAddress }
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct();
                _watchWorkspace = new NetworkToolsWindow(false, string.Join(", ", targets), [], _appScale);
            }
            ShowWorkspaceContent(_watchWorkspace, "watch");
        }

        private void Diagnose_Click(object sender, RoutedEventArgs e)
        {
            var device = GetSelectedDevice();
            if (device == null) { StatusText.Text = Loc("Str_Diag_Select"); return; }
            OpenNetworkTool(device, true);
        }

        private void OpenNetworkTool(Models.NetworkDevice device, bool diagnostics)
        {
            if (!diagnostics)
            {
                Watch_Click(this, new RoutedEventArgs());
                _watchWorkspace!.IncludeTarget(device.IpAddress);
                return;
            }
            if (_diagnosticsWorkspace != null)
            {
                _workspaceBody.Children.Remove(_diagnosticsWorkspace);
                _diagnosticsWorkspace.Dispose();
            }
            _diagnosticsWorkspace = new NetworkToolsWindow(true, device.IpAddress,
                device.OpenPorts.Concat([22, 80, 443, 445, 3389]), _appScale);
            ShowWorkspaceContent(_diagnosticsWorkspace, "diagnostics");
        }
    }
}
