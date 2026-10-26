using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace KillerScan.Engine
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
#if !NET
            // .NET Framework 4.8 can default to older TLS; modern .NET negotiates TLS 1.2+ itself.
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
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
                lock (OuiLookup.PublicationGate)
                {
                    current = OuiLookup.Count;
                    if (bestCount < current)
                        return (false, current, new Status("Str_Db_Smaller", bestCount, current));
                    PublishDatabase(path, newContent);
                    if (!OuiLookup.ReloadFrom(path)) throw new IOException("The saved vendor database could not be loaded. The prior lookup table is still active.");
                }
            }
            catch (Exception ex)
            {
                return (false, current, new Status("Str_Db_SaveFailed", ex.Message));
            }

            return (true, OuiLookup.Count, new Status("Str_Db_Updated", OuiLookup.Count));
        }

        internal static void PublishDatabase(string path, string content, Action<Stream, byte[]>? write = null)
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] bytes = new UTF8Encoding(false).GetBytes(content);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backup = temporary + ".original";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    if (write == null) stream.Write(bytes, 0, bytes.Length);
                    else write(stream, bytes);
                    stream.Flush(true);
                }
                if (!File.ReadAllBytes(temporary).SequenceEqual(bytes))
                    throw new IOException("The staged vendor database did not match the complete download.");
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, backup); }
                    catch
                    {
                        if (!File.Exists(path) && File.Exists(backup)) File.Move(backup, path);
                        throw;
                    }
                    TryDelete(backup);
                }
                else File.Move(temporary, path);
            }
            finally { TryDelete(temporary); }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // The full manuf list is a few megabytes; these leave ample room while keeping a hostile or
        // broken source from filling memory with an endless body or a small, highly compressed one.
        internal const int MaxDownloadBytes = 32 * 1024 * 1024;
        internal const int MaxTextBytes = 64 * 1024 * 1024;

        private static async Task<string> DownloadTextAsync(string url)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60), MaxResponseContentBufferSize = MaxDownloadBytes };
            http.DefaultRequestHeaders.UserAgent.TryParseAdd(Ua);
            using var response = await http.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            // .NET Framework follows a redirect from HTTPS to plain HTTP; the list must not come over one.
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps)
                throw new HttpRequestException("The vendor database was redirected away from HTTPS.");
            return Decode(await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
        }

        /// <summary>Text of a downloaded list. manuf is served gzip-compressed, so it is decompressed
        /// when the gzip magic (1F 8B) is present, never past <see cref="MaxTextBytes"/>.</summary>
        internal static string Decode(byte[] bytes)
        {
            if (bytes.Length > MaxDownloadBytes) throw new InvalidDataException("The vendor database download is too large.");
            if (bytes.Length < 2 || bytes[0] != 0x1f || bytes[1] != 0x8b) return Encoding.UTF8.GetString(bytes);
            using var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress);
            using var text = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = gz.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (text.Length + read > MaxTextBytes) throw new InvalidDataException("The vendor database expands beyond its size limit.");
                text.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(text.GetBuffer(), 0, (int)text.Length);
        }

        // Wireshark manuf: "<prefix[/mask]>\t<short>\t<long>"  (/28 = MA-M, /36 = MA-S, none = MA-L).
        internal static (int, StringBuilder) ParseManuf(string content)
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
                    if (!int.TryParse(prefix[(slash + 1)..], out bits)) continue;
                    prefix = prefix[..slash];
                }
                // The lookup keys MA-L, MA-M and MA-S blocks only; any other mask would be truncated
                // into a shorter key that overwrites the real owner of the wider block.
                if (bits != 24 && bits != 28 && bits != 36) continue;
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
