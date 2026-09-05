using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private string _exportContext = "scan";

        /// <summary>
        /// Which view the export button is acting for. "scan" means one of this control's own
        /// views and is resolved to devices, services or topology when the menu opens; the shell
        /// sets "watch" or "terminal" when one of its own views is in front.
        /// </summary>
        public string ExportContext
        {
            get => _exportContext;
            set => _exportContext = string.IsNullOrWhiteSpace(value) ? "scan" : value;
        }

        /// <summary>Raised for the exports the shell owns: the Keep Alive run and the terminal.</summary>
        public event EventHandler<string>? ShellExportRequested;

        private void ExportWatchCsv_Click(object sender, RoutedEventArgs e) => ShellExportRequested?.Invoke(this, "watch-csv");
        private void ExportWatchHtml_Click(object sender, RoutedEventArgs e) => ShellExportRequested?.Invoke(this, "watch-html");
        private void ExportWatchPng_Click(object sender, RoutedEventArgs e) => ShellExportRequested?.Invoke(this, "watch-png");
        private void ExportTerminalText_Click(object sender, RoutedEventArgs e) => ShellExportRequested?.Invoke(this, "terminal-txt");

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (ExportButton.ContextMenu is null) return;
            // Keep Alive and the terminal have something to export with no scan behind them, so the
            // empty-results guard applies only to the views built on the device list.
            if (ActiveDevices.Count == 0 && _exportContext is "scan") return;

            // Only what the view in front of you can actually produce. Topology has no table to
            // write, so CSV and the device report are hidden there rather than exporting the list
            // behind the picture; Keep Alive and the terminal export themselves through the shell,
            // which is what owns those controls.
            string context = _exportContext == "scan"
                ? (_showTopology ? "topology" : _showServices ? "services" : "devices")
                : _exportContext;

            static Visibility When(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
            bool table = context is "devices" or "services";

            ExportCsvItem.Visibility          = When(table);
            ExportHtmlItem.Visibility         = When(table);
            ExportServicesCsvItem.Visibility  = When(context == "services");
            ExportTopologyPngItem.Visibility  = When(context == "topology");
            ExportTopologyJpegItem.Visibility = When(context == "topology");
            ExportTopologySvgItem.Visibility  = When(context == "topology");
            ExportWatchCsvItem.Visibility     = When(context == "watch");
            ExportWatchHtmlItem.Visibility    = When(context == "watch");
            ExportWatchPngItem.Visibility     = When(context == "watch");
            ExportTerminalTextItem.Visibility = When(context == "terminal");
            ExportServicesCsvItem.Visibility = _showServices ? Visibility.Visible : Visibility.Collapsed;
            // The button now sits on the rail down the left, so the flyout opens beside it rather
            // than below it, anchored to the button itself.
            ExportButton.ContextMenu.PlacementTarget = ExportButton;
            ExportButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Right;
            ExportButton.ContextMenu.IsOpen = true;
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Csv") + "|*.csv",
                FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, ReportExport.BuildCsv(ActiveDevices), Encoding.UTF8);
                StatusText.Text = string.Format(Loc("Str_St_Exported"), Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            { MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message), AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        /// <summary>
        /// The service view's own export: one row per open port, as the grid shows it, rather
        /// than one row per device with the ports packed into a single cell.
        /// </summary>
        private void ExportServicesCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Csv") + "|*.csv",
                FileName = $"KillerScan_Services_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                // English headers and blanket quoting, matching ReportExport.BuildCsv so both
                // exports open the same way in a spreadsheet.
                var text = new StringBuilder();
                text.AppendLine("Service,Port,Name,IP Address,Type");
                foreach (var row in ServicesGrid.Items.OfType<ServiceRow>())
                    text.AppendLine($"\"{row.Service}\",\"{row.Port}\",\"{row.DeviceName}\",\"{row.IpAddress}\",\"{row.DeviceTypeDisplay}\"");
                File.WriteAllText(dlg.FileName, text.ToString(), Encoding.UTF8);
                StatusText.Text = string.Format(Loc("Str_St_Exported"), Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            { MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message), AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ExportHtml_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Html") + "|*.html",
                FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.html"
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            try
            {
                string html = ReportExport.BuildHtml(
                    ActiveDevices, SubnetInput.Text, ThemeManager.Current.ToString().ToLowerInvariant());
                File.WriteAllText(dlg.FileName, html, Encoding.UTF8);
                StatusText.Text = string.Format(Loc("Str_St_Exported"), Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            { MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message), AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

    }
}
