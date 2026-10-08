using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        internal static string ServiceEndpoint(string ip, int port) =>
            (ip.Contains(':') ? "[" + ip + "]" : ip) + ":" + port.ToString(CultureInfo.InvariantCulture);

        internal static Uri? ServiceBrowserUri(string ip, int port)
        {
            if (!IPAddress.TryParse(ip, out var address)) return null;
            string? scheme = port switch
            {
                443 or 5001 or 8006 or 8443 => "https",
                80 or 5000 or 5357 or 8080 or 8123 or 32400 => "http",
                _ => null
            };
            return scheme == null ? null : new UriBuilder(scheme, address.ToString(), port).Uri;
        }

        private static bool CanOpenService(ServiceRow row, string action) => action switch
        {
            "Browser" => ServiceBrowserUri(row.IpAddress, row.Port) != null,
            "Ssh" or "SshAs" => row.Port == 22,
            "Rdp" => row.Port == 3389,
            _ => false
        };

        private void ServicesGrid_RightClick(object sender, MouseButtonEventArgs e)
        {
            var row = ItemsControl.ContainerFromElement(ServicesGrid, e.OriginalSource as DependencyObject) as DataGridRow;
            if (row?.Item is not ServiceRow service) { ServicesGrid.UnselectAll(); return; }
            if (!ServicesGrid.SelectedItems.Contains(service))
            {
                ServicesGrid.SelectedItems.Clear();
                ServicesGrid.SelectedItem = service;
            }
            ServicesGrid.CurrentItem = service;
            ServicesGrid.Focus();
        }

        private void ServicesGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var row = ServicesGrid.CurrentItem as ServiceRow ?? ServicesGrid.SelectedItem as ServiceRow;
            bool selected = row != null && ServicesGrid.SelectedItems.Contains(row);
            foreach (var item in ServicesGrid.ContextMenu.Items.OfType<MenuItem>())
            {
                string action = item.Tag as string ?? "";
                if (action is "Browser" or "Ssh" or "SshAs" or "Rdp")
                    item.Visibility = selected && CanOpenService(row!, action) ? Visibility.Visible : Visibility.Collapsed;
                item.IsEnabled = action is "SelectAll" or "Export" ? ServicesGrid.Items.Count > 0
                    : selected && (action != "CopyHost" || ServicesGrid.SelectedItems.Cast<ServiceRow>().Any(service => !string.IsNullOrWhiteSpace(service.Hostname)));
            }
            ServiceConnectSeparator.Visibility = selected && new[] { "Browser", "Ssh", "Rdp" }.Any(action => CanOpenService(row!, action))
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private string ServiceCopyText(string action) => string.Join(Environment.NewLine,
            ServicesGrid.Items.OfType<ServiceRow>().Where(row => ServicesGrid.SelectedItems.Contains(row))
                .Select(row => action switch
                {
                    "CopyService" => row.Service,
                    "CopyPort" => row.Port.ToString(CultureInfo.InvariantCulture),
                    "CopyEndpoint" => ServiceEndpoint(row.IpAddress, row.Port),
                    "CopyIp" => row.IpAddress,
                    "CopyHost" => row.Hostname,
                    _ => ""
                }).Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.Ordinal));

        private void CopyServiceText(string action) => CopyDeviceText(ServiceCopyText(action));

        private void ServiceMenu_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string action }) return;
            if (action.StartsWith("Copy", StringComparison.Ordinal)) CopyServiceText(action);
            else if (action == "SelectAll") ServicesGrid.SelectAll();
            else if (action == "Export") ExportServicesCsv_Click(sender, e);
            else RaiseServiceAction(action);
        }

        public bool HandleServiceShortcut(Key key, ModifierKeys modifiers)
        {
            string? action = (key, modifiers) switch
            {
                (Key.Enter, ModifierKeys.None) => "Browser",
                (Key.S, ModifierKeys.Control) => "Ssh",
                (Key.S, ModifierKeys.Control | ModifierKeys.Shift) => "SshAs",
                (Key.D, ModifierKeys.Control) => "Rdp",
                (Key.C, ModifierKeys.Control) => "CopyIp",
                (Key.C, ModifierKeys.Control | ModifierKeys.Shift) => "CopyEndpoint",
                (Key.C, ModifierKeys.Control | ModifierKeys.Alt) => "CopyHost",
                (Key.Y, ModifierKeys.Control | ModifierKeys.Shift) => "CopyService",
                (Key.P, ModifierKeys.Control | ModifierKeys.Alt) => "CopyPort",
                _ => null
            };
            if (action != null)
            {
                if (action.StartsWith("Copy", StringComparison.Ordinal)) CopyServiceText(action);
                else RaiseServiceAction(action);
                return true;
            }
            if (key == Key.Apps && modifiers == ModifierKeys.None || key == Key.F10 && modifiers == ModifierKeys.Shift)
            {
                ServicesGrid_ContextMenuOpening(ServicesGrid, null!);
                ServicesGrid.ContextMenu.PlacementTarget = (ServicesGrid.SelectedItem == null ? null :
                    ServicesGrid.ItemContainerGenerator.ContainerFromItem(ServicesGrid.SelectedItem)) as UIElement ?? ServicesGrid;
                ServicesGrid.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                ServicesGrid.ContextMenu.IsOpen = true; return true;
            }
            return false;
        }

        private void RaiseServiceAction(string action)
        {
            var row = ServicesGrid.CurrentItem as ServiceRow ?? ServicesGrid.SelectedItem as ServiceRow;
            if (row == null || !ServicesGrid.SelectedItems.Contains(row) || !CanOpenService(row, action)) return;
            var device = _active.Devices.FirstOrDefault(device => device.IpAddress == row.IpAddress);
            if (device != null) DeviceAction?.Invoke(this, new ScanDeviceActionEventArgs(device, action, false, row.Port));
        }
    }
}
