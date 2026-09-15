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
            void Line(string text, int color)
            {
                string remaining = Clean(text);
                bool continuation = false;
                do
                {
                    int available = Width - (continuation ? 2 : 0);
                    int length = Math.Min(remaining.Length, available);
                    if (length < remaining.Length)
                    {
                        int space = remaining.LastIndexOf(' ', length - 1, length);
                        if (space > 0) length = space;
                    }
                    result.Append("\u001b[").Append(color).Append('m');
                    if (continuation) result.Append("  ");
                    result.Append(remaining[..length]).Append("\u001b[0m\r\n");
                    remaining = remaining[length..].TrimStart();
                    continuation = true;
                } while (remaining.Length > 0);
            }
            foreach (var device in devices)
            {
                Line($"{device.IpAddress}  {device.Hostname}  {device.DeviceType}", 36);
                Line($"  {device.MacAddress}  {device.Vendor}", 37);
                Line($"  {loc("Str_Col_Ports")}: {device.OpenPortsDisplay}", 32);
                result.Append("\r\n");
            }
            return result.ToString();
        }
    }
}
