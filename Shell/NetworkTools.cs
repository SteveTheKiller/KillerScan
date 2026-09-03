using System.Windows;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private NetworkToolsWindow? _watchWindow;
        private NetworkToolsWindow? _diagnosticsWindow;

        private void Watch_Click(object sender, RoutedEventArgs e)
        {
            if (_watchWindow != null) { _watchWindow.Activate(); return; }
            var local = LocalNetwork.Detect();
            var targets = new[] { local?.Gateway, local?.Dns, GetSelectedDevice()?.IpAddress }
                .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct();
            _watchWindow = new NetworkToolsWindow(false, string.Join(", ", targets), [], _appScale) { Owner = this };
            _watchWindow.Closed += (_, _) => _watchWindow = null;
            _watchWindow.Show();
        }

        private void Diagnose_Click(object sender, RoutedEventArgs e)
        {
            var device = GetSelectedDevice();
            if (device == null) { StatusText.Text = Loc("Str_Diag_Select"); return; }
            _diagnosticsWindow?.Close();
            _diagnosticsWindow = new NetworkToolsWindow(true, device.IpAddress,
                device.OpenPorts.Concat(new[] { 22, 80, 443, 445, 3389 }), _appScale) { Owner = this };
            _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
            _diagnosticsWindow.Show();
        }
    }
}
