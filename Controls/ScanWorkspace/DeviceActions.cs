using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private NetworkDevice? GetSelectedDevice() => ResultsGrid.SelectedItem as NetworkDevice;

        private void CopyIp_Click(object sender, RoutedEventArgs e)
        { var d = GetSelectedDevice(); if (d != null) Clipboard.SetText(d.IpAddress); }

        private void CopyMac_Click(object sender, RoutedEventArgs e)
        { var d = GetSelectedDevice(); if (d != null && !string.IsNullOrEmpty(d.MacAddress)) Clipboard.SetText(d.MacAddress); }

        private void CopyHostname_Click(object sender, RoutedEventArgs e)
        { var d = GetSelectedDevice(); if (d != null && !string.IsNullOrEmpty(d.Hostname)) Clipboard.SetText(d.Hostname); }

        private void PingDevice_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Ping");
        private void OpenBrowser_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Browser");
        private void RdpDevice_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Rdp");
        private void SshDevice_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Ssh");
        private void SshAsDevice_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("SshAs");
        private void Diagnose_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Diagnose");
        private void Watch_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Watch");

        private void SetTypeMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem parent) return;
            var d = GetSelectedDevice();
            foreach (object o in parent.Items)
            {
                if (o is not MenuItem mi || mi.Tag is not string type) continue;
                mi.IsChecked = d != null &&
                               string.Equals(type, d.DeviceType, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void SetType_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null || string.IsNullOrEmpty(d.MacAddress)) return;
            if (sender is MenuItem mi && mi.Tag is string type)
            {
                DeviceOverrides.Set(d.MacAddress, type);
                d.DeviceType = type;
                _filteredView?.Refresh();
                if (_showTopology) RefreshTopology();
                StatusText.Text = string.Format(Loc("Str_St_OverrideSet"), d.MacAddress, type);
            }
        }

        private void ClearOverride_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null || string.IsNullOrEmpty(d.MacAddress)) return;
            DeviceOverrides.Set(d.MacAddress, null);
            d.DeviceType = NetworkScanner.ClassifyDevice(d);
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
            StatusText.Text = string.Format(Loc("Str_St_OverrideCleared"), d.MacAddress);
        }

        private void RenameDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            var dlg = new InputDialog(
                Loc("Str_Rename_Title"),
                d.IpAddress,
                Loc("Str_Rename_Label"),
                DevicePreferences.GetName(d) ?? d.Hostname,
                Loc("Str_Rename_Save"),
                Loc("Str_Btn_Cancel")) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;
            DevicePreferences.SetName(d, dlg.Value);
            if (!string.IsNullOrWhiteSpace(dlg.Value)) d.Hostname = dlg.Value.Trim();
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
        }

        private void ClearDeviceName_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            DevicePreferences.SetName(d, null);
            d.Hostname = string.IsNullOrWhiteSpace(d.NetbiosName) ? string.Empty : d.NetbiosName;
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
        }

        private void TrustDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            DevicePreferences.SetTrusted(d, !DevicePreferences.IsTrusted(d));
            TrustDeviceMenuItem.IsChecked = DevicePreferences.IsTrusted(d);
        }
    }
}
