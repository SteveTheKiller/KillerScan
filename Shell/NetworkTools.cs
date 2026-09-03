using System.Windows;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private void Watch_Click(object sender, RoutedEventArgs e)
        {
            var local = LocalNetwork.Detect();
            var targets = new[] { local?.Gateway, local?.Dns, GetSelectedDevice()?.IpAddress }
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct();
            AddWorkspaceTab(new WorkspaceTab
            {
                Content = new NetworkToolsWindow(false, string.Join(", ", targets), [], _appScale),
                TitleKey = "Str_Watch_Title"
            }, false);
        }

        private void Diagnose_Click(object sender, RoutedEventArgs e)
        {
            var device = GetSelectedDevice();
            if (device == null) { StatusText.Text = Loc("Str_Diag_Select"); return; }
            OpenNetworkTool(device, true, false);
        }

        private void OpenNetworkTool(Models.NetworkDevice device, bool diagnostics, bool beside)
        {
            AddWorkspaceTab(new WorkspaceTab
            {
                Content = new NetworkToolsWindow(diagnostics, device.IpAddress,
                    device.OpenPorts.Concat([22, 80, 443, 445, 3389]), _appScale),
                TitleKey = diagnostics ? "Str_Diag_Title" : "Str_Watch_Title",
                TitleSuffix = ": " + device.IpAddress
            }, beside);
        }
    }
}
