using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Reflection;
using KillerScan.Models;
using KillerScan.Services;
using Microsoft.Win32;

namespace KillerScan
{
    public partial class MainWindow : Window
    {
        private readonly NetworkScanner _scanner = new();
        private readonly ObservableCollection<NetworkDevice> _devices = [];
        private CancellationTokenSource? _cts;
        private ICollectionView? _filteredView;

        public MainWindow()
        {
            InitializeComponent();
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) VersionLabel.Text = $"v{v.Major}.{v.Minor}.{v.Build}";
            ResultsGrid.ItemsSource = _devices;            _filteredView = CollectionViewSource.GetDefaultView(_devices);
            if (_filteredView is ListCollectionView lcv)
            {
                lcv.IsLiveSorting = true;
                lcv.LiveSortingProperties.Add(nameof(NetworkDevice.IpSortKey));
                lcv.SortDescriptions.Add(new SortDescription(nameof(NetworkDevice.IpSortKey), ListSortDirection.Ascending));
            }
            OuiLookup.Load();
            DeviceOverrides.Load();
            var ver = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.1.3";
            VersionLabel.Text = $"v{ver}";
            PopulateNetworkInfo();
            StatusText.Text = $"Ready -- {OuiLookup.Count:N0} OUI vendors loaded";
            _scanner.StatusChanged += status =>
                Dispatcher.Invoke(() => StatusText.Text = status);
            _scanner.ProgressChanged += pct =>
                Dispatcher.Invoke(() => ScanProgress.Value = pct);
            _scanner.DeviceFound += device =>
                Dispatcher.Invoke(() =>
                {
                    _devices.Add(device);
                    DeviceCount.Text = $"{_devices.Count} device{(_devices.Count == 1 ? "" : "s")} found";
                });
        }

        private void PopulateNetworkInfo()
        {
            try
            {
                // Prefer interfaces that have a gateway (real network, not VPN/virtual)
                var candidates = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(i => i.OperationalStatus == OperationalStatus.Up)
                    .Where(i => i.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                    .Where(i => i.GetIPProperties().UnicastAddresses
                        .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                    .OrderByDescending(i => i.GetIPProperties().GatewayAddresses
                        .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
                    .ThenByDescending(i => i.NetworkInterfaceType is NetworkInterfaceType.Ethernet)
                    .ThenByDescending(i => i.NetworkInterfaceType is NetworkInterfaceType.Wireless80211);

                foreach (var iface in candidates)
                {
                    var props = iface.GetIPProperties();
                    var unicast = props.UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (unicast == null) continue;                    var ip = unicast.Address;
                    var mask = unicast.IPv4Mask;
                    var ipBytes = ip.GetAddressBytes();
                    var maskBytes = mask.GetAddressBytes();
                    var netBytes = new byte[4];
                    for (int i = 0; i < 4; i++)
                        netBytes[i] = (byte)(ipBytes[i] & maskBytes[i]);
                    int prefix = 0;
                    foreach (byte b in maskBytes)
                        for (int bit = 7; bit >= 0; bit--)
                            if ((b & (1 << bit)) != 0) prefix++;
                            else goto done;
                    done:
                    string subnet = $"{new IPAddress(netBytes)}/{prefix}";
                    SubnetInput.Text = subnet;
                    LocalIpLabel.Text = $"local:{ip}";
                    InterfaceLabel.Text = iface.NetworkInterfaceType switch
                    {
                        NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                        NetworkInterfaceType.Ethernet => "Ethernet",
                        _ => iface.NetworkInterfaceType.ToString()
                    };
                    var gw = props.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);
                    if (gw != null)
                        GatewayLabel.Text = $"gw:{gw.Address}";
                    var dns = props.DnsAddresses
                        .FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork);
                    if (dns != null)
                        DnsLabel.Text = $"dns:{dns}";
                    break;
                }
            }
            catch { }
        }

        private async void ScanBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_cts != null)
            {
                _cts.Cancel(); _cts = null;
                ScanBtn.Content = "Scan";
                ScanProgress.Visibility = Visibility.Collapsed;
                ExportButton.IsEnabled = _devices.Count > 0;
                return;
            }
            _devices.Clear();
            DeviceCount.Text = "0 devices found";
            ScanProgress.Value = 0;
            ScanProgress.Visibility = Visibility.Visible;
            ExportButton.IsEnabled = false;
            ScanBtn.Content = "Stop";
            _cts = new CancellationTokenSource();
            try
            {
                await _scanner.ScanSubnetAsync(SubnetInput.Text, _cts.Token, fullScan: true);
                ExportButton.IsEnabled = _devices.Count > 0;
            }
            catch (OperationCanceledException) { StatusText.Text = "Scan cancelled"; }
            catch (Exception ex) { MessageBox.Show($"Scan error: {ex.Message}", "KillerScan", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally
            {
                _cts = null;
                ScanBtn.Content = "Scan";
                ScanProgress.Visibility = Visibility.Collapsed;
            }
        }

        private void FilterInput_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_filteredView == null) return;
            string filter = FilterInput.Text.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(filter))
                _filteredView.Filter = null;
            else
                _filteredView.Filter = obj =>
                {
                    if (obj is not NetworkDevice d) return false;
                    return d.IpAddress.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || d.Hostname.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || d.MacAddress.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || d.Vendor.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || d.DeviceType.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                };
            int shown = _filteredView.Cast<object>().Count();
            DeviceCount.Text = string.IsNullOrEmpty(filter)
                ? $"{_devices.Count} device{(_devices.Count == 1 ? "" : "s")} found"
                : $"{shown} of {_devices.Count} shown";
        }
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
            if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is string type)
            {
                DeviceOverrides.Set(d.MacAddress, type);
                d.DeviceType = type;
                _filteredView?.Refresh();
                StatusText.Text = $"Override set: {d.MacAddress} = {type}";
            }
        }

        private void ClearOverride_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null || string.IsNullOrEmpty(d.MacAddress)) return;
            DeviceOverrides.Set(d.MacAddress, null);
            // Reclassify without override
            d.DeviceType = NetworkScanner.ClassifyDevice(d);
            _filteredView?.Refresh();
            StatusText.Text = $"Override cleared for {d.MacAddress}";
        }
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_devices.Count == 0) return;
            ExportButton.ContextMenu!.IsOpen = true;
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "CSV File|*.csv", FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("IP Address,Hostname,MAC Address,Vendor,Type,Open Ports");
                foreach (var d in _devices.OrderBy(d => d.IpSortKey))
                    sb.AppendLine($"\"{d.IpAddress}\",\"{d.Hostname}\",\"{d.MacAddress}\",\"{d.Vendor}\",\"{d.DeviceType}\",\"{d.OpenPortsDisplay}\"");
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                StatusText.Text = $"Exported to {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            { MessageBox.Show($"Export error: {ex.Message}", "KillerScan", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        private void ExportHtml_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = "HTML Report|*.html", FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.html" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("<!DOCTYPE html><html><head><meta charset='utf-8'/><title>KillerScan Report</title>");
                sb.AppendLine("<style>body{background:#1c1c1c;color:#e0e0e0;font-family:'Segoe UI',sans-serif;padding:24px}");
                sb.AppendLine("h1{color:#1ea54c;font-size:22px}table{border-collapse:collapse;width:100%;margin-top:16px}");
                sb.AppendLine("th{background:#353535;color:#999;text-align:left;padding:8px 12px;font-size:12px}");
                sb.AppendLine("td{border-bottom:1px solid #282828;padding:8px 12px;font-size:13px}tr:hover{background:#1a3a25}");
                sb.AppendLine(".meta{color:#999;font-size:12px;margin-top:8px}.footer{color:#444;font-size:10px;margin-top:24px}");
                sb.AppendLine("</style></head><body>");
                sb.AppendLine($"<h1>KillerScan Report</h1>");
                sb.AppendLine($"<p class='meta'>Subnet: {SubnetInput.Text} | Scanned: {DateTime.Now:yyyy-MM-dd HH:mm} | Devices: {_devices.Count}</p>");
                sb.AppendLine("<table><tr><th>IP Address</th><th>Hostname</th><th>MAC Address</th><th>Vendor</th><th>Type</th><th>Open Ports</th></tr>");
                foreach (var d in _devices.OrderBy(d => d.IpSortKey))
                    sb.AppendLine($"<tr><td>{d.IpAddress}</td><td>{d.Hostname}</td><td>{d.MacAddress}</td><td>{d.Vendor}</td><td>{d.DeviceType}</td><td>{d.OpenPortsDisplay}</td></tr>");
                sb.AppendLine("</table>");
                sb.AppendLine("<p class='footer'>&copy; 2026 Steve the Killer</p>");
                sb.AppendLine("</body></html>");
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                StatusText.Text = $"Exported to {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            { MessageBox.Show($"Export error: {ex.Message}", "KillerScan", MessageBoxButton.OK, MessageBoxImage.Error); }
        }
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) MaximizeBtn_Click(sender, e);
            else DragMove();
        }

        private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void CloseBtn_Click(object sender, RoutedEventArgs e)
            => Close();
    }
}