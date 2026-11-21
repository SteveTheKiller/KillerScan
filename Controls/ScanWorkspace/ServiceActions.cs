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
            string? scheme = DeviceAccess.SuggestedWebProtocol(port);
            return scheme == null ? null : new UriBuilder(scheme, address.ToString(), port).Uri;
        }

        /// <summary>
        /// The address Open in Browser uses. For a port, the scheme that port actually answered
        /// with, or the one its number suggests. For the device, the web endpoint that answered
        /// (HTTPS first), falling back to http://address/ when none did.
        /// </summary>
        internal static string? BrowserAddress(NetworkDevice device, int? port = null)
        {
            if (!IPAddress.TryParse(device.IpAddress, out var address)) return null;
            var web = DeviceAccess.BestWeb(device, port);
            if (web != null) return DeviceAccess.Address(device.IpAddress, web);
            return port == null ? new UriBuilder("http", address.ToString()).Uri.AbsoluteUri : null;
        }

        private static bool CanOpenService(ServiceRow row, string action) => action switch
        {
            "Browser" => row.WebAddress != null,
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

        private void ServicesGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e) => PrepareServiceCard();

        /// <summary>
        /// Rebuilds the service card (Controls/ScanCards.cs) for the selected services: the ways in, the copy
        /// variants, then Select all and Export. A way in that this service does not offer stays in its place,
        /// dimmed, so the tiles never shift.
        /// </summary>
        internal void PrepareServiceCard()
        {
            if (ServiceCard == null) return;
            var card = ScanCards.Prepare(ServiceCard);
            var row = ServicesGrid.CurrentItem as ServiceRow ?? ServicesGrid.SelectedItem as ServiceRow;
            bool selected = row != null && ServicesGrid.SelectedItems.Contains(row);
            bool Allowed(string action) => action is "SelectAll" or "Export" ? ServicesGrid.Items.Count > 0
                : selected && (action != "CopyHost" || ServicesGrid.SelectedItems.Cast<ServiceRow>().Any(service => !string.IsNullOrWhiteSpace(service.Hostname)));
            bool Offered(string action) => selected && CanOpenService(row!, action);
            int count = ServicesGrid.SelectedItems.Count;
            string title = count > 1 ? string.Format(Loc("Str_Card_Services"), count) : selected ? row!.Service : Loc("Str_Services_Title");
            string meta = count == 1 && selected
                ? string.Join("  ·  ", new[] { ServiceEndpoint(row!.IpAddress, row.Port), row.Hostname }.Where(part => !string.IsNullOrWhiteSpace(part)))
                : "";
            card.Items.Add(ScanCards.Header(card, title, meta));
            KillerFamily.Controls.CardTile Tile(string id, string glyph, string caption, string? keys, bool enabled, string? shown = null, bool mirror = false) =>
                new(glyph, caption, keys, () => RunServiceMenuAction(id, this, new RoutedEventArgs()), mirror, enabled) { Id = id, Shown = shown };
            var open = new[]
            {
                Tile("Browser", "", Loc("Str_Ctx_OpenBrowser"), "Enter", Offered("Browser"), Loc("Str_Card_Browser")),
                Tile("Ssh", "", Loc("Str_Ctx_Ssh"), "Ctrl+S", Offered("Ssh")),
                Tile("Rdp", "", Loc("Str_Ctx_Rdp"), "Ctrl+D", Offered("Rdp")),
                Tile("SshAs", "", Loc("Str_Ctx_SshAs"), "Ctrl+Shift+S", Offered("SshAs")),
            };
            var copy = new[]
            {
                Tile("CopyEndpoint", "", Loc("Str_Services_CopyEndpoint"), "Ctrl+Shift+C", Allowed("CopyEndpoint"), Loc("Str_Card_Endpoint")),
                Tile("CopyService", "", Loc("Str_Services_CopyName"), "Ctrl+Shift+Y", Allowed("CopyService"), Loc("Str_Services_Service"), mirror: true),
                Tile("CopyPort", "", Loc("Str_Services_CopyPort"), "Ctrl+Alt+P", Allowed("CopyPort"), Loc("Str_Card_Port")),
                Tile("CopyIp", "", Loc("Str_Ctx_CopyIp"), "Ctrl+C", Allowed("CopyIp")),
                Tile("CopyHost", "", Loc("Str_Ctx_CopyHost"), "Ctrl+Alt+C", Allowed("CopyHost")),
            };
            var list = new[]
            {
                Tile("SelectAll", "", Loc("Str_Ctx_SelectAll"), "Ctrl+A", Allowed("SelectAll")),
                Tile("Export", "", Loc("Str_Export_ServicesCsv"), "Ctrl+E", Allowed("Export")),
            };
            // Customizable: SSH as and Copy service name start in the drawer, so the default card is three full rows.
            card.Items.Add(ScanCards.Editable(card, "Service", ["Browser", "SelectAll"], ["SshAs", "CopyService"], open, copy, list));
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

        private void RunServiceMenuAction(string action, object sender, RoutedEventArgs e)
        {
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
