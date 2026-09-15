using System.Text;
using KillerScan.Models;

namespace KillerScan.Terminal
{
    internal sealed class TerminalScanPresentation(Func<string, string> loc, Func<int> columns)
    {
        private int Width => Math.Max(12, columns() - 2);
        private static string Clean(string text) => new(text.Where(c => !char.IsControl(c)).ToArray());

        public string Progress(string status)
        {
            string text = Clean(status);
            if (text.Length > Width) text = text[..Width];
            return "\r\u001b[2K" + text;
        }

        public string Result(IEnumerable<NetworkDevice> devices)
        {
            var result = new StringBuilder("\r\u001b[2K");
            string[] headers = [loc("Str_Col_Ip"), loc("Str_Col_Host"), loc("Str_Col_Mac"),
                loc("Str_Col_Vendor"), loc("Str_Col_Type"), loc("Str_Col_Ports")];
            var rows = devices.Select(d => new[] { d.IpAddress, d.Hostname, d.MacAddress,
                d.Vendor, d.DeviceType, d.OpenPortsDisplay }).ToList();
            int[] limits = [15, 24, 17, 30, 14, 24];
            int[] widths = Enumerable.Range(0, 6).Select(c => Math.Min(limits[c],
                Math.Max(headers[c].Length, rows.Select(r => Clean(r[c]).Length).DefaultIfEmpty(0).Max()))).ToArray();
            int available = Math.Max(12, Width - 10);
            while (widths.Sum() > available)
            {
                int column = Enumerable.Range(0, 6).Where(c => widths[c] > (c == 5 ? 6 : 1))
                    .OrderByDescending(c => widths[c]).First();
                widths[column]--;
            }
            int[] flexible = [1, 3, 5];
            for (int extra = 0; widths.Sum() < available; extra++) widths[flexible[extra % flexible.Length]]++;
            static List<string> Wrap(string text, int width)
            {
                string remaining = Clean(text);
                var lines = new List<string>();
                do
                {
                    int length = Math.Min(remaining.Length, width);
                    if (length < remaining.Length)
                    {
                        int space = remaining.LastIndexOf(' ', length - 1, length);
                        if (space > 0) length = space;
                    }
                    lines.Add(remaining[..length]);
                    remaining = remaining[length..].TrimStart();
                } while (remaining.Length > 0);
                return lines;
            }
            void Row(string[] cells, bool header)
            {
                var lines = cells.Select((cell, c) => Wrap(cell, widths[c])).ToArray();
                for (int line = 0; line < lines.Max(c => c.Count); line++)
                {
                    for (int c = 0; c < 6; c++)
                    {
                        int color = header ? 37 : c switch { 0 => 36, 2 => 33, 4 => 35, 5 => 32, _ => 37 };
                        string cell = line < lines[c].Count ? lines[c][line] : "";
                        result.Append("\u001b[").Append(color).Append('m').Append(cell.PadRight(widths[c])).Append("\u001b[0m");
                        if (c < 5) result.Append("  ");
                    }
                    result.Append("\r\n");
                }
            }
            Row(headers, true);
            foreach (var row in rows) Row(row, false);
            return result.ToString();
        }
    }
}
