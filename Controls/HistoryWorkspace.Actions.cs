using System.Globalization;
using System.IO;
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
                grid.ContextMenu = BuildHistoryMenu(grid);
                grid.PreviewMouseRightButtonDown += (_, e) => SelectContextRow(grid, e.OriginalSource as DependencyObject);
            }
            ContextMenu = BuildHistoryMenu(null);
        }

        private static void SelectContextRow(DataGrid grid, DependencyObject? source)
        {
            var row = source == null ? null : ItemsControl.ContainerFromElement(grid, source) as DataGridRow;
            if (row == null) { grid.UnselectAll(); return; }
            grid.SelectedItem = row.Item;
            grid.CurrentItem = row.Item;
            grid.Focus();
        }

        private ContextMenu BuildHistoryMenu(DataGrid? grid)
        {
            var menu = new ContextMenu();
            void Add(string key, string gesture, string action, int glyph)
            {
                var item = new MenuItem { InputGestureText = gesture, Tag = action, Icon = MenuGlyph.Create(glyph) };
                item.SetResourceReference(MenuItem.HeaderProperty, key);
                item.Click += (_, _) => ExecuteAction(action);
                menu.Items.Add(item);
            }
            Add("Str_Ctx_CopyIp", "Ctrl+C", "ip", 0xE8C8);
            Add("Str_Ctx_CopyMac", "Ctrl+Shift+C", "mac", 0xE8C8);
            Add("Str_Ctx_CopyHost", "Ctrl+Alt+C", "host", 0xE8C8);
            Add("Str_History_CopyDetails", "Ctrl+Shift+Y", "details", 0xE8C8);
            menu.Items.Add(new Separator());
            Add("Str_Export_Csv", "Ctrl+E", "export", 0xE896);
            Add("Str_History_ViewChanges", "Ctrl+Shift+H", "changes", 0xE8FD);
            Add("Str_History_ViewAll", "Ctrl+Shift+H", "all", 0xE772);
            menu.Items.Add(new Separator());
            Add("Str_History_Title", "Ctrl+H", "sidebar", 0xE81C);
            Add("Str_View_Devices", "F6", "current", 0xE772);
            menu.Opened += (_, _) =>
            {
                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    string action = (string)item.Tag;
                    item.IsEnabled = action switch
                    {
                        "ip" or "mac" or "host" or "details" => grid != null && CopyValue(action).Length > 0,
                        "export" => _entry != null && VisibleGrid.Items.Count > 0,
                        "changes" => _entry != null && _showAll,
                        "all" => _entry != null && !_showAll,
                        _ => true
                    };
                }
            };
            return menu;
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
            return action switch
            {
                "ip" => device.IpAddress,
                "mac" => device.MacAddress,
                "host" => device.Hostname,
                "details" => string.Join("\t", device.IpAddress, device.Hostname, device.MacAddress,
                    device.Vendor, DeviceTypeConverter.Display(device.DeviceType), string.Join(", ", device.OpenPorts)),
                _ => string.Empty
            };
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
            string Cell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
            var lines = new List<string> { string.Join(",", new[] { "Str_History_Change", "Str_Col_Ip", "Str_Col_Name",
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
            var item = new MenuItem { InputGestureText = "Ctrl+E", Icon = MenuGlyph.Create(0xE896), IsEnabled = _entry != null && VisibleGrid.Items.Count > 0 };
            item.SetResourceReference(MenuItem.HeaderProperty, "Str_Export_Csv");
            item.Click += (_, _) => ExportCsv();
            var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Right };
            menu.Items.Add(item);
            return menu;
        }

        internal void ExportCsv()
        {
            if (_entry == null || VisibleGrid.Items.Count == 0) return;
            var dialog = new FileDialog(FileDialogMode.Save) { Filter = Loc("Str_Filter_Csv") + "|*.csv",
                DefaultExt = ".csv", AddExtension = true, FileName = "KillerScan_History_" + _entry.ScannedAt.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            try { File.WriteAllText(dialog.FileName, BuildCsv(), new UTF8Encoding(true)); }
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
