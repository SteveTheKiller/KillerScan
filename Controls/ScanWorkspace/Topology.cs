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
            LoadTopologySettings();
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
            if (!Services.DemoData.Enabled) App.SetSetting("TopologyOrder", order.ToString());
            UpdateTopologyOrderUi();
            RefreshTopology();
        }

        private void UpdateTopologyOrderUi()
        {
            TopologyRoleItem.IsChecked = _topologyOrder == TopologyOrder.Role;
            TopologyTypeItem.IsChecked = _topologyOrder == TopologyOrder.Type;
            TopologyIpItem.IsChecked = _topologyOrder == TopologyOrder.Ip;
            TopologyVendorItem.IsChecked = _topologyOrder == TopologyOrder.Vendor;
            UpdateTopologySettingsUi();
        }

        private void TopologyPane_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_showTopology) RefreshTopology();
        }

        private void RefreshTopology()
        {
            if (TopologyCanvas == null || TopologyPane == null || !_showTopology) return;

            double scale = TopologyScale;
            double viewportWidth = Math.Max(1, TopologyPane.ActualWidth - SystemParameters.VerticalScrollBarWidth);
            double viewportHeight = Math.Max(1, TopologyPane.ActualHeight - SystemParameters.HorizontalScrollBarHeight);
            double nodeCross = TopologyHorizontal ? TopologyNodeHeight : TopologyNodeWidth;
            double nodeDepth = TopologyHorizontal ? TopologyNodeWidth : TopologyNodeHeight;
            double cross = Math.Max(TopologyHorizontal ? viewportHeight : viewportWidth,
                Math.Max(640 * scale, 3 * nodeCross + 154 * scale));
            Point Orient(double across, double depth) => TopologyHorizontal ? new Point(depth, across) : new Point(across, depth);
            List<NetworkDevice> visible =
                [.. (_filteredView?.Cast<object>().OfType<NetworkDevice>() ?? ActiveDevices)];
            string localIp = LocalIpLabel?.Text ?? string.Empty;
            string gatewayIp = GatewayLabel?.Text ?? string.Empty;
            string dnsIp = DnsLabel?.Text ?? string.Empty;

            var regular = visible.Where(d => !SameIp(d.IpAddress, localIp)
                                          && !SameIp(d.IpAddress, gatewayIp)
                                          && !SameIp(d.IpAddress, dnsIp)).ToList();
            int columns = Math.Max(1, (int)((cross - 36 * scale) / (nodeCross + 22 * scale)));
            var deviceRows = BuildDeviceRows(regular, columns, _topologyOrder);
            int groupGaps = Math.Max(0, deviceRows.Count(r => r.StartsGroup) - 1);
            double gatewayDepth = nodeDepth / 2 + 22 * scale;
            double centerDepth = gatewayDepth + nodeDepth + 56 * scale;
            double rowStart = centerDepth + nodeDepth + 40 * scale;
            double depth = Math.Max(TopologyHorizontal ? viewportWidth : viewportHeight,
                rowStart + deviceRows.Count * (nodeDepth + 16 * scale) + groupGaps * 10 * scale);
            bool radial = _topologyOptions["Pattern"] == "Radial";
            double width = TopologyHorizontal ? depth : cross;
            double height = TopologyHorizontal ? cross : depth;
            Point[] radialPoints = [];
            var center = Orient(cross / 2, centerDepth);
            if (radial)
            {
                Point RelativeRole(double across, double along) => TopologyHorizontal
                    ? new Point(along, across) : new Point(across, along);
                Rect Box(Point p) => new Rect(p.X - TopologyNodeWidth / 2, p.Y - TopologyNodeHeight / 2,
                    TopologyNodeWidth, TopologyNodeHeight);
                var reserved = new List<Rect>
                {
                    Box(new Point()), Box(RelativeRole(0, -(nodeDepth + 56 * scale))),
                    Box(RelativeRole(-(nodeCross + 64 * scale), 0))
                };
                if (!string.IsNullOrWhiteSpace(dnsIp) && dnsIp != "--" && !SameIp(dnsIp, gatewayIp))
                    reserved.Add(Box(RelativeRole(nodeCross + 64 * scale, 0)));
                radialPoints = TopologyGeometry.Radial(regular.Select(_ => new Size(TopologyNodeWidth, TopologyNodeHeight)).ToArray(),
                    reserved, viewportWidth, viewportHeight, 12 * scale, TopologyHorizontal);
                var bounds = reserved[0];
                foreach (var box in reserved.Skip(1).Concat(radialPoints.Select(Box))) bounds.Union(box);
                width = Math.Max(viewportWidth, bounds.Width + 44 * scale);
                height = Math.Max(viewportHeight, bounds.Height + 44 * scale);
                center = new Point((width - bounds.Width) / 2 - bounds.Left, (height - bounds.Height) / 2 - bounds.Top);
            }
            TopologyCanvas.Width = width;
            TopologyCanvas.Height = height;
            TopologyCanvas.Children.Clear();
            _topologyLinks.Clear();

            Point RolePoint(double across, double relativeDepth) => radial
                ? new Point(center.X + (TopologyHorizontal ? relativeDepth : across),
                    center.Y + (TopologyHorizontal ? across : relativeDepth))
                : Orient(cross / 2 + across, centerDepth + relativeDepth);
            Point Positioned(NetworkDevice? device, Point point) =>
                device != null && _topologyPositions.TryGetValue(device.IpAddress, out var saved)
                    ? new Point(Math.Max(TopologyNodeWidth / 2, Math.Min(width - TopologyNodeWidth / 2, saved.X)),
                        Math.Max(TopologyNodeHeight / 2, Math.Min(height - TopologyNodeHeight / 2, saved.Y)))
                    : point;

            var gateway = RolePoint(0, -(nodeDepth + 56 * scale));
            var local = RolePoint(-(nodeCross + 64 * scale), 0);
            var gatewayDevice = visible.FirstOrDefault(d => SameIp(d.IpAddress, gatewayIp));
            var localDevice = visible.FirstOrDefault(d => SameIp(d.IpAddress, localIp));
            gateway = Positioned(gatewayDevice, gateway);
            local = Positioned(localDevice, local);
            var gatewayLink = DrawInferredLink(center, gateway);
            var localLink = DrawInferredLink(center, local);
            AddRoleNode(gateway.X, gateway.Y, Loc("Str_Lbl_Gateway"), gatewayIp, "TypeRouter",
                gatewayDevice, gatewayLink);
            AddRoleNode(local.X, local.Y, Loc("Str_Lbl_Local"), localIp, "PrimaryBrush",
                localDevice, localLink);

            if (!string.IsNullOrWhiteSpace(dnsIp) && dnsIp != "--" && !SameIp(dnsIp, gatewayIp))
            {
                var dns = RolePoint(nodeCross + 64 * scale, 0);
                var dnsDevice = visible.FirstOrDefault(d => SameIp(d.IpAddress, dnsIp));
                dns = Positioned(dnsDevice, dns);
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
                    FontSize = TopologyFontSize,
                    TextAlignment = TextAlignment.Center,
                    Width = 300
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                Canvas.SetLeft(empty, center.X - 150);
                Canvas.SetTop(empty, center.Y + TopologyNodeHeight / 2 + 10 * scale);
                TopologyCanvas.Children.Add(empty);
                return;
            }

            if (radial)
            {
                var ordered = deviceRows.SelectMany(row => row.Devices).ToList();
                for (int index = 0; index < ordered.Count; index++)
                {
                    var device = ordered[index];
                    var point = Positioned(device, new Point(center.X + radialPoints[index].X, center.Y + radialPoints[index].Y));
                    var link = DrawInferredLink(center, point);
                    AddDeviceNode(point.X, point.Y, device, link);
                }
                return;
            }

            double rowY = rowStart;
            for (int rowIndex = 0; rowIndex < deviceRows.Count; rowIndex++)
            {
                var (rowDevices, startsGroup) = deviceRows[rowIndex];
                if (rowIndex > 0 && startsGroup) rowY += 10 * scale;
                int rowCount = rowDevices.Count;
                double rowWidth = rowCount * nodeCross + (rowCount - 1) * 22 * scale;
                double left = (cross - rowWidth) / 2 + nodeCross / 2;
                for (int column = 0; column < rowCount; column++)
                {
                    var point = Orient(left + column * (nodeCross + 22 * scale), rowY);
                    var device = rowDevices[column];
                    point = Positioned(device, point);
                    var link = DrawInferredLink(center, point);
                    AddDeviceNode(point.X, point.Y, device, link);
                }
                rowY += nodeDepth + 16 * scale;
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
                Opacity = _topologyOptions["Links"] == "On" ? 0.65 : 0,
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
                FontSize = TopologyFontSize,
                FontFamily = _topologyOptions["Font"] == "Default" ? FontFamily : new FontFamily(_topologyOptions["Font"]),
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 2, 6, 0)
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TopologyNodeTextBrush");

            var detailBlock = new TextBlock
            {
                Text = detail,
                FontFamily = new FontFamily(_topologyOptions["Font"] == "Default" ? "Consolas" : _topologyOptions["Font"]),
                FontSize = TopologyFontSize - 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 6, 2)
            };
            detailBlock.SetResourceReference(TextBlock.ForegroundProperty, "TopologyNodeTextBrush");
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
            // Topology nodes have their own two keys rather than borrowing the menu surface. A
            // node has to separate from the pane behind it, which a menu never has to do, and on
            // the near-black themes those two needs pull in opposite directions. Themes that do
            // not set them fall back to the menu brushes, so nothing else moves.
            border.SetResourceReference(Border.BackgroundProperty, "TopologyNodeBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "TopologyNodeBorderBrush");
            if (device != null)
            {
                border.Cursor = DragCursors.Open;   // a node is picked up and carried
                border.MouseLeftButtonDown += TopologyNode_Click;
                border.MouseMove += TopologyNode_MouseMove;
                border.MouseLeftButtonUp += TopologyNode_MouseLeftButtonUp;
                border.LostMouseCapture += (_, _) => DragCursors.EndDrag();
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
                DragCursors.BeginDrag();
            }
            e.Handled = true;
        }

        private void TopologyNode_MouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not Border border || !border.IsMouseCaptured ||
                e.LeftButton != MouseButtonState.Pressed || _topologyDragStarts.Count == 0) return;

            Point now = e.GetPosition(TopologyCanvas);
            Vector delta = now - _topologyDragMouseStart;
            MoveTopologyNodes(delta);
            e.Handled = true;
        }

        private void MoveTopologyNodes(Vector delta)
        {
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
        }

        private void TopologyNode_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.IsMouseCaptured) border.ReleaseMouseCapture();
            _topologyDragStarts.Clear();
            DragCursors.EndDrag();
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
            ResultsGrid.CurrentItem = device;
            TopologyCanvas.Focus();
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
                selected ? "PrimaryBrush" : "TopologyNodeBorderBrush");
        }

        private static void TopologyNode_MouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is not Border border) return;
            border.SetResourceReference(Border.BackgroundProperty, "TopologyNodeHoverBrush");
            // The hover fill is a solid accent on some themes, navy on 98SE, so the label has to
            // move with it. Themes that hover with a subtle tint set the same brush for both and
            // nothing appears to change.
            SetNodeText(border, "TopologyNodeHoverTextBrush");
        }

        private static void TopologyNode_MouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is not Border border) return;
            border.SetResourceReference(Border.BackgroundProperty, "TopologyNodeBrush");
            SetNodeText(border, "TopologyNodeTextBrush");
        }

        private static void SetNodeText(Border border, string key)
        {
            if (border.Child is not StackPanel stack) return;
            foreach (var block in stack.Children.OfType<TextBlock>())
                block.SetResourceReference(TextBlock.ForegroundProperty, key);
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
