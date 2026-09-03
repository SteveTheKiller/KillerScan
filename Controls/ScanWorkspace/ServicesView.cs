using System.Windows;
using KillerScan.Models;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private bool _showServices;

        private void ServicesButton_Click(object sender, RoutedEventArgs e)
        {
            _showServices = !_showServices;
            if (_showServices && _showTopology)
                TopologyButton_Click(this, new RoutedEventArgs());
            ResultsGrid.Visibility = _showServices ? Visibility.Collapsed : Visibility.Visible;
            ServicesGrid.Visibility = _showServices ? Visibility.Visible : Visibility.Collapsed;
            ServicesButton.Tag = _showServices ? "on" : null;

            PaneTitle.Text = _showServices ? Loc("Str_Services_Title") : Loc("Str_DiscoveredDevices");
            if (_showServices) RefreshServices();
        }

        private void RefreshServices()
        {
            var selected = ServicesGrid.SelectedItems.Cast<ServiceRow>()
                .Select(row => (row.IpAddress, row.Port)).ToList();
            var sorts = ServicesGrid.Items.SortDescriptions.ToList();
            var rows = (_filteredView?.Cast<object>().OfType<NetworkDevice>() ?? ActiveDevices)
                .SelectMany(device => device.OpenPorts.Select(port => ServiceRow.From(device, port)))
                .OrderBy(row => row.Service, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Port)
                .ThenBy(row => row.IpSortKey)
                .ToList();
            ServicesGrid.ItemsSource = rows;
            foreach (var sort in sorts) ServicesGrid.Items.SortDescriptions.Add(sort);
            foreach (var row in rows.Where(row => selected.Contains((row.IpAddress, row.Port))))
                ServicesGrid.SelectedItems.Add(row);
        }

        private sealed class ServiceRow
        {
            private static readonly Dictionary<int, string> Names = new()
            {
                [21] = "FTP", [22] = "SSH", [23] = "Telnet", [53] = "DNS", [80] = "HTTP",
                [88] = "Kerberos", [135] = "Windows RPC", [139] = "NetBIOS", [389] = "LDAP",
                [443] = "HTTPS", [445] = "SMB", [464] = "Kerberos", [515] = "LPR",
                [548] = "AFP", [554] = "RTSP", [631] = "IPP", [636] = "LDAPS", [902] = "VMware",
                [1883] = "MQTT", [2179] = "Hyper-V", [3268] = "AD Global Catalog",
                [3269] = "AD Global Catalog TLS", [3389] = "RDP", [5000] = "Synology DSM",
                [5001] = "Synology DSM HTTPS", [5353] = "mDNS", [5357] = "WSD", [8006] = "Proxmox",
                [8080] = "HTTP Alternate", [8123] = "Home Assistant", [8443] = "HTTPS Alternate",
                [8883] = "MQTT TLS", [9100] = "Raw Printing", [32400] = "Plex", [62078] = "Apple Sync"
            };

            public string Service { get; init; } = string.Empty;
            public int Port { get; init; }
            public string DeviceName { get; init; } = string.Empty;
            public string IpAddress { get; init; } = string.Empty;
            public string DeviceType { get; init; } = string.Empty;
            public uint IpSortKey { get; init; }

            public static ServiceRow From(NetworkDevice device, int port) => new()
            {
                Service = Names.TryGetValue(port, out string? name) ? name : $"TCP {port}",
                Port = port,
                DeviceName = string.IsNullOrWhiteSpace(device.Hostname) ? device.MacAddress : device.Hostname,
                IpAddress = device.IpAddress,
                DeviceType = Controls.DeviceTypeConverter.Display(device.DeviceType),
                IpSortKey = device.IpSortKey
            };
        }
    }
}
