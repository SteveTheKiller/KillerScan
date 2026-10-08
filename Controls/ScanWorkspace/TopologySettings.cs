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
        private double TopologyNodeWidth => 126 * TopologyScale;
        private double TopologyNodeHeight => Math.Max(40 * TopologyScale, TopologyFontSize * 2.8 + 9);
        private bool TopologyHorizontal => _topologyOptions["Orientation"] == "Horizontal";

        private void LoadTopologySettings()
        {
            foreach (string name in TopologyChoices.Keys)
            {
                string? value = App.GetSetting("Topology" + name);
                if (value != null && TopologyChoices[name].Contains(value)) _topologyOptions[name] = value;
            }
            var menu = TopologyOrderButton.ContextMenu;
            foreach (string name in TopologyChoices.Keys.Where(name => name != "Links"))
            {
                var group = new MenuItem();
                group.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Topology_" + name);
                foreach (string value in TopologyChoices[name])
                {
                    var item = new MenuItem { Tag = name + "=" + value, IsCheckable = true };
                    if (name is "Pattern" or "Orientation" || value == "Default")
                        item.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Topology_" + value);
                    else item.Header = name == "NodeScale" ? value + "%" : value;
                    item.Click += TopologySetting_Click;
                    group.Items.Add(item);
                }
                menu.Items.Add(group);
            }
            AddTopologyZoomMenu(menu);
            var links = new MenuItem { Tag = "Links", IsCheckable = true };
            links.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Topology_Links");
            links.Click += TopologySetting_Click;
            menu.Items.Add(links);
            var reset = new MenuItem();
            reset.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Topology_Reset");
            reset.Click += TopologyReset_Click;
            menu.Items.Add(reset);
            menu.Items.Add(new Separator());
            var export = new MenuItem();
            export.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_TT_Export");
            foreach (var entry in new (string Key, RoutedEventHandler Handler)[]
            {
                ("Str_Export_SnapPngAlpha", ExportSnapshotPngAlpha_Click),
                ("Str_Export_SnapJpeg", ExportSnapshotJpeg_Click),
                ("Str_Export_SnapSvg", ExportSnapshotSvg_Click)
            })
            {
                var item = new MenuItem();
                item.SetResourceReference(HeaderedItemsControl.HeaderProperty, entry.Key);
                item.Click += entry.Handler;
                export.Items.Add(item);
            }
            menu.Items.Add(export);
        }

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
                foreach (var item in group.Items.OfType<MenuItem>().Concat(new[] { group }))
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
            UpdateTopologyOrderUi();
            var menu = TopologyOrderButton.ContextMenu;
            menu.PlacementTarget = TopologyCanvas;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }
}
