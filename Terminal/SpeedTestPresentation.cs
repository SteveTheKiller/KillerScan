using System;
using System.Text;
using KillerScan.Services.SpeedTest;

namespace KillerScan.Terminal
{
    internal sealed class SpeedTestPresentation(Func<string, string> loc, Func<int> columns)
    {
        private const string Reset = "\u001b[0m";
        private int Width => Math.Max(12, Math.Min(64, columns() - 2));
        private string Rule => "\u001b[36m" + new string('─', Width) + Reset + "\r\n";
        private string Value(double? value, string unit) => value.HasValue ? value.Value.ToString("N1") + " " + unit : loc("Str_Speed_Unavailable");
        private string Row(string label, string value, int color) =>
            "  \u001b[37m" + label + "  \u001b[1;" + color + "m" + value + Reset + "\r\n";

        public string Header(Uri endpoint) => "\r\n" + Rule + "  \u001b[1;36m" + loc("Str_Speed_Title") + Reset +
            "\r\n  \u001b[90m" + loc("Str_Speed_Tagline") + Reset + "\r\n" + Rule +
            Row(loc("Str_Speed_Server"), endpoint.Host, 37) +
            "  \u001b[90mEsc / Ctrl+C\u001b[0m\r\n\r\n";

        public string Progress(SpeedTestProgress p)
        {
            bool down = p.Phase is SpeedTestPhase.Download or SpeedTestPhase.DownloadWarmup;
            bool up = p.Phase is SpeedTestPhase.Upload or SpeedTestPhase.UploadWarmup;
            bool warmup = p.Phase is SpeedTestPhase.DownloadWarmup or SpeedTestPhase.UploadWarmup;
            string label = loc(down ? "Str_Speed_Download" : up ? "Str_Speed_Upload" : "Str_Speed_Idle");
            if (p.IsPhaseComplete && !warmup && (down || up))
                return "\r\u001b[2K" + Row(label, Value(p.Mbps, "Mbps"), down ? 36 : 35);
            string value = Value(down || up ? p.Mbps : p.LatencyMs, down || up ? "Mbps" : "ms");
            string text = label + "  " + value + (warmup ? "  " + loc("Str_Speed_Warmup") : "");
            int barWidth = Math.Max(4, Math.Min(16, Width / 4));
            int filled = Math.Min(barWidth, (int)(p.Elapsed.TotalSeconds / (warmup ? 6 : 10) * barWidth));
            string bar = "[" + new string('=', Math.Max(0, filled)) + new string(' ', barWidth - Math.Max(0, filled)) + "] ";
            if (down || up) text = bar + text;
            if (text.Length > Width) text = text[..Width];
            return "\r\u001b[2K\u001b[" + (down ? "36" : up ? "35" : "33") + "m" + text + Reset;
        }

        public string Result(SpeedTestResult result)
        {
            var text = new StringBuilder("\r\u001b[2K").Append(Rule);
            text.Append(Row(loc("Str_Speed_Download"), Value(result.Download.Mbps, "Mbps"), 36));
            text.Append(Row(loc("Str_Speed_Upload"), Value(result.Upload.Mbps, "Mbps"), 35));
            text.Append(Row(loc("Str_Speed_Idle"), Value(result.IdleLatencyMs, "ms"), 33));
            text.Append(Row(loc("Str_Speed_Jitter"), Value(result.JitterMs, "ms"), 33));
            text.Append("\r\n\u001b[37m  " + loc("Str_Speed_Loaded") + Reset + "\r\n");
            text.Append(Row("  " + loc("Str_Speed_Download"), Value(result.Download.LoadedLatencyMs, "ms"), 36));
            text.Append(Row("  " + loc("Str_Speed_Upload"), Value(result.Upload.LoadedLatencyMs, "ms"), 35));
            text.Append(Row(loc("Str_Speed_Streams"), result.Download.StreamCount + " / " + result.Upload.StreamCount, 37));
            double mib = (result.Download.BytesTransferred + result.Download.WarmupBytes + result.Upload.BytesTransferred + result.Upload.WarmupBytes) / 1048576d;
            text.Append(Row(loc("Str_Speed_Transferred"), mib.ToString("N1") + " MiB", 37));
            bool complete = result.Download.CompletedDuration && result.Upload.CompletedDuration;
            text.Append(Rule).Append("  \u001b[" + (complete ? "32" : "33") + "m" +
                loc(complete ? "Str_Speed_Completed" : "Str_Speed_Limited") + Reset + "\r\n\r\n");
            return text.ToString();
        }
    }
}
