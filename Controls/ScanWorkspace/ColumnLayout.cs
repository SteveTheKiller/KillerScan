using System.Windows.Controls;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private Dictionary<string, DataGridColumn> ColumnMap() => new()
        {
            ["ColIp"]     = ColIp,
            ["ColHost"]   = ColHost,
            ["ColMac"]    = ColMac,
            ["ColVendor"] = ColVendor,
            ["ColType"]   = ColType,
            ["ColPorts"]  = ColPorts,
        };

        /// <summary>
        /// Keeps the MAC column at least as wide as a full address in the grid's current font,
        /// so neither a drag, a saved width from an older version nor a wider theme font can cut
        /// one off. The widest hex digit stands in for every position.
        /// </summary>
        private void FitMacColumn()
        {
            var sample = new System.Windows.Media.FormattedText("DD:DD:DD:DD:DD:DD",
                System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(ResultsGrid.FontFamily, ResultsGrid.FontStyle, System.Windows.FontWeights.SemiBold, ResultsGrid.FontStretch),
                ResultsGrid.FontSize, System.Windows.Media.Brushes.Black,
                System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);
            // Cell padding, the gridline and the sort glyph's room in the header.
            double needed = Math.Ceiling(sample.WidthIncludingTrailingWhitespace) + 24;
            ColMac.MinWidth = Math.Max(110, needed);
            if (ColMac.Width.IsAbsolute && ColMac.Width.Value < ColMac.MinWidth)
                ColMac.Width = new DataGridLength(ColMac.MinWidth);
        }

        private void SaveColumnLayout()
        {
            try
            {
                var parts = ColumnMap().Select(kv =>
                {
                    var c = kv.Value;
                    string w = c.Width.UnitType == DataGridLengthUnitType.Pixel
                        ? c.Width.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                        : "-";
                    return $"{kv.Key}:{c.DisplayIndex}:{w}";
                });
                App.SetSetting("GridColumns", string.Join("|", parts));
            }
            catch { /* best-effort */ }
        }

        private void RestoreColumnLayout()
        {
            string? s = App.GetSetting("GridColumns");
            if (string.IsNullOrWhiteSpace(s)) return;
            try
            {
                var map = ColumnMap();
                int count = ResultsGrid.Columns.Count;
                var order = new List<(DataGridColumn Col, int Idx)>();
                foreach (string part in s!.Split('|'))
                {
                    string[] f = part.Split(':');
                    if (f.Length != 3 || !map.TryGetValue(f[0], out var col)) continue;
                    if (int.TryParse(f[1], out int idx) && idx >= 0 && idx < count)
                        order.Add((col, idx));
                    if (f[2] != "-" &&
                        double.TryParse(f[2], System.Globalization.NumberStyles.Float,
                                        System.Globalization.CultureInfo.InvariantCulture, out double w) &&
                        w > 0)
                        col.Width = new DataGridLength(w);
                }
                foreach (var (col, idx) in order.OrderBy(o => o.Idx))
                    col.DisplayIndex = idx;
            }
            catch { /* best-effort */ }
        }

        /// <summary>
        /// Lets a trimmed cell's tooltip open only when the text was actually cut, so a value
        /// that fits is not repeated back on hover.
        /// </summary>
        private void TrimmedCell_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (sender is not TextBlock block || string.IsNullOrEmpty(block.Text)) { e.Handled = true; return; }
            var typeface = new System.Windows.Media.Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch);
            var text = new System.Windows.Media.FormattedText(block.Text, System.Globalization.CultureInfo.CurrentCulture,
                block.FlowDirection, typeface, block.FontSize, System.Windows.Media.Brushes.Black,
                System.Windows.Media.VisualTreeHelper.GetDpi(block).PixelsPerDip);
            double room = block.ActualWidth - block.Padding.Left - block.Padding.Right;
            if (text.WidthIncludingTrailingWhitespace <= room + 0.5) e.Handled = true;
        }
    }
}
