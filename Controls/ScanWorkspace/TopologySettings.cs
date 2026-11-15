using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private static readonly Dictionary<string, string[]> TopologyChoices = new()
        {
            ["Pattern"] = ["Hierarchy", "Radial"],
            ["Orientation"] = ["Vertical", "Horizontal"],
            ["NodeScale"] = ["75", "100", "125", "150", "200"],
            ["Font"] = ["Default", "Segoe UI", "Arial", "Consolas", "Tahoma"],
            ["FontSize"] = ["10", "11", "12", "14", "16", "18"],
            ["Links"] = ["On", "Off"]
        };
        private readonly Dictionary<string, string> _topologyOptions = new()
        {
            ["Pattern"] = "Hierarchy", ["Orientation"] = "Vertical", ["NodeScale"] = "100",
            ["Font"] = "Default", ["FontSize"] = "11", ["Links"] = "On"
        };

        private double TopologyScale => double.Parse(_topologyOptions["NodeScale"], CultureInfo.InvariantCulture) / 100;
        private double TopologyFontSize => double.Parse(_topologyOptions["FontSize"], CultureInfo.InvariantCulture);
        private double DefaultTopologyNodeWidth => 126 * TopologyScale;
        private double TopologyNodeWidth => Math.Max(DefaultTopologyNodeWidth, _topologyCardSize.Width);
        // Just the two lines, the 3px ring and 2px of air: no fixed floor, so a card only grows
        // when the topology font does.
        private double DefaultTopologyNodeHeight => Math.Ceiling(TopologyFontSize * 2.66 + 7);
        private double TopologyNodeHeight => Math.Max(DefaultTopologyNodeHeight, _topologyCardSize.Height);

        /// <summary>The largest card the last build measured. Cards size to their text, so a long
        /// name is wider than the default slot; the layout spaces by this so none overlap or leave
        /// the canvas (RefreshTopology).</summary>
        private Size _topologyCardSize;
        private bool TopologyHorizontal => _topologyOptions["Orientation"] == "Horizontal";

        private void LoadTopologySettings()
        {
            foreach (string name in TopologyChoices.Keys)
            {
                string? value = App.GetSetting("Topology" + name);
                if (value != null && TopologyChoices[name].Contains(value)) _topologyOptions[name] = value;
            }
            var menu = TopologyOrderButton.ContextMenu;
            menu.Items.Add(KillerFamily.Controls.CardBuilder.Header(Loc("Str_Topology_Settings")));
            // Order as a segment: one of four, each on its Ctrl+digit. Built here rather than
            // through CardBuilder.SegmentRow so the checked segment follows the shortcuts.
            var track = new StackPanel { Orientation = Orientation.Horizontal };
            string segmentGroup = "TopologyOrder" + Guid.NewGuid().ToString("N");
            foreach (var (order, key, gesture) in new[]
            {
                (TopologyOrder.Role, "Str_Topology_Role", "Ctrl+1"), (TopologyOrder.Type, "Str_Col_Type", "Ctrl+2"),
                (TopologyOrder.Ip, "Str_Col_Ip", "Ctrl+3"), (TopologyOrder.Vendor, "Str_Col_Vendor", "Ctrl+4")
            })
            {
                var segment = new System.Windows.Controls.RadioButton { GroupName = segmentGroup, Tag = order };
                segment.SetResourceReference(StyleProperty, "CardSegment");
                segment.SetResourceReference(ContentControl.ContentProperty, key);
                segment.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, key);
                System.Windows.Automation.AutomationProperties.SetAcceleratorKey(segment, gesture);
                var tip = new TextBlock();
                var name = new System.Windows.Documents.Run();
                name.SetResourceReference(System.Windows.Documents.Run.TextProperty, key);
                tip.Inlines.Add(name);
                tip.Inlines.Add(new System.Windows.Documents.Run(" (" + gesture + ")"));
                segment.ToolTip = tip;
                segment.Click += (_, _) => { KillerFamily.Controls.CardBuilder.Close(segment); if (_topologyOrder != order) SetTopologyOrder(order); };
                _topologyOrderSegments.Add(segment);
                track.Children.Add(segment);
            }
            var trackBorder = new Border { HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(2), Child = track };
            trackBorder.SetResourceReference(Border.BackgroundProperty, "MenuHoverBrush");
            trackBorder.SetResourceReference(Border.CornerRadiusProperty, "ControlCornerRadius");
            var orderRow = KillerFamily.Controls.CardBuilder.Labeled(Loc("Str_Topology_Order"), trackBorder);
            menu.Items.Add(orderRow);
            menu.Opened += (_, _) =>
            {
                // The labels follow a language switch; the row is built once.
                if (orderRow.Header is Panel row && row.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock label)
                    label.Text = Loc("Str_Topology_Order");
            };
            foreach (string name in TopologyChoices.Keys.Where(name => name != "Links"))
            {
                int glyph = name switch { "Pattern" => 0xE968, "Orientation" => 0xE7AD, "NodeScale" => 0xE799, "FontSize" => 0xE8E9, _ => 0xE8D2 };
                var group = new MenuItem { Icon = MenuGlyph.Create(glyph) };
                group.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Topology_" + name);
                foreach (string value in TopologyChoices[name])
                {
                    var item = new MenuItem { Tag = name + "=" + value, IsCheckable = true, Icon = MenuGlyph.Create(glyph) };
                    if (name is "Pattern" or "Orientation" || value == "Default")
                        item.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Topology_" + value);
                    else item.Header = name == "NodeScale" ? value + "%" : value;
                    item.Click += TopologySetting_Click;
                    group.Items.Add(item);
                }
                menu.Items.Add(group);
            }
            AddTopologyZoomMenu(menu);
            var links = new MenuItem { Tag = "Links", IsCheckable = true, Icon = MenuGlyph.Create(0xE71B) };
            links.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Topology_Links");
            links.Click += TopologySetting_Click;
            menu.Items.Add(links);
            var reset = new MenuItem { Icon = MenuGlyph.Create(0xE72C) };
            reset.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Topology_Reset");
            reset.Click += TopologyReset_Click;
            menu.Items.Add(reset);
            // The three picture formats on a strip, each with its own icon and its shortcut,
            // the same three the export button offers in this view.
            MenuItem Strip() => KillerFamily.Controls.CardBuilder.QuickStrip(Loc("Str_Card_Export"), new[]
            {
                new KillerFamily.Controls.CardAction("", "PNG", Loc("Str_Export_SnapPngAlpha"), "Ctrl+Shift+G",
                    () => ExportSnapshotPngAlpha_Click(this, new RoutedEventArgs())),
                new KillerFamily.Controls.CardAction("", "JPG", Loc("Str_Export_SnapJpeg"), "Ctrl+Shift+J",
                    () => ExportSnapshotJpeg_Click(this, new RoutedEventArgs())),
                new KillerFamily.Controls.CardAction("", "HTML", Loc("Str_Export_SnapSvg"), "Ctrl+Alt+H",
                    () => ExportSnapshotSvg_Click(this, new RoutedEventArgs())),
            });
            var strip = Strip();
            menu.Items.Add(strip);
            // Rebuilt as the card opens, so its tooltips follow the current language.
            menu.Opened += (_, _) =>
            {
                int at = menu.Items.IndexOf(strip);
                if (at < 0) return;
                strip = Strip();
                menu.Items[at] = strip;
            };
        }

        private readonly List<System.Windows.Controls.RadioButton> _topologyOrderSegments = [];

        private void TopologySetting_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag }) return;
            if (tag == "Links") _topologyOptions["Links"] = _topologyOptions["Links"] == "On" ? "Off" : "On";
            else
            {
                string[] parts = tag.Split('=');
                if (parts.Length != 2 || !TopologyChoices.TryGetValue(parts[0], out var choices) ||
                    !choices.Contains(parts[1])) return;
                _topologyOptions[parts[0]] = parts[1];
            }
            SaveTopologySettings(tag != "Links" && !tag.StartsWith("Font=", StringComparison.Ordinal));
        }

        private void TopologyReset_Click(object sender, RoutedEventArgs e)
        {
            _topologyOrder = TopologyOrder.Role;
            foreach (string name in TopologyChoices.Keys) _topologyOptions[name] = TopologyChoices[name][0];
            _topologyOptions["NodeScale"] = "100";
            _topologyOptions["FontSize"] = "11";
            SaveTopologySettings(true);
            SetTopologyZoom(1, new Point());
            TopologyScrollViewer.ScrollToHorizontalOffset(0);
            TopologyScrollViewer.ScrollToVerticalOffset(0);
        }

        private void SaveTopologySettings(bool resetPositions)
        {
            if (!DemoData.Enabled)
            {
                foreach (var option in _topologyOptions) App.SetSetting("Topology" + option.Key, option.Value);
                App.SetSetting("TopologyOrder", _topologyOrder.ToString());
            }
            if (resetPositions) _topologyPositions.Clear();
            UpdateTopologyOrderUi();
            RefreshTopology();
        }

        private void UpdateTopologySettingsUi()
        {
            foreach (var group in TopologyOrderButton.ContextMenu.Items.OfType<MenuItem>())
                foreach (var item in group.Items.OfType<MenuItem>().Concat([group]))
                    if (item.Tag is string tag)
                    {
                        if (tag == "Links") item.IsChecked = _topologyOptions["Links"] == "On";
                        else
                        {
                            string[] parts = tag.Split('=');
                            if (parts.Length == 2 && _topologyOptions.TryGetValue(parts[0], out string value))
                                item.IsChecked = value == parts[1];
                        }
                    }
        }

        private void TopologyBackground_RightClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && source != TopologyCanvas)
            {
                if (source is Border { Tag: NetworkDevice }) return;
                source = VisualTreeHelper.GetParent(source);
            }
            ResultsGrid.UnselectAll();
            UpdateTopologySelectionVisuals();
            TopologyCanvas.Focus();
            UpdateTopologyOrderUi();
            var menu = TopologyOrderButton.ContextMenu;
            menu.PlacementTarget = TopologyScrollViewer;
            menu.PlacementRectangle = new Rect(e.GetPosition(TopologyScrollViewer), new Size());
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }
}
