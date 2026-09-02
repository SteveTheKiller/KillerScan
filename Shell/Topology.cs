using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerScan.Models;

namespace KillerScan.Shell
{
    // The first topology layer uses only facts the scanner already knows. It shows membership in
    // the scanned local network, not physical switch ports or measured route hops. Those stronger
    // evidence types can be added without changing the view's basic node and link vocabulary.
    public partial class MainWindow
    {
        private const double TopologyNodeWidth = 126;
        private const double TopologyNodeHeight = 50;
        private bool _showTopology;

        private void TopologyButton_Click(object sender, RoutedEventArgs e)
        {
            _showTopology = !_showTopology;
            ResultsGrid.Visibility = _showTopology ? Visibility.Collapsed : Visibility.Visible;
            TopologyPane.Visibility = _showTopology ? Visibility.Visible : Visibility.Collapsed;
            TopologyButton.Tag = _showTopology ? "on" : null;
            FixedTopologyButton.Tag = _showTopology ? "on" : null;
            PaneTitle.Text = Loc(_showTopology ? "Str_Topology_Title" : "Str_DiscoveredDevices");
            if (_showTopology) RefreshTopology();
        }

        private void TopologyPane_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_showTopology) RefreshTopology();
        }

        private void RefreshTopology()
        {
            if (TopologyCanvas == null || TopologyPane == null || !_showTopology) return;

            double width = Math.Max(TopologyPane.ActualWidth, 640);
            var visible = _filteredView?.Cast<object>().OfType<NetworkDevice>().ToList()
                          ?? ActiveDevices.ToList();
            string localIp = LocalIpLabel?.Text ?? string.Empty;
            string gatewayIp = GatewayLabel?.Text ?? string.Empty;
            string dnsIp = DnsLabel?.Text ?? string.Empty;

            var regular = visible.Where(d => !SameIp(d.IpAddress, localIp)
                                          && !SameIp(d.IpAddress, gatewayIp)
                                          && !SameIp(d.IpAddress, dnsIp)).ToList();
            int columns = Math.Max(1, (int)((width - 36) / (TopologyNodeWidth + 22)));
            int rows = (int)Math.Ceiling(regular.Count / (double)columns);
            double height = Math.Max(TopologyPane.ActualHeight, 235 + rows * 76);
            TopologyCanvas.Width = width;
            TopologyCanvas.Height = height;
            TopologyCanvas.Children.Clear();

            var center = new Point(width / 2, 138);

            var gateway = new Point(center.X, 42);
            var local = new Point(Math.Max(76, center.X - 190), center.Y);
            DrawInferredLink(center, gateway);
            DrawInferredLink(center, local);
            AddRoleNode(gateway.X, gateway.Y, Loc("Str_Lbl_Gateway"), gatewayIp, "TypeRouter");
            AddRoleNode(local.X, local.Y, Loc("Str_Lbl_Local"), localIp, "PrimaryBrush");

            if (!string.IsNullOrWhiteSpace(dnsIp) && dnsIp != "--" && !SameIp(dnsIp, gatewayIp))
            {
                var dns = new Point(Math.Min(width - 76, center.X + 190), center.Y);
                DrawInferredLink(center, dns);
                AddRoleNode(dns.X, dns.Y, Loc("Str_Lbl_Dns"), dnsIp, "TypeDns");
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

            for (int i = 0; i < regular.Count; i++)
            {
                int row = i / columns;
                int column = i % columns;
                int rowCount = Math.Min(columns, regular.Count - row * columns);
                double rowWidth = rowCount * TopologyNodeWidth + (rowCount - 1) * 22;
                double left = (width - rowWidth) / 2 + TopologyNodeWidth / 2;
                var point = new Point(left + column * (TopologyNodeWidth + 22), 235 + row * 76);
                DrawInferredLink(center, point);
                AddDeviceNode(point.X, point.Y, regular[i]);
            }
        }

        private static bool SameIp(string left, string right) =>
            !string.IsNullOrWhiteSpace(left) && left != "--" &&
            string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        private void DrawInferredLink(Point from, Point to)
        {
            var line = new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 4 },
                Opacity = 0.65,
                IsHitTestVisible = false
            };
            line.SetResourceReference(Shape.StrokeProperty, "MutedTextBrush");
            TopologyCanvas.Children.Add(line);
        }

        private void AddNetworkNode(double x, double y)
        {
            string subnet = string.IsNullOrWhiteSpace(ActiveSubnet) ? SubnetInput.Text : ActiveSubnet;
            AddNode(x, y, Loc("Str_Topology_Network"), subnet, "PrimaryBrush", null);
        }

        private void AddRoleNode(double x, double y, string role, string value, string brushKey)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "--") value = Loc("Str_Dev_Unknown");
            AddNode(x, y, role, value, brushKey, null);
        }

        private void AddDeviceNode(double x, double y, NetworkDevice device)
        {
            string title = string.IsNullOrWhiteSpace(device.Hostname)
                ? Controls.DeviceTypeConverter.Display(device.DeviceType)
                : device.Hostname;
            string brush = DeviceBrush(device.DeviceType);
            var node = AddNode(x, y, title, device.IpAddress, brush, device);
            node.ToolTip = string.Join(Environment.NewLine,
                new[] { title, device.IpAddress, Controls.DeviceTypeConverter.Display(device.DeviceType), device.Vendor }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        private Border AddNode(double x, double y, string title, string detail, string brushKey, NetworkDevice? device)
        {
            var accent = new Border { Height = 3 };
            accent.SetResourceReference(Border.BackgroundProperty, brushKey);

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(7, 4, 7, 0)
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

            var detailBlock = new TextBlock
            {
                Text = detail,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(7, 1, 7, 4)
            };
            detailBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

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
            border.SetResourceReference(Border.BackgroundProperty, "TextFieldBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");
            if (device != null)
            {
                border.Cursor = Cursors.Hand;
                border.MouseLeftButtonDown += TopologyNode_Click;
            }

            Canvas.SetLeft(border, x - TopologyNodeWidth / 2);
            Canvas.SetTop(border, y - TopologyNodeHeight / 2);
            Panel.SetZIndex(border, 2);
            TopologyCanvas.Children.Add(border);
            return border;
        }

        private void TopologyNode_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Border { Tag: NetworkDevice device }) return;
            ResultsGrid.SelectedItem = device;
            ResultsGrid.ScrollIntoView(device);
            e.Handled = true;
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
