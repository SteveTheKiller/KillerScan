using System;
using System.IO;
using System.Reflection;

namespace KillerScan.Services
{
    /// <summary>
    /// MAC OUI vendor lookup using the full IEEE OUI database (~41k entries across MA-L/MA-M/MA-S).
    /// Prefers a user-refreshed copy in %LOCALAPPDATA%\KillerScan\oui.txt; otherwise falls back to
    /// the copy embedded in the .exe. The in-app "update vendor database" action writes the external
    /// file and calls Reload().
    /// </summary>
    public static class OuiLookup
    {
        private static readonly Dictionary<string, string> OuiTable = new(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        /// <summary>Writable copy that overrides the embedded list once the user refreshes it.</summary>
        public static string ExternalFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "oui.txt");

        /// <summary>When the external list was last written; null when running on the bundled copy.</summary>
        public static DateTime? LastRefreshed { get; private set; }

        /// <summary>True when the loaded list is the user-refreshed external file rather than the bundled one.</summary>
        public static bool UsingExternal { get; private set; }

        /// <summary>Human-readable provenance for the About screen.</summary>
        public static string LastRefreshedDisplay =>
            LastRefreshed.HasValue ? $"refreshed {LastRefreshed.Value:yyyy-MM-dd}" : "bundled with the app";

        public static void Load()
        {
            if (_loaded) return;
            LoadCore();
            _loaded = true;
        }

        /// <summary>Re-reads the database from disk (used after an in-app refresh).</summary>
        public static void Reload()
        {
            OuiTable.Clear();
            LoadCore();
            _loaded = true;
        }

        private static void LoadCore()
        {
            // Load whichever list is LARGER. A new app version can bundle a fresher database than
            // the user's last in-app refresh, and an upgrade should never keep serving older data
            // just because an external copy exists. A tie goes to the external copy, so a user
            // refresh that matches the bundle keeps its provenance and refresh date in About.
            var external = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(ExternalFilePath))
                    using (var r = new StreamReader(ExternalFilePath))
                        LoadFrom(r, external);
            }
            catch { external.Clear(); }

            var bundled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("oui.txt"));
                if (resourceName != null)
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                        using (var reader = new StreamReader(stream))
                            LoadFrom(reader, bundled);
                }
            }
            catch { bundled.Clear(); }

            bool useExternal = external.Count > 0 && external.Count >= bundled.Count;
            var chosen = useExternal ? external : bundled;
            foreach (var kv in chosen) OuiTable[kv.Key] = kv.Value;
            UsingExternal = useExternal;
            LastRefreshed = useExternal ? File.GetLastWriteTime(ExternalFilePath) : (DateTime?)null;
        }

        private static void LoadFrom(StreamReader reader, Dictionary<string, string> table)
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split(['\t'], 2);
                if (parts.Length == 2)
                {
                    // Normalise the key to raw uppercase hex so MA-L (6 hex / 24-bit), MA-M (7 / 28-bit)
                    // and MA-S (9 / 36-bit) prefixes all live in one table and longest-match wins.
                    var key = parts[0].Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
                    if (key.Length >= 6) table[key] = parts[1];
                }
            }
        }

        public static string GetVendor(string macAddress)
        {
            if (!_loaded) Load();
            if (string.IsNullOrEmpty(macAddress)) return string.Empty;

            // Normalise the MAC to raw uppercase hex, then try the most specific block first:
            // MA-S (36-bit / 9 hex) -> MA-M (28-bit / 7 hex) -> MA-L (24-bit / 6 hex).
            var hex = new string([.. macAddress.Where(Uri.IsHexDigit)]).ToUpperInvariant();
            if (hex.Length < 6) return string.Empty;
            if (hex.Length >= 9 && OuiTable.TryGetValue(hex[..9], out var v9)) return v9;
            if (hex.Length >= 7 && OuiTable.TryGetValue(hex[..7], out var v7)) return v7;
            return OuiTable.TryGetValue(hex[..6], out var v6) ? v6 : string.Empty;
        }

        public static int Count => OuiTable.Count;
    }
}
