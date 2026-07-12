using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan
{
    // Scanner core: right-click actions on a discovered device (copy, ping, open,
    // remote in, and device-type overrides).
    public partial class MainWindow
    {
        private NetworkDevice? GetSelectedDevice() => ResultsGrid.SelectedItem as NetworkDevice;

        private void CopyIp_Click(object sender, RoutedEventArgs e)
        { var d = GetSelectedDevice(); if (d != null) Clipboard.SetText(d.IpAddress); }

        private void CopyMac_Click(object sender, RoutedEventArgs e)
        { var d = GetSelectedDevice(); if (d != null && !string.IsNullOrEmpty(d.MacAddress)) Clipboard.SetText(d.MacAddress); }

        private void CopyHostname_Click(object sender, RoutedEventArgs e)
        { var d = GetSelectedDevice(); if (d != null && !string.IsNullOrEmpty(d.Hostname)) Clipboard.SetText(d.Hostname); }

        private void PingDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            Process.Start(new ProcessStartInfo("cmd", $"/c ping -n 4 {d.IpAddress} & pause") { UseShellExecute = true });
        }

        private void OpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            Process.Start(new ProcessStartInfo($"http://{d.IpAddress}") { UseShellExecute = true });
        }

        private void RdpDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            Process.Start(new ProcessStartInfo("mstsc", $"/v:{d.IpAddress}") { UseShellExecute = true });
        }

        private void SshDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            Process.Start(new ProcessStartInfo("cmd", $"/c ssh {d.IpAddress}") { UseShellExecute = true });
        }

        private void SetType_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null || string.IsNullOrEmpty(d.MacAddress)) return;
            if (sender is MenuItem mi && mi.Tag is string type)
            {
                DeviceOverrides.Set(d.MacAddress, type);
                d.DeviceType = type;
                _filteredView?.Refresh();
                StatusText.Text = string.Format(Loc("Str_St_OverrideSet"), d.MacAddress, type);
            }
        }

        private void ClearOverride_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null || string.IsNullOrEmpty(d.MacAddress)) return;
            DeviceOverrides.Set(d.MacAddress, null);
            // Reclassify without override
            d.DeviceType = NetworkScanner.ClassifyDevice(d);
            _filteredView?.Refresh();
            StatusText.Text = string.Format(Loc("Str_St_OverrideCleared"), d.MacAddress);
        }
    }
}
