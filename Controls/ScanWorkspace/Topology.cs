using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerScan.Models;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private const double TopologyNodeWidth = 126;
        private const double TopologyNodeHeight = 40;
        private enum TopologyOrder { Role, Type, Ip, Vendor }
        private TopologyOrder _topologyOrder = TopologyOrder.Role;
        private bool _topologyOrderLoaded;
        private bool _showTopology;
        private readonly Dictionary<string, Point> _topologyPositions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Line> _topologyLinks = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Border, Point> _topologyDragStarts = [];
        private Point _topologyDragMouseStart;

        private void TopologyButton_Click(object sender, RoutedEventArgs e)
        {
            _showTopology = !_showTopology;
            if (_showTopology && _showServices)
            {
                _showServices = false;
                ServicesGrid.Visibility = Visibility.Collapsed;
                ServicesButton.Tag = null;

            }
            ResultsGrid.Visibility = _showTopology ? Visibility.Collapsed : Visibility.Visible;
            TopologyPane.Visibility = _showTopology ? Visibility.Visible : Visibility.Collapsed;
            TopologyButton.Tag = _showTopology ? "on" : null;
            UpdateViewChrome();

            PaneTitle.Text = _showTopology ? Loc("Str_Topology_Title") : Loc("Str_DiscoveredDevices");
            if (_showTopology)
            {
                LoadTopologyOrder();
                RefreshTopology();
            }
        }

        /// <summary>
        /// Derives the toolbar chrome that belongs to one view from the current state, rather
        /// than leaving it as a side effect of whichever toggle happened to run. The arrange
        /// button used to be shown and hidden inside TopologyButton_Click alone, so any path
        /// that reached topology without an odd number of trips through that handler left the
        /// button hidden while the graph was on screen.
        /// </summary>
        private void UpdateViewChrome() =>
            TopologyOrderButton.Visibility = _showTopology ? Visibility.Visible : Visibility.Collapsed;

        private void LoadTopologyOrder()
        {
            if (_topologyOrderLoaded) return;
            _topologyOrderLoaded = true;
            if (Enum.TryParse(App.GetSetting("TopologyOrder"), out TopologyOrder saved))
                _topologyOrder = saved;
            UpdateTopologyOrderUi();
        }

        private void TopologyOrderButton_Click(object sender, RoutedEventArgs e)
        {
            if (TopologyOrderButton.ContextMenu == null) return;
            UpdateTopologyOrderUi();
            TopologyOrderButton.ContextMenu.PlacementTarget = TopologyOrderButton;
            TopologyOrderButton.ContextMenu.Placement =
                System.Windows.Controls.Primitives.PlacementMode.Bottom;
            TopologyOrderButton.ContextMenu.IsOpen = true;
        }

        private void TopologyOrderItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string value } ||
                !Enum.TryParse(value, out TopologyOrder order)) return;
            SetTopologyOrder(order);
        }

        private void SetTopologyOrder(TopologyOrder order)
        {
            _topologyOrder = order;
            _topologyPositions.Clear();
            App.SetSetting("TopologyOrder", order.ToString());
            UpdateTopologyOrderUi();
            RefreshTopology();
        }

        private void UpdateTopologyOrderUi()
        {
            TopologyRoleItem.IsChecked = _topologyOrder == TopologyOrder.Role;
            TopologyTypeItem.IsChecked = _topologyOrder == TopologyOrder.Type;
            TopologyIpItem.IsChecked = _topologyOrder == TopologyOrder.Ip;
            TopologyVendorItem.IsChecked = _topologyOrder == TopologyOrder.Vendor;
        }

        private void TopologyPane_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_showTopology) RefreshTopology();
        }

        private void RefreshTopology()
        {
            if (TopologyCanvas == null || TopologyPane == null || !_showTopology) return;

            double width = Math.Max(TopologyPane.ActualWidth, 640);
            List<NetworkDevice> visible =
                [.. (_filteredView?.Cast<object>().OfType<NetworkDevice>() ?? ActiveDevices)];
            string localIp = LocalIpLabel?.Text ?? string.Empty;
            string gatewayIp = GatewayLabel?.Text ?? string.Empty;
            string dnsIp = DnsLabel?.Text ?? string.Empty;

            var regular = visible.Where(d => !SameIp(d.IpAddress, localIp)
                                          && !SameIp(d.IpAddress, gatewayIp)
                                          && !SameIp(d.IpAddress, dnsIp)).ToList();
            int columns = Math.Max(1, (int)((width - 36) / (TopologyNodeWidth + 22)));
            var deviceRows = BuildDeviceRows(regular, columns, _topologyOrder);
            int groupGaps = Math.Max(0, deviceRows.Count(r => r.StartsGroup) - 1);
            double height = Math.Max(TopologyPane.ActualHeight,
                218 + deviceRows.Count * 56 + groupGaps * 10);
            TopologyCanvas.Width = width;
            TopologyCanvas.Height = height;
            TopologyCanvas.Children.Clear();
            _topologyLinks.Clear();

            var center = new Point(width / 2, 138);

            var gateway = new Point(center.X, 42);
            var local = new Point(Math.Max(76, center.X - 190), center.Y);
            var gatewayDevice = visible.FirstOrDefault(d => SameIp(d.IpAddress, gatewayIp));
            var localDevice = visible.FirstOrDefault(d => SameIp(d.IpAddress, localIp));
            if (gatewayDevice != null && _topologyPositions.TryGetValue(gatewayDevice.IpAddress, out var savedGateway))
                gateway = savedGateway;
            if (localDevice != null && _topologyPositions.TryGetValue(localDevice.IpAddress, out var savedLocal))
                local = savedLocal;
            var gatewayLink = DrawInferredLink(center, gateway);
            var localLink = DrawInferredLink(center, local);
            AddRoleNode(gateway.X, gateway.Y, Loc("Str_Lbl_Gateway"), gatewayIp, "TypeRouter",
                gatewayDevice, gatewayLink);
            AddRoleNode(local.X, local.Y, Loc("Str_Lbl_Local"), localIp, "PrimaryBrush",
                localDevice, localLink);

            if (!string.IsNullOrWhiteSpace(dnsIp) && dnsIp != "--" && !SameIp(dnsIp, gatewayIp))
            {
                var dns = new Point(Math.Min(width - 76, center.X + 190), center.Y);
                var dnsDevice = visible.FirstOrDefault(d => SameIp(d.IpAddress, dnsIp));
                if (dnsDevice != null && _topologyPositions.TryGetValue(dnsDevice.IpAddress, out var savedDns))
                    dns = savedDns;
                var dnsLink = DrawInferredLink(center, dns);
                AddRoleNode(dns.X, dns.Y, Loc("Str_Lbl_Dns"), dnsIp, "TypeDns",
                    dnsDevice, dnsLink);
            }

            AddNetworkNode(center.X, center.Y);

            if (regular.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = Loc("Str_Topology_Empty"),
                    FontSize = 12,
                    TextAlignment = TextAlignment.Center,
                    Width = 300
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                Canvas.SetLeft(empty, center.X - 150);
                Canvas.SetTop(empty, center.Y + 45);
                TopologyCanvas.Children.Add(empty);
                return;
            }

            double rowY = 218;
            for (int rowIndex = 0; rowIndex < deviceRows.Count; rowIndex++)
            {
                var (rowDevices, startsGroup) = deviceRows[rowIndex];
                if (rowIndex > 0 && startsGroup) rowY += 10;
                int rowCount = rowDevices.Count;
                double rowWidth = rowCount * TopologyNodeWidth + (rowCount - 1) * 22;
                double left = (width - rowWidth) / 2 + TopologyNodeWidth / 2;
                for (int column = 0; column < rowCount; column++)
                {
                    var point = new Point(left + column * (TopologyNodeWidth + 22), rowY);
                    var device = rowDevices[column];
                    if (_topologyPositions.TryGetValue(device.IpAddress, out var saved))
                        point = new Point(
                            Math.Max(TopologyNodeWidth / 2, Math.Min(width - TopologyNodeWidth / 2, saved.X)),
                            Math.Max(TopologyNodeHeight / 2, Math.Min(height - TopologyNodeHeight / 2, saved.Y)));
                    var link = DrawInferredLink(center, point);
                    AddDeviceNode(point.X, point.Y, device, link);
                }
                rowY += 56;
            }
        }

        private static List<(List<NetworkDevice> Devices, bool StartsGroup)> BuildDeviceRows(
            IEnumerable<NetworkDevice> devices, int columns, TopologyOrder order)
        {
            var rows = new List<(List<NetworkDevice>, bool)>();
            if (order == TopologyOrder.Ip)
            {
                List<NetworkDevice> items = [.. devices.OrderBy(d => d.IpSortKey)];
                for (int offset = 0; offset < items.Count; offset += columns)
                    rows.Add(([.. items.Skip(offset).Take(columns)], false));
                return rows;
            }

            var grouped = order switch
            {
                TopologyOrder.Type => devices
                    .OrderBy(d => d.DeviceType, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.IpSortKey)
                    .GroupBy(d => d.DeviceType),
                TopologyOrder.Vendor => devices
                    .OrderBy(d => d.Vendor, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.IpSortKey)
                    .GroupBy(d => string.IsNullOrWhiteSpace(d.Vendor) ? "~" : d.Vendor),
                _ => devices
                    .OrderBy(d => TopologyGroup(d.DeviceType))
                    .ThenBy(d => d.DeviceType, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(d => d.IpSortKey)
                    .GroupBy(d => TopologyGroup(d.DeviceType).ToString())
            };

            foreach (var group in grouped)
            {
                List<NetworkDevice> items = [.. group];
                for (int offset = 0; offset < items.Count; offset += columns)
                    rows.Add(([.. items.Skip(offset).Take(columns)], offset == 0));
            }
            return rows;
        }

        private static int TopologyGroup(string type) => type switch
        {
            "Router" or "Router/DNS" or "Switch/AP" or "Network" or "DNS Server" => 0,
            "Server" or "Windows Server" or "Linux/SSH" or "NAS" or "Hypervisor" or
                "Home Assistant" => 1,
            "Windows" or "Apple Device" or "Mobile" => 2,
            "Printer" or "Camera" or "IoT" or "Smart TV" or "Apple TV" or
                "Media Streamer" or "Web Device" => 3,
            _ => 4
        };

        private static bool SameIp(string left, string right) =>
            !string.IsNullOrWhiteSpace(left) && left != "--" &&
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private Line DrawInferredLink(Point from, Point to)
        {
            var line = new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                StrokeThickness = 1,
                StrokeDashArray = [2, 4],
                Opacity = 0.65,
                IsHitTestVisible = false
            };
            line.SetResourceReference(Shape.StrokeProperty, "MutedTextBrush");
            TopologyCanvas.Children.Add(line);
            return line;
        }

        private void AddNetworkNode(double x, double y)
        {
            string subnet = string.IsNullOrWhiteSpace(ActiveSubnet) ? SubnetInput.Text : ActiveSubnet;
            AddNode(x, y, Loc("Str_Topology_Network"), subnet, "PrimaryBrush", null);
        }

        private void AddRoleNode(double x, double y, string role, string value, string brushKey,
                                 NetworkDevice? device, Line link)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "--") value = Loc("Str_Dev_Unknown");
            AddNode(x, y, role, value, brushKey, device);
            if (device != null)
                _topologyLinks[device.IpAddress] = link;
        }

        private void AddDeviceNode(double x, double y, NetworkDevice device, Line link)
        {
            string title = string.IsNullOrWhiteSpace(device.Hostname)
                ? Controls.DeviceTypeConverter.Display(device.DeviceType)
                : device.Hostname;
            string brush = DeviceBrush(device.DeviceType);
            var node = AddNode(x, y, title, device.IpAddress, brush, device);
            _topologyLinks[device.IpAddress] = link;
            node.ToolTip = string.Join(Environment.NewLine,
                new[] { title, device.IpAddress, Controls.DeviceTypeConverter.Display(device.DeviceType), device.Vendor }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        private Border AddNode(double x, double y, string title, string detail, string brushKey, NetworkDevice? device)
        {
            var accent = new Border { Height = 2 };
            accent.SetResourceReference(Border.BackgroundProperty, brushKey);

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 2, 6, 0)
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "MenuTextBrush");

            var detailBlock = new TextBlock
            {
                Text = detail,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 6, 2)
            };
            detailBlock.SetResourceReference(TextBlock.ForegroundProperty, "MenuTextBrush");
            detailBlock.Opacity = 0.78;

            var stack = new StackPanel();
            stack.Children.Add(accent);
            stack.Children.Add(titleBlock);
            stack.Children.Add(detailBlock);

            var border = new Border
            {
                Width = TopologyNodeWidth,
                Height = TopologyNodeHeight,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Child = stack,
                Tag = device
            };
            border.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "MenuBorderBrush");
            if (device != null)
            {
                border.Cursor = Cursors.Hand;
                border.MouseLeftButtonDown += TopologyNode_Click;
                border.MouseMove += TopologyNode_MouseMove;
                border.MouseLeftButtonUp += TopologyNode_MouseLeftButtonUp;
                border.PreviewMouseRightButtonDown += TopologyNode_RightClick;
                border.MouseEnter += TopologyNode_MouseEnter;
                border.MouseLeave += TopologyNode_MouseLeave;
            }

            Canvas.SetLeft(border, x - TopologyNodeWidth / 2);
            Canvas.SetTop(border, y - TopologyNodeHeight / 2);
            Panel.SetZIndex(border, 2);
            TopologyCanvas.Children.Add(border);
            UpdateTopologyNodeSelection(border);
            return border;
        }

        private void TopologyNode_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: NetworkDevice device } border) return;
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            bool selected = ResultsGrid.SelectedItems.Contains(device);
            if (ctrl)
            {
                if (selected) ResultsGrid.SelectedItems.Remove(device);
                else ResultsGrid.SelectedItems.Add(device);
            }
            else if (!selected)
            {
                ResultsGrid.SelectedItems.Clear();
                ResultsGrid.SelectedItems.Add(device);
            }

            UpdateTopologySelectionVisuals();
            _topologyDragStarts.Clear();
            if (ResultsGrid.SelectedItems.Contains(device))
            {
                foreach (var node in TopologyCanvas.Children.OfType<Border>()
                             .Where(n => n.Tag is NetworkDevice d && ResultsGrid.SelectedItems.Contains(d)))
                    _topologyDragStarts[node] = new Point(Canvas.GetLeft(node), Canvas.GetTop(node));
                _topologyDragMouseStart = e.GetPosition(TopologyCanvas);
                border.CaptureMouse();
            }
            e.Handled = true;
        }

        private void TopologyNode_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Border border || !border.IsMouseCaptured ||
                e.LeftButton != MouseButtonState.Pressed || _topologyDragStarts.Count == 0) return;

            Point now = e.GetPosition(TopologyCanvas);
            Vector delta = now - _topologyDragMouseStart;
            foreach (var item in _topologyDragStarts)
            {
                double left = Math.Max(0, Math.Min(TopologyCanvas.Width - TopologyNodeWidth,
                    item.Value.X + delta.X));
                double top = Math.Max(0, Math.Min(TopologyCanvas.Height - TopologyNodeHeight,
                    item.Value.Y + delta.Y));
                Canvas.SetLeft(item.Key, left);
                Canvas.SetTop(item.Key, top);

                if (item.Key.Tag is not NetworkDevice device) continue;
                var center = new Point(left + TopologyNodeWidth / 2, top + TopologyNodeHeight / 2);
                _topologyPositions[device.IpAddress] = center;
                if (_topologyLinks.TryGetValue(device.IpAddress, out var link))
                {
                    link.X2 = center.X;
                    link.Y2 = center.Y;
                }
            }
            e.Handled = true;
        }

        private void TopologyNode_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.IsMouseCaptured) border.ReleaseMouseCapture();
            _topologyDragStarts.Clear();
            e.Handled = true;
        }

        private void TopologyNode_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: NetworkDevice device }) return;
            if (!ResultsGrid.SelectedItems.Contains(device))
            {
                ResultsGrid.SelectedItems.Clear();
                ResultsGrid.SelectedItems.Add(device);
                UpdateTopologySelectionVisuals();
            }
            ResultsGrid.ScrollIntoView(device);
            PrepareDeviceContextMenu();
            if (ResultsGrid.ContextMenu != null)
            {
                ResultsGrid.ContextMenu.PlacementTarget = (Border)sender;
                ResultsGrid.ContextMenu.Placement =
                    System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                ResultsGrid.ContextMenu.IsOpen = true;
            }
            e.Handled = true;
        }

        private void UpdateTopologySelectionVisuals()
        {
            foreach (var border in TopologyCanvas.Children.OfType<Border>())
                UpdateTopologyNodeSelection(border);
        }

        private void UpdateTopologyNodeSelection(Border border)
        {
            bool selected = border.Tag is NetworkDevice device && ResultsGrid.SelectedItems.Contains(device);
            border.BorderThickness = new Thickness(selected ? 2 : 1);
            border.SetResourceReference(Border.BorderBrushProperty,
                selected ? "PrimaryBrush" : "MenuBorderBrush");
        }

        private static void TopologyNode_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is Border border)
                border.SetResourceReference(Border.BackgroundProperty, "MenuHoverBrush");
        }

        private static void TopologyNode_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is Border border)
                border.SetResourceReference(Border.BackgroundProperty, "MenuBackgroundBrush");
        }

        private static string DeviceBrush(string type) => type switch
        {
            "Router" or "Router/DNS" => "TypeRouter",
            "Windows" or "Windows Server" => "TypeWindows",
            "Printer" => "TypePrinter",
            "Switch/AP" or "Apple Device" or "Apple TV" => "TypeSwitch",
            "Hypervisor" => "TypeHypervisor",
            "NAS" => "TypeNas",
            "IoT" => "TypeIot",
            "Server" => "TypeServer",
            "Linux/SSH" => "TypeLinux",
            "DNS Server" => "TypeDns",
            "Home Assistant" => "TypeHa",
            "Mobile" => "TypeMobile",
            "Camera" => "TypeCamera",
            "Smart TV" => "TypeSmarttv",
            "Media Streamer" => "TypeMedia",
            _ => "MutedTextBrush"
        };
    }
}
