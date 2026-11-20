using KillerFamily.Controls;
using System.Collections.Generic;
using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private string _exportContext = "scan";
        internal Func<FileDialog, bool?>? ExportDialog { get; set; }
        private bool? ShowExportDialog(FileDialog dialog) => ExportDialog is { } select
            ? select(dialog) : dialog.ShowDialog(Window.GetWindow(this));

        /// <summary>
        /// Which view the export button is acting for. "scan" means one of this control's own
        /// views and is resolved to devices, services or topology when the menu opens; the shell
        /// sets "watch" or "terminal" when one of its own views is in front.
        /// </summary>
        public string ExportContext
        {
            get => _exportContext;
            set
            {
                _exportContext = string.IsNullOrWhiteSpace(value) ? "scan" : value;
                UpdateExportButtonAvailability();
            }
        }

        private void UpdateExportButtonAvailability()
        {
            ExportButton.IsEnabled = _exportContext is "watch" or "terminal" or "history" || ActiveDevices.Count > 0;
            string shortcuts = CurrentExportContext() switch
            {
                "services" or "history" => "CSV Ctrl+E",
                "watch" => "CSV Ctrl+E, HTML Ctrl+Shift+E, TXT Ctrl+Shift+X, PNG Ctrl+Shift+G",
                "terminal" => "TXT Ctrl+Shift+X",
                "topology" => "PNG Ctrl+Shift+G, JPG Ctrl+Shift+J, SVG Ctrl+Alt+H",
                _ => "CSV Ctrl+E, HTML Ctrl+Shift+E"
            };
            var tooltip = new TextBlock();
            var caption = new Run();
            caption.SetResourceReference(Run.TextProperty, "Str_TT_Export");
            tooltip.Inlines.Add(caption);
            tooltip.Inlines.Add(" (" + shortcuts + ")");
            ExportButton.ToolTip = tooltip;
        }

        private string CurrentExportContext() => _exportContext == "scan"
            ? (_showTopology ? "topology" : _showServices ? "services" : "devices")
            : _exportContext;

        /// <summary>Raised for the exports the shell owns: the Keep Alive run and the terminal.</summary>
        public event EventHandler<string>? ShellExportRequested;

        private void ExportCsv_Click(object sender, RoutedEventArgs e) => Export("csv");
        private void ExportServicesCsv_Click(object sender, RoutedEventArgs e) => Export("csv");
        private void ExportHtml_Click(object sender, RoutedEventArgs e) => Export("html");
        private void ExportWatchCsv_Click(object sender, RoutedEventArgs e) => Export("csv");
        private void ExportWatchHtml_Click(object sender, RoutedEventArgs e) => Export("html");
        private void ExportWatchTxt_Click(object sender, RoutedEventArgs e) => Export("txt");
        private void ExportWatchPng_Click(object sender, RoutedEventArgs e) => Export("png");
        private void ExportTerminalText_Click(object sender, RoutedEventArgs e) => Export("txt");

        public void Export(string format)
        {
            if (!ExportButton.IsEnabled) return;

            switch (CurrentExportContext(), format)
            {
                case ("devices", "csv"): WriteDevicesCsv(); break;
                case ("services", "csv"): WriteServicesCsv(); break;
                case ("history", "csv"): ShellExportRequested?.Invoke(this, "history-csv"); break;
                case ("watch", "csv"): ShellExportRequested?.Invoke(this, "watch-csv"); break;
                case ("devices", "html"): WriteDevicesHtml(); break;
                case ("watch", "html"): ShellExportRequested?.Invoke(this, "watch-html"); break;
                case ("watch", "txt"): ShellExportRequested?.Invoke(this, "watch-txt"); break;
                case ("terminal", "txt"): ShellExportRequested?.Invoke(this, "terminal-txt"); break;
                case ("watch", "png"): ShellExportRequested?.Invoke(this, "watch-png"); break;
                case ("topology", "png"): ExportSnapshot(SnapshotFormat.PngTransparent); break;
                case ("topology", "jpg"): ExportSnapshot(SnapshotFormat.Jpeg); break;
                case ("topology", "svg"): ExportSnapshot(SnapshotFormat.Svg); break;
            }
        }

        /// <summary>A right-click opens the same card a click does, filled for the view in front, never the empty template.</summary>
        private void ExportButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;
            ExportButton_Click(sender, new RoutedEventArgs());
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (ExportButton.ContextMenu is null) return;
            if (_exportContext == "history") { ShellExportRequested?.Invoke(this, "history-menu"); return; }
            // Keep Alive and the terminal have something to export with no scan behind them, so the
            // empty-results guard applies only to the views built on the device list.
            if (ActiveDevices.Count == 0 && _exportContext is "scan") return;

            // Only what the view in front of you can actually produce. Topology has no table to
            // write, so CSV and the device report are hidden there rather than exporting the list
            // behind the picture; Keep Alive and the terminal export themselves through the shell,
            // which is what owns those controls.
            string context = CurrentExportContext();

            PrepareExportCard(context);
            // The button now sits on the rail down the left, so the flyout opens beside it rather
            // than below it, anchored to the button itself.
            ExportCard.PlacementTarget = ExportButton;
            ExportCard.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
            ExportCard.IsOpen = true;
        }

        /// <summary>The export card: its title over a tile for each format the view in front of you can write.</summary>
        internal void PrepareExportCard(string context)
        {
            var card = ScanCards.Prepare(ExportCard);
            card.Items.Add(ScanCards.Header(card, Loc("Str_Card_Export"), ""));
            CardTile Tile(string id, string glyph, string key, string keys, RoutedEventHandler handler) =>
                new(glyph, Loc(key), keys, () => handler(this, new RoutedEventArgs()), false, true) { Id = id };
            List<CardTile> tiles = context switch
            {
                "devices" => [Tile("Csv", "", "Str_Export_Csv", "Ctrl+E", ExportCsv_Click),
                              Tile("Html", "", "Str_Export_Html", "Ctrl+Shift+E", ExportHtml_Click)],
                "services" => [Tile("ServicesCsv", "", "Str_Export_ServicesCsv", "Ctrl+E", ExportServicesCsv_Click)],
                "topology" => [Tile("TopologyPng", "", "Str_Export_SnapPngAlpha", "Ctrl+Shift+G", ExportSnapshotPngAlpha_Click),
                               Tile("TopologyJpeg", "", "Str_Export_SnapJpeg", "Ctrl+Shift+J", ExportSnapshotJpeg_Click),
                               Tile("TopologySvg", "", "Str_Export_SnapSvg", "Ctrl+Alt+H", ExportSnapshotSvg_Click)],
                "watch" => [Tile("WatchCsv", "", "Str_Export_Csv", "Ctrl+E", ExportWatchCsv_Click),
                            Tile("WatchHtml", "", "Str_Export_Html", "Ctrl+Shift+E", ExportWatchHtml_Click),
                            Tile("WatchTxt", "", "Str_Export_Txt", "Ctrl+Shift+X", ExportWatchTxt_Click),
                            Tile("WatchPng", "", "Str_Export_WatchPng", "Ctrl+Shift+G", ExportWatchPng_Click)],
                "terminal" => [Tile("TerminalText", "", "Str_Export_TerminalTxt", "Ctrl+Shift+X", ExportTerminalText_Click)],
                _ => [],
            };
            card.Items.Add(ScanCards.Grid(card, tiles));
        }

        private void WriteDevicesCsv()
        {
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Csv") + "|*.csv",
                FileName = $"KillerScan_{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.csv"
            };
            try
            {
                string csv = ReportExport.BuildCsv(ActiveDevices);
                if (ShowExportDialog(dlg) != true) return;
                ExportFile.WriteText(dlg.FileName, csv, Encoding.UTF8);
                StatusText.Text = string.Format(Loc("Str_St_Exported"), Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            { MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message), AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        /// <summary>
        /// The service view's own export: one row per open port, as the grid shows it, rather
        /// than one row per device with the ports packed into a single cell.
        /// </summary>
        private void WriteServicesCsv()
        {
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Csv") + "|*.csv",
                FileName = $"KillerScan_Services_{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.csv"
            };
            try
            {
                // English headers and blanket quoting, matching ReportExport.BuildCsv so both
                // exports open the same way in a spreadsheet.
                var text = new StringBuilder();
                text.AppendLine("Service,Port,Name,IP Address,Type");
                foreach (var row in ServicesGrid.Items.OfType<ServiceRow>())
                    text.AppendLine(string.Join(",", ReportExport.CsvField(row.Service), ReportExport.CsvField(row.Port.ToString()),
                        ReportExport.CsvField(row.DeviceName), ReportExport.CsvField(row.IpAddress), ReportExport.CsvField(row.DeviceTypeDisplay)));
                if (ShowExportDialog(dlg) != true) return;
                ExportFile.WriteText(dlg.FileName, text.ToString(), Encoding.UTF8);
                StatusText.Text = string.Format(Loc("Str_St_Exported"), Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            { MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message), AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void WriteDevicesHtml()
        {
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Html") + "|*.html",
                FileName = $"KillerScan_{DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.html"
            };
            try
            {
                string html = ReportExport.BuildSnapshotHtml(
                    ActiveDevices, _active.ScannedSubnet, ThemeManager.Current.ToString().ToLowerInvariant(),
                    _active.ScannedAt, _active.Completion, _active.Refresh,
                    _location.IsTemporary ? string.Empty : LocationName, _active.Adapter);
                if (ShowExportDialog(dlg) != true) return;
                ExportFile.WriteText(dlg.FileName, html, Encoding.UTF8);
                StatusText.Text = string.Format(Loc("Str_St_Exported"), Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            { MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message), AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

    }
}
