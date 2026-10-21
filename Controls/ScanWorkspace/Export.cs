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

            static Visibility When(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
            ExportCsvItem.Visibility          = When(context == "devices");
            ExportHtmlItem.Visibility         = When(context == "devices");
            ExportServicesCsvItem.Visibility  = When(context == "services");
            ExportTopologyPngItem.Visibility  = When(context == "topology");
            ExportTopologyJpegItem.Visibility = When(context == "topology");
            ExportTopologySvgItem.Visibility  = When(context == "topology");
            ExportWatchCsvItem.Visibility     = When(context == "watch");
            ExportWatchHtmlItem.Visibility    = When(context == "watch");
            ExportWatchTxtItem.Visibility     = When(context == "watch");
            ExportWatchPngItem.Visibility     = When(context == "watch");
            ExportTerminalTextItem.Visibility = When(context == "terminal");
            // The button now sits on the rail down the left, so the flyout opens beside it rather
            // than below it, anchored to the button itself.
            ExportButton.ContextMenu.PlacementTarget = ExportButton;
            ExportButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
            ExportButton.ContextMenu.IsOpen = true;
        }

        private void WriteDevicesCsv()
        {
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Csv") + "|*.csv",
                FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
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
                FileName = $"KillerScan_Services_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
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
                FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.html"
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
