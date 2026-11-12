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

        /// <summary>
        /// On a table, the saved-device card: the device's name and address over its copy chips,
        /// then the view and export rows. Nothing live is on it (no ping, rescan or connect): the
        /// row is a record of a past scan. The pane background keeps the plain list, since there
        /// is no device to copy from.
        /// </summary>
        private ContextMenu BuildHistoryMenu(DataGrid? grid)
        {
            ContextMenu menu;
            MenuItem? header = null;
            var chips = new List<Button>();
            if (grid != null)
            {
                menu = KillerFamily.Controls.CardBuilder.Menu();
                header = KillerFamily.Controls.CardBuilder.Header(Loc("Str_History_Title"));
                menu.Items.Add(header);
                var wrap = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                void Chip(string? text, string? textKey, string nameKey, string gesture, string action)
                {
                    var chip = new Button { CommandParameter = action };
                    chip.SetResourceReference(StyleProperty, "CardChip");
                    if (text != null) chip.Content = text; else chip.SetResourceReference(ContentControl.ContentProperty, textKey!);
                    chip.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, nameKey);
                    System.Windows.Automation.AutomationProperties.SetAcceleratorKey(chip, gesture);
                    var tip = new TextBlock();
                    var name = new System.Windows.Documents.Run();
                    name.SetResourceReference(System.Windows.Documents.Run.TextProperty, nameKey);
                    tip.Inlines.Add(name);
                    tip.Inlines.Add(new System.Windows.Documents.Run("  (" + gesture + ")"));
                    chip.ToolTip = tip;
                    chip.Click += (_, _) => { KillerFamily.Controls.CardBuilder.Close(chip); ExecuteAction(action); };
                    chips.Add(chip);
                    wrap.Children.Add(chip);
                }
                // IP and MAC are names, the same in every language.
                Chip("IP", null, "Str_Ctx_CopyIp", "Ctrl+C", "ip");
                Chip("MAC", null, "Str_Ctx_CopyMac", "Ctrl+Shift+C", "mac");
                Chip(null, "Str_Col_Host", "Str_Ctx_CopyHost", "Ctrl+Alt+C", "host");
                Chip(null, "Str_Card_Details", "Str_History_CopyDetails", "Ctrl+Shift+Y", "details");
                var copy = KillerFamily.Controls.CardBuilder.Labeled(Loc("Str_Ctx_Copy"), wrap);
                copy.Padding = new Thickness(24, 6, 4, 4);
                ((TextBlock)((DockPanel)copy.Header).Children[0]).SetResourceReference(TextBlock.TextProperty, "Str_Ctx_Copy");
                copy.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "Str_Ctx_Copy");
                menu.Items.Add(copy);
            }
            else menu = new ContextMenu();
            void Add(string key, string gesture, string action, int glyph)
            {
                var item = new MenuItem { InputGestureText = gesture, Tag = action, Icon = MenuGlyph.Create(glyph) };
                item.SetResourceReference(MenuItem.HeaderProperty, key);
                item.Click += (_, _) => ExecuteAction(action);
                menu.Items.Add(item);
            }
            if (grid != null) menu.Items.Add(new Separator());
            Add("Str_Export_Csv", "Ctrl+E", "export", 0xE896);
            Add("Str_History_ViewChanges", "Ctrl+Shift+H", "changes", 0xE8FD);
            Add("Str_History_ViewAll", "Ctrl+Shift+H", "all", 0xE772);
            menu.Items.Add(new Separator());
            Add("Str_History_Title", "Ctrl+H", "sidebar", 0xE81C);
            Add("Str_View_Devices", "F6", "current", 0xE772);
            menu.Opened += (_, _) =>
            {
                foreach (var chip in chips) chip.IsEnabled = CopyValue((string)chip.CommandParameter).Length > 0;
                if (header != null)
                {
                    var device = SelectedHistoryDevice;
                    string title = device == null ? Loc("Str_History_Title")
                        : string.IsNullOrWhiteSpace(device.Hostname) ? device.IpAddress : device.Hostname;
                    string meta = device == null ? "" : string.Join("  ·  ", new[] { device.IpAddress, device.MacAddress, device.Vendor }
                        .Where(part => !string.IsNullOrWhiteSpace(part) && part != title));
                    ScanWorkspace.SetCardHeader(header, title, meta);
                }
                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    if (item.Tag is not string action) continue;
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
