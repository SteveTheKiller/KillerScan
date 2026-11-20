using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class HistoryWorkspace
    {
        internal event Action? CurrentScanRequested;
        internal event Action? SidebarRequested;
        private DataGrid VisibleGrid => _showAll ? HistoryAllGrid : HistoryChangesGrid;

        private void ConfigureActions()
        {
            foreach (var grid in new[] { HistoryChangesGrid, HistoryAllGrid })
            {
                var card = ScanCards.Menu();
                grid.ContextMenu = card;
                grid.ContextMenuOpening += (_, e) => { PrepareHistoryCard(card, grid); };
                grid.PreviewMouseRightButtonDown += (_, e) => SelectContextRow(grid, e.OriginalSource as DependencyObject);
            }
            var pane = ScanCards.Menu();
            ContextMenu = pane;
            ContextMenuOpening += (_, e) => { if (ReferenceEquals(e.Source, this) || e.Source is not DataGrid) PrepareHistoryCard(pane, null); };
        }

        private static void SelectContextRow(DataGrid grid, DependencyObject? source)
        {
            var row = source == null ? null : ItemsControl.ContainerFromElement(grid, source) as DataGridRow;
            if (row == null) { grid.UnselectAll(); return; }
            grid.SelectedItem = row.Item;
            grid.CurrentItem = row.Item;
            grid.Focus();
        }

        /// <summary>
        /// On a table, the saved-device card: the device's name and address over its copy tiles, then the
        /// view and export tiles. Nothing live is on it (no ping, rescan or connect): the row is a record of
        /// a past scan. The pane background has no device to copy from, so its card leaves the copy tiles off.
        /// Built as it opens, from the selection.
        /// </summary>
        internal void PrepareHistoryCard(ContextMenu menu, DataGrid? grid)
        {
            var card = ScanCards.Prepare(menu);
            var device = grid == null ? null : SelectedHistoryDevice;
            string title = device == null ? Loc("Str_History_Title")
                : string.IsNullOrWhiteSpace(device.Hostname) ? device.IpAddress : device.Hostname;
            string meta = device == null ? "" : string.Join("  ·  ", new[] { device.IpAddress, device.MacAddress, device.Vendor }
                .Where(part => !string.IsNullOrWhiteSpace(part) && part != title));
            card.Items.Add(ScanCards.Header(card, title, meta));
            // Each tile's Id is the action it runs, the same name HandleShortcut dispatches.
            KillerFamily.Controls.CardTile Tile(string action, string glyph, string key, string keys, string _, bool enabled) =>
                new(glyph, Loc(key), keys, () => ExecuteAction(action), false, enabled) { Id = action };
            var groups = new List<List<KillerFamily.Controls.CardTile>>();
            if (grid != null)
                groups.Add(
                [
                    Tile("ip", "\uE8C8", "Str_Ctx_CopyIp", "Ctrl+C", "ip", CopyValue("ip").Length > 0),
                    Tile("mac", "\uE8C8", "Str_Ctx_CopyMac", "Ctrl+Shift+C", "mac", CopyValue("mac").Length > 0),
                    Tile("host", "\uE8C8", "Str_Ctx_CopyHost", "Ctrl+Alt+C", "host", CopyValue("host").Length > 0),
                    Tile("details", "\uE8C8", "Str_History_CopyDetails", "Ctrl+Shift+Y", "details", CopyValue("details").Length > 0),
                ]);
            groups.Add(
            [
                Tile("export", "\uE9F9", "Str_Export_Csv", "Ctrl+E", "export", _entry != null && VisibleGrid.Items.Count > 0),
                Tile("changes", "\uE8FD", "Str_History_ViewChanges", "Ctrl+Shift+H", "changes", _entry != null && _showAll),
                Tile("all", "\uE772", "Str_History_ViewAll", "Ctrl+Shift+H", "all", _entry != null && !_showAll),
            ]);
            groups.Add(
            [
                Tile("sidebar", "\uE81C", "Str_History_Title", "Ctrl+H", "sidebar", true),
                Tile("current", "\uE772", "Str_View_Devices", "F6", "current", true),
            ]);
            card.Items.Add(ScanCards.Grid(card, groups.ToArray()));
        }

        private HistoricalDevice? SelectedHistoryDevice => VisibleGrid.SelectedItem switch
        {
            HistoryChangeRow row => row.Device,
            HistoryDeviceRow row => row.Device,
            _ => null
        };

        private string CopyValue(string action)
        {
            var device = SelectedHistoryDevice;
            if (device == null) return string.Empty;
            return (action switch
            {
                "ip" => device.IpAddress,
                "mac" => device.MacAddress,
                "host" => device.Hostname,
                "details" => string.Join("\t", device.IpAddress, device.Hostname, device.MacAddress,
                    device.Vendor, DeviceTypeConverter.Display(device.DeviceType), string.Join(", ", device.OpenPorts)),
                _ => string.Empty
            }) ?? string.Empty;
        }

        private async void ExecuteAction(string action)
        {
            if (action == "changes" || action == "all") { SetView(action == "all"); VisibleGrid.Focus(); return; }
            if (action == "current") { CurrentScanRequested?.Invoke(); return; }
            if (action == "sidebar") { SidebarRequested?.Invoke(); return; }
            if (action == "export") { ExportCsv(); return; }
            string value = CopyValue(action);
            if (value.Length == 0) return;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Clipboard.SetText(value); return; }
                catch (COMException)
                {
                    if (attempt == 4) { HistorySummary.Text = Loc("Str_Clipboard_Failed"); return; }
                    await Task.Delay(50);
                }
            }
        }

        private string BuildCsv()
        {
            static string Cell(string value) => ReportExport.CsvField(value);
            var lines = new List<string> { string.Join(",", new[] { "Str_History_Change", "Str_Col_Ip", "Str_Col_DeviceName",
                "Str_Col_Mac", "Str_Col_Vendor", "Str_Col_Type", "Str_Col_Ports" }.Select(key => Cell(Loc(key)))) };
            foreach (object row in VisibleGrid.Items)
            {
                var device = row is HistoryChangeRow change ? change.Device : ((HistoryDeviceRow)row).Device;
                lines.Add(string.Join(",", new[] { row is HistoryChangeRow changed ? changed.Change : "",
                    device.IpAddress, device.Hostname, device.MacAddress, device.Vendor,
                    DeviceTypeConverter.Display(device.DeviceType), string.Join(", ", device.OpenPorts) }.Select(Cell)));
            }
            return string.Join(Environment.NewLine, lines) + Environment.NewLine;
        }

        internal void ShowExportMenu(FrameworkElement target)
            => CreateExportMenu(target).IsOpen = true;

        private ContextMenu CreateExportMenu(FrameworkElement target)
        {
            var card = ScanCards.Menu();
            card.PlacementTarget = target;
            card.Placement = PlacementMode.Right;
            card.Items.Add(ScanCards.Header(card, Loc("Str_Card_Export"), ""));
            card.Items.Add(ScanCards.Grid(card, new[]
            {
                new KillerFamily.Controls.CardTile("\uE9F9", Loc("Str_Export_Csv"), "Ctrl+E", ExportCsv, false,
                    _entry != null && VisibleGrid.Items.Count > 0) { Id = "Csv" },
            }));
            return card;
        }

        internal void ExportCsv()
        {
            if (_entry == null || VisibleGrid.Items.Count == 0) return;
            var dialog = new FileDialog(FileDialogMode.Save) { Filter = Loc("Str_Filter_Csv") + "|*.csv",
                DefaultExt = ".csv", AddExtension = true, FileName = "KillerScan_History_" + _entry.ScannedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try { ExportFile.WriteText(dialog.FileName, BuildCsv(), new UTF8Encoding(true)); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { HistorySummary.Text = string.Format(Loc("Str_Err_Export"), ex.Message); }
        }

        public bool HandleShortcut(Key key, ModifierKeys modifiers, bool textInput = false)
        {
            if (textInput) return false;
            string? action = (key, modifiers) switch
            {
                (Key.C, ModifierKeys.Control) => "ip",
                (Key.C, ModifierKeys.Control | ModifierKeys.Shift) => "mac",
                (Key.C, ModifierKeys.Control | ModifierKeys.Alt) => "host",
                (Key.Y, ModifierKeys.Control | ModifierKeys.Shift) => "details",
                (Key.E, ModifierKeys.Control) => "export",
                (Key.H, ModifierKeys.Control | ModifierKeys.Shift) => _showAll ? "changes" : "all",
                (Key.F6, ModifierKeys.None) => "current",
                (Key.H, ModifierKeys.Control) => "sidebar",
                _ => null
            };
            if (action != null) { ExecuteAction(action); return true; }
            if (key == Key.Apps && modifiers == ModifierKeys.None || key == Key.F10 && modifiers == ModifierKeys.Shift)
            {
                var grid = VisibleGrid;
                PrepareHistoryCard(grid.ContextMenu, grid);
                var target = (grid.SelectedItem == null ? null : grid.ItemContainerGenerator.ContainerFromItem(grid.SelectedItem)) as UIElement ?? grid;
                grid.ContextMenu.PlacementTarget = target;
                grid.ContextMenu.Placement = PlacementMode.Bottom;
                grid.ContextMenu.IsOpen = true;
                return true;
            }
            // Archived addresses never dispatch live device or export commands to the hidden scan.
            return key is Key.F3 or Key.F5 or Key.Enter ||
                (modifiers & ModifierKeys.Control) != 0 && key is Key.R or Key.P or Key.D or Key.S or Key.E or Key.X or Key.G or Key.J or Key.H;
        }
    }
}
