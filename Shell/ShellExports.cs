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
    /// <summary>
    /// The exports for the two views the shell owns rather than the scan workspace: the Keep Alive
    /// run and the terminal. The export button and its menu still live on the rail with everything
    /// else, so these arrive as requests from that menu.
    /// </summary>
    public partial class MainWindow
    {
        private void ShellExport(object? sender, string kind)
        {
            switch (kind)
            {
                case "watch-csv":  SaveWatch("csv"); break;
                case "watch-html": SaveWatch("html"); break;
                case "watch-png":  SaveWatch("png"); break;
                case "terminal-txt": SaveTerminalText(); break;
            }
        }

        private void SaveWatch(string format)
        {
            if (_watchWorkspace is not { } watch) return;

            (string filterKey, string extension) = format switch
            {
                "html" => ("Str_Filter_Html", ".html"),
                "png"  => ("Str_Filter_Png", ".png"),
                _      => ("Str_Filter_Csv", ".csv"),
            };

            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc(filterKey) + "|*" + extension,
                FileName = $"KillerScan_KeepAlive_{DateTime.Now:yyyyMMdd_HHmmss}{extension}",
                DefaultExt = extension,
                AddExtension = true
            };
            if (dlg.ShowDialog(this) != true) return;

            try
            {
                if (format == "png") SaveElementPng(watch.CardsVisual, dlg.FileName);
                else File.WriteAllText(dlg.FileName, format == "html" ? watch.BuildHtml() : watch.BuildCsv(),
                                       new UTF8Encoding(false));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message),
                    AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SaveTerminalText()
        {
            if (_terminalControl is not { } terminal) return;

            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Text") + "|*.txt",
                FileName = $"KillerScan_Terminal_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                DefaultExt = ".txt",
                AddExtension = true
            };
            if (dlg.ShowDialog(this) != true) return;

            try { File.WriteAllText(dlg.FileName, terminal.GetText(), new UTF8Encoding(false)); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message),
                    AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// An element as it appears, painted onto the theme's own surface first so the picture is
        /// not a set of cards floating on nothing. Rendered at the element's full size rather than
        /// the part of it scrolled into view.
        /// </summary>
        private void SaveElementPng(FrameworkElement element, string path)
        {
            element.UpdateLayout();
            double width = element.ActualWidth, height = element.ActualHeight;
            if (element is System.Windows.Controls.Panel or System.Windows.Controls.ItemsControl)
            {
                width = Math.Max(width, element.DesiredSize.Width);
                height = Math.Max(height, element.DesiredSize.Height);
            }
            int w = Math.Max(1, (int)Math.Ceiling(width));
            int h = Math.Max(1, (int)Math.Ceiling(height));

            var drawing = new DrawingVisual();
            using (DrawingContext dc = drawing.RenderOpen())
            {
                var background = TryFindResource("ScanContentPaneBrush") as Brush ?? Brushes.Black;
                dc.DrawRectangle(background, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(element)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                }, null, new Rect(0, 0, w, h));
            }

            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
    }
}
