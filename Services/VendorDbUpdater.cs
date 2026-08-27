using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace KillerScan.Services
{
    /// <summary>
    /// Refreshes the MAC vendor database from the internet, in-app (no script needed). Pulls the
    /// Wireshark "manuf" list first (full MA-L + MA-M + MA-S, gzip-served) and falls back to Nmap's
    /// mac-prefixes (MA-L, always reachable on GitHub). Writes OuiLookup.ExternalFilePath and reloads.
    ///
    /// Safety: it will never replace your list with a smaller one - a blocked or partial download
    /// leaves the existing database untouched.
    /// </summary>
    public static class VendorDbUpdater
    {
        private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        private const string ManufUrl = "https://www.wireshark.org/download/automated/data/manuf";
        private const string NmapUrl  = "https://raw.githubusercontent.com/nmap/nmap/master/nmap-mac-prefixes";

        /// <summary>What the updater has to say, as a RESOURCE KEY plus its numeric arguments -
        /// never as a finished sentence. This service has no access to the resource dictionary and
        /// should not: giving a background downloader a localizer would point it at the UI. The
        /// caller looks the key up and formats the counts in the user's own culture
        /// (AboutController.Format). One exception message rides along as Detail, because an
        /// exception's text is not ours to translate.</summary>
        public readonly struct Status
        {
            public readonly string Key;
            public readonly int[] Args;
            public readonly string? Detail;
            public Status(string key, params int[] args) { Key = key; Args = args; Detail = null; }
            public Status(string key, string detail) { Key = key; Args = []; Detail = detail; }
        }

        public static async Task<(bool ok, int count, Status status)> UpdateAsync(IProgress<Status>? progress = null)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            int current = OuiLookup.Count;

            StringBuilder? best = null; int bestCount = 0;

            progress?.Report(new Status("Str_Db_Downloading"));
            try
            {
                var (n, sb) = ParseManuf(await DownloadTextAsync(ManufUrl).ConfigureAwait(false));
                if (n > bestCount) { bestCount = n; best = sb; }
            }
            catch { /* try the fallback */ }

            if (bestCount < Math.Max(1000, current))
            {
                progress?.Report(new Status("Str_Db_Fallback"));
                try
                {
                    var (n, sb) = ParseNmap(await DownloadTextAsync(NmapUrl).ConfigureAwait(false));
                    if (n > bestCount) { bestCount = n; best = sb; }
                }
                catch { /* fall through to the guard below */ }
            }

            if (best == null || bestCount < 1000)
                return (false, current, new Status("Str_Db_Blocked"));
            if (bestCount < current)
                return (false, current, new Status("Str_Db_Smaller", bestCount, current));

            var path = OuiLookup.ExternalFilePath;
            var newContent = best.ToString();

            // Nothing changed since the last refresh? Say so instead of claiming an update.
            if (OuiLookup.UsingExternal && File.Exists(path))
            {
                try
                {
                    if (File.ReadAllText(path) == newContent)
                        return (true, current, new Status("Str_Db_UpToDate", current));
                }
                catch { /* unreadable - fall through and rewrite */ }
            }

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, newContent, new UTF8Encoding(false));
                OuiLookup.Reload();
            }
            catch (Exception ex)
            {
                return (false, current, new Status("Str_Db_SaveFailed", ex.Message));
            }

            return (true, OuiLookup.Count, new Status("Str_Db_Updated", OuiLookup.Count));
        }

        private static async Task<string> DownloadTextAsync(string url)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.TryParseAdd(Ua);
            var bytes = await http.GetByteArrayAsync(url).ConfigureAwait(false);
            // manuf is served gzip-compressed; decompress when the gzip magic (1F 8B) is present.
            if (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b)
            {
                using var ms = new MemoryStream(bytes);
                using var gz = new GZipStream(ms, CompressionMode.Decompress);
                using var sr = new StreamReader(gz, Encoding.UTF8);
                return sr.ReadToEnd();
            }
            return Encoding.UTF8.GetString(bytes);
        }

        // Wireshark manuf: "<prefix[/mask]>\t<short>\t<long>"  (/28 = MA-M, /36 = MA-S, none = MA-L).
        private static (int, StringBuilder) ParseManuf(string content)
        {
            var sb = new StringBuilder(); int n = 0;
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t');
                if (f.Length < 2) continue;
                var prefix = f[0].Trim();
                if (prefix.Length == 0) continue;
                var vendor = (f.Length >= 3 && f[2].Trim().Length > 0) ? f[2].Trim() : f[1].Trim();
                if (vendor.Length == 0) continue;

                int bits = 24;
                int slash = prefix.IndexOf('/');
                if (slash >= 0)
                {
                    int.TryParse(prefix[(slash + 1)..], out bits);
                    prefix = prefix[..slash];
                }
                if (bits <= 0) bits = 24;
                int nib = bits / 4;
                var hex = new string([.. prefix.Where(Uri.IsHexDigit)]).ToUpperInvariant();
                if (hex.Length < nib) continue;
                sb.Append(hex[..nib]).Append('\t').Append(vendor).Append('\n');
                n++;
            }
            return (n, sb);
        }

        // Nmap mac-prefixes: "<6 hex> <vendor>", comment lines start with '#'.
        private static (int, StringBuilder) ParseNmap(string content)
        {
            var sb = new StringBuilder(); int n = 0;
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                int sp = line.IndexOf(' ');
                if (sp < 6) continue;
                var hex = line[..sp].Trim().ToUpperInvariant();
                if (hex.Length < 6) continue;
                bool allHex = true;
                for (int i = 0; i < 6; i++) if (!Uri.IsHexDigit(hex[i])) { allHex = false; break; }
                if (!allHex) continue;
                var vendor = line[(sp + 1)..].Trim();
                if (vendor.Length == 0) continue;
                sb.Append(hex[..6]).Append('\t').Append(vendor).Append('\n');
                n++;
            }
            return (n, sb);
        }
    }
}
