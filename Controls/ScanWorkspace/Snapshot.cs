using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using KillerScan.Services;

namespace KillerScan.Controls
{
    /// <summary>
    /// Snapshots of the topology view. The PNG that already existed is one of four: a flattened
    /// JPEG for pasting somewhere that will not take a transparent image, a PNG with the pane
    /// background left out so the picture can sit on someone else's document, and an HTML file
    /// carrying real SVG, which stays sharp at any size and can be edited afterwards.
    /// </summary>
    public partial class ScanWorkspace
    {
        private enum SnapshotFormat { PngTransparent, Jpeg, Svg }

        private void ExportSnapshotPngAlpha_Click(object sender, RoutedEventArgs e) => ExportSnapshot(SnapshotFormat.PngTransparent);
        private void ExportSnapshotJpeg_Click(object sender, RoutedEventArgs e) => ExportSnapshot(SnapshotFormat.Jpeg);
        private void ExportSnapshotSvg_Click(object sender, RoutedEventArgs e) => ExportSnapshot(SnapshotFormat.Svg);

        private void ExportSnapshot(SnapshotFormat format)
        {
            if (!_showTopology || TopologyCanvas.Width <= 0 || TopologyCanvas.Height <= 0) return;

            (string filter, string extension) = format switch
            {
                SnapshotFormat.Jpeg => (Loc("Str_Filter_Jpeg") + "|*.jpg", ".jpg"),
                SnapshotFormat.Svg  => (Loc("Str_Filter_Html") + "|*.html", ".html"),
                _                   => (Loc("Str_Filter_Png") + "|*.png", ".png"),
            };

            var dlg = new FileDialog(FileDialogMode.Save)
            {
                Filter = filter,
                FileName = $"KillerScan_Topology_{DateTime.Now:yyyyMMdd_HHmmss}{extension}",
                DefaultExt = extension,
                AddExtension = true
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

            try
            {
                if (format == SnapshotFormat.Svg) File.WriteAllText(dlg.FileName, BuildTopologySvg(), new UTF8Encoding(false));
                else WriteTopologyRaster(dlg.FileName, format);
                // System.IO.Path spelled out: System.Windows.Shapes.Path is in scope here too,
                // because this file draws the connectors.
                StatusText.Text = string.Format(Loc("Str_St_Exported"), System.IO.Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Same failure path the other exports use, so a locked file or a full disk reports
                // itself the same way whichever menu entry you picked.
                MessageBox.Show(string.Format(Loc("Str_Err_Export"), ex.Message),
                    AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Renders the whole canvas, not the part of it on screen, so devices scrolled out of view
        /// are still in the file. The transparent variant simply skips the background rectangle.
        /// </summary>
        private void WriteTopologyRaster(string path, SnapshotFormat format)
        {
            int width = Math.Max(1, (int)Math.Ceiling(TopologyCanvas.Width));
            int height = Math.Max(1, (int)Math.Ceiling(TopologyCanvas.Height));

            var drawing = new DrawingVisual();
            using (DrawingContext dc = drawing.RenderOpen())
            {
                if (format == SnapshotFormat.Jpeg)
                {
                    // JPEG has no alpha, so it is flattened onto the theme's own surface: the pane
                    // brush first, which is a tiling drawing rather than a flat colour on 98SE,
                    // then the film grain over it at the strength that theme uses. Without the
                    // grain the exported picture is visibly smoother than the app it came from.
                    var background = TryFindResource("ScanContentPaneBrush") as Brush ?? Brushes.Black;
                    var area = new Rect(0, 0, width, height);
                    dc.DrawRectangle(background, null, area);

                    if (TryFindResource("GrainTileBrush") is Brush grain)
                    {
                        double opacity = TryFindResource("GrainOpacity") is double value ? value : 0.0;
                        if (opacity > 0)
                        {
                            dc.PushOpacity(opacity);
                            dc.DrawRectangle(grain, null, area);
                            dc.Pop();
                        }
                    }
                }
                dc.DrawRectangle(new VisualBrush(TopologyCanvas)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top
                }, null, new Rect(0, 0, width, height));
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);

            BitmapEncoder encoder = format == SnapshotFormat.Jpeg
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        /// <summary>
        /// The same picture as vectors rather than pixels, walked off the canvas itself so what is
        /// written is what is on screen, including boxes you moved. Wrapped in a minimal HTML page
        /// so it opens in a browser by double-clicking, with the SVG inline so it can be lifted
        /// straight out into a document or an editor.
        /// </summary>
        private string BuildTopologySvg()
        {
            var ci = CultureInfo.InvariantCulture;
            double width = TopologyCanvas.Width, height = TopologyCanvas.Height;

            var svg = new StringBuilder();
            svg.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 ")
               .Append(width.ToString("0.##", ci)).Append(' ').Append(height.ToString("0.##", ci))
               .Append("\" width=\"").Append(width.ToString("0.##", ci))
               .Append("\" height=\"").Append(height.ToString("0.##", ci)).Append("\">\n");

            // Connectors first so the boxes sit on top of them, matching the canvas.
            foreach (var line in TopologyCanvas.Children.OfType<Line>())
            {
                svg.Append("  <line x1=\"").Append(line.X1.ToString("0.##", ci))
                   .Append("\" y1=\"").Append(line.Y1.ToString("0.##", ci))
                   .Append("\" x2=\"").Append(line.X2.ToString("0.##", ci))
                   .Append("\" y2=\"").Append(line.Y2.ToString("0.##", ci))
                   .Append("\" stroke=\"").Append(Hex(line.Stroke, "#808080"))
                   .Append("\" stroke-width=\"").Append(line.StrokeThickness.ToString("0.##", ci))
                   .Append("\" stroke-dasharray=\"2 3\" opacity=\"")
                   .Append(line.Opacity.ToString("0.##", ci)).Append("\"/>\n");
            }

            foreach (var node in TopologyCanvas.Children.OfType<Border>())
            {
                double x = Canvas.GetLeft(node), y = Canvas.GetTop(node);
                if (double.IsNaN(x) || double.IsNaN(y)) continue;
                double w = node.Width, h = node.Height;
                if (double.IsNaN(w) || double.IsNaN(h)) continue;

                svg.Append("  <g>\n    <rect x=\"").Append(x.ToString("0.##", ci))
                   .Append("\" y=\"").Append(y.ToString("0.##", ci))
                   .Append("\" width=\"").Append(w.ToString("0.##", ci))
                   .Append("\" height=\"").Append(h.ToString("0.##", ci))
                   .Append("\" rx=\"3\" fill=\"").Append(Hex(node.Background, "#101010"))
                   .Append("\" stroke=\"").Append(Hex(node.BorderBrush, "#404040"))
                   .Append("\" stroke-width=\"1\"/>\n");

                if (node.Child is StackPanel stack)
                {
                    // The accent strip along the top is the device type's colour, and the two text
                    // lines are the name and the address.
                    if (stack.Children.Count > 0 && stack.Children[0] is Border accent)
                        svg.Append("    <rect x=\"").Append(x.ToString("0.##", ci))
                           .Append("\" y=\"").Append(y.ToString("0.##", ci))
                           .Append("\" width=\"").Append(w.ToString("0.##", ci))
                           .Append("\" height=\"").Append(accent.Height.ToString("0.##", ci))
                           .Append("\" fill=\"").Append(Hex(accent.Background, "#808080")).Append("\"/>\n");

                    double textY = y + 16;
                    foreach (var text in stack.Children.OfType<TextBlock>())
                    {
                        svg.Append("    <text x=\"").Append((x + 6).ToString("0.##", ci))
                           .Append("\" y=\"").Append(textY.ToString("0.##", ci))
                           .Append("\" font-family=\"").Append(text.FontFamily?.Source ?? "Segoe UI")
                           .Append("\" font-size=\"").Append(text.FontSize.ToString("0.##", ci))
                           .Append("\" fill=\"").Append(Hex(text.Foreground, "#e0e0e0"))
                           .Append("\" opacity=\"").Append(text.Opacity.ToString("0.##", ci))
                           .Append("\">").Append(Escape(text.Text)).Append("</text>\n");
                        textY += text.FontSize + 4;
                    }
                }
                svg.Append("  </g>\n");
            }
            svg.Append("</svg>");

            string pane = Hex(TryFindResource("ScanContentPaneBrush") as Brush, "#101010");
            string title = Escape(_active.ScannedSubnet.Length > 0 ? _active.ScannedSubnet : "KillerScan");
            return "<!doctype html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>"
                 + title + " topology</title>\n<style>\n"
                 + "  body { margin: 0; background: " + pane + "; display: flex; justify-content: center; }\n"
                 + "  svg { max-width: 100%; height: auto; }\n"
                 + "</style>\n</head>\n<body>\n" + svg + "\n</body>\n</html>\n";
        }

        /// <summary>A brush as #rrggbb, or the fallback when it is not a plain colour.</summary>
        private static string Hex(Brush? brush, string fallback) =>
            brush is SolidColorBrush solid
                ? $"#{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}"
                : fallback;

        private static string Escape(string? text) => string.IsNullOrEmpty(text)
            ? string.Empty
            : text!.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
