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
            TopologyOrderButton.ContextMenuOpening += (_, _) => PrepareTopologyCard();
            PrepareTopologyCard();
        }

        /// <summary>
        /// The topology card: the four orders as checked tiles on Ctrl+1 to Ctrl+4, the layout choices as
        /// flyout tiles, Links and Reset, then the three picture formats. Built as it opens, so the checks
        /// follow the shortcuts and the captions follow the current language.
        /// </summary>
        internal void PrepareTopologyCard()
        {
            var menu = TopologyOrderButton.ContextMenu;
            if (menu == null) return;
            var card = ScanCards.Prepare(menu);
            card.Items.Add(ScanCards.Header(card, Loc("Str_Topology_Settings"), Loc("Str_Topology_Order")));
            var order = new[]
            {
                (TopologyOrder.Role, "Str_Topology_Role", "Ctrl+1", "\uE902"), (TopologyOrder.Type, "Str_Col_Type", "Ctrl+2", "\uE8FD"),
                (TopologyOrder.Ip, "Str_Col_Ip", "Ctrl+3", "\uE968"), (TopologyOrder.Vendor, "Str_Col_Vendor", "Ctrl+4", "\uE821"),
            }.Select(o => new KillerFamily.Controls.CardTile(o.Item4, Loc(o.Item2), o.Item3,
                () => { if (_topologyOrder != o.Item1) SetTopologyOrder(o.Item1); })
                { Id = "Order" + o.Item1, Checked = _topologyOrder == o.Item1 }).ToList();

            var layout = new List<KillerFamily.Controls.CardTile>();
            foreach (string name in TopologyChoices.Keys.Where(name => name != "Links"))
            {
                int glyph = name switch { "Pattern" => 0xE968, "Orientation" => 0xE7AD, "NodeScale" => 0xE799, "FontSize" => 0xE8E9, _ => 0xE8D2 };
                var rows = new List<object>();
                foreach (string value in TopologyChoices[name])
                {
                    var item = new MenuItem { Tag = name + "=" + value, IsCheckable = true, Icon = MenuGlyph.Create(glyph),
                        IsChecked = _topologyOptions.TryGetValue(name, out string? current) && current == value };
                    if (name is "Pattern" or "Orientation" || value == "Default")
                        item.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Topology_" + value);
                    else item.Header = name == "NodeScale" ? value + "%" : value;
                    item.Click += TopologySetting_Click;
                    rows.Add(item);
                }
                layout.Add(ScanCards.Flyout(char.ConvertFromUtf32(glyph), Loc("Str_Topology_" + name), null, rows) with { Id = name, Mirror = true });
            }
            layout.Add(ScanCards.Flyout("\uE71E", _topologyZoom.ToString("P0"), "Ctrl+Wheel", TopologyZoomRows()) with { Id = "Zoom", Mirror = true });
            layout.Add(new KillerFamily.Controls.CardTile("\uE71B", Loc("Str_Topology_Links"), null,
                () => TopologySetting_Click(new MenuItem { Tag = "Links" }, new RoutedEventArgs()))
                { Id = "Links", Checked = _topologyOptions["Links"] == "On" });
            layout.Add(new KillerFamily.Controls.CardTile("\uE72C", Loc("Str_Topology_Reset"), null,
                () => TopologyReset_Click(this, new RoutedEventArgs())) { Id = "Reset" });

            // The three picture formats, the same three the export button offers in this view.
            var export = new List<KillerFamily.Controls.CardTile>
            {
                new("\uE91B", Loc("Str_Export_SnapPngAlpha"), "Ctrl+Shift+G", () => ExportSnapshotPngAlpha_Click(this, new RoutedEventArgs())) { Id = "Png", Shown = "PNG" },
                new("\uEB9F", Loc("Str_Export_SnapJpeg"), "Ctrl+Shift+J", () => ExportSnapshotJpeg_Click(this, new RoutedEventArgs())) { Id = "Jpeg", Shown = "JPG" },
                new("\uE943", Loc("Str_Export_SnapSvg"), "Ctrl+Alt+H", () => ExportSnapshotSvg_Click(this, new RoutedEventArgs())) { Id = "Svg", Shown = "HTML" },
            };
            card.Items.Add(ScanCards.Grid(card, order, layout, export));
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

        // The card is built as it opens; one open while a shortcut changes a setting is rebuilt in place.
        private void UpdateTopologySettingsUi()
        {
            if (TopologyOrderButton.ContextMenu is { IsOpen: true }) PrepareTopologyCard();
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
            PrepareTopologyCard();
            var menu = TopologyOrderButton.ContextMenu;
            menu.PlacementTarget = TopologyScrollViewer;
            menu.PlacementRectangle = new Rect(e.GetPosition(TopologyScrollViewer), new Size());
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.RelativePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }
}
