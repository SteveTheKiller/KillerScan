using System;
using System.IO;
using System.Text;
using System.Windows;
using KillerScan.Services;
using Microsoft.Win32;

namespace KillerScan.Shell
{
    // Scanner core, window half: the export menu and the two save dialogs. The documents
    // themselves are written by Services/ReportExport.cs, which the command line uses as well.
    public partial class MainWindow
    {
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveDevices.Count == 0 || ExportButton.ContextMenu is null) return;
            ExportButton.ContextMenu.PlacementTarget = ExportButton;
            ExportButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            ExportButton.ContextMenu.IsOpen = true;
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            // The description before the | is what the Windows save dialog shows in its type
            // dropdown, so it is interface text; the *.csv pattern after it is not.
            var dlg = new SaveFileDialog { Filter = Loc("Str_Filter_Csv") + "|*.csv", FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.csv" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, ReportExport.BuildCsv(ActiveDevices), Encoding.UTF8);
                StatusText.Text = string.Format(Loc("Str_St_Exported"), Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            { MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message), AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void ExportHtml_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new SaveFileDialog { Filter = Loc("Str_Filter_Html") + "|*.html", FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.html" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                // The report opens in whatever theme the app is in; all thirteen palettes ride along, so
                // the reader can switch inside the file.
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
