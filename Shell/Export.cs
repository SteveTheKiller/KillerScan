using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    // Scanner core, window half: the export menu and the two save dialogs. The documents
    // themselves are written by Services/ReportExport.cs, which the command line uses as well.
    public partial class MainWindow
    {
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (ActiveDevices.Count == 0 || ExportButton.ContextMenu is null) return;
            ExportTopologyPngItem.Visibility = _showTopology ? Visibility.Visible : Visibility.Collapsed;
            ExportButton.ContextMenu.PlacementTarget = ExportButton;
            ExportButton.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            ExportButton.ContextMenu.IsOpen = true;
        }

        private void ExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Csv") + "|*.csv",
                FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (dlg.ShowDialog(this) != true) return;
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
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Html") + "|*.html",
                FileName = $"KillerScan_{DateTime.Now:yyyyMMdd_HHmmss}.html"
            };
            if (dlg.ShowDialog(this) != true) return;
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

        private void ExportTopologyPng_Click(object sender, RoutedEventArgs e)
        {
            if (!_showTopology || TopologyCanvas.Width <= 0 || TopologyCanvas.Height <= 0) return;
            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Png") + "|*.png",
                FileName = $"KillerScan_Topology_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                DefaultExt = ".png",
                AddExtension = true
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                int width = Math.Max(1, (int)Math.Ceiling(TopologyCanvas.Width));
                int height = Math.Max(1, (int)Math.Ceiling(TopologyCanvas.Height));
                var drawing = new DrawingVisual();
                using (DrawingContext dc = drawing.RenderOpen())
                {
                    var background = TryFindResource("ScanContentPaneBrush") as Brush ?? Brushes.Black;
                    dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
                    dc.DrawRectangle(new VisualBrush(TopologyCanvas)
                    {
                        Stretch = Stretch.None,
                        AlignmentX = AlignmentX.Left,
                        AlignmentY = AlignmentY.Top
                    }, null, new Rect(0, 0, width, height));
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(dlg.FileName);
                encoder.Save(stream);
                StatusText.Text = string.Format(Loc("Str_St_Exported"), Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message),
                    AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
