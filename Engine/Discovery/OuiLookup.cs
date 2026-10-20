using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace KillerScan.Engine
{
    /// <summary>
    /// MAC OUI vendor lookup using the full MA-L/MA-M/MA-S registry set (~58k entries, from the
    /// Wireshark manuf list; build/update-oui.ps1 refreshes the bundled copy).
    /// Prefers a user-refreshed copy in %LOCALAPPDATA%\KillerScan\oui.txt; otherwise falls back to
    /// the copy embedded in the .exe. The in-app "update vendor database" action writes the external
    /// file and calls Reload().
    /// </summary>
    public static class OuiLookup
    {
        internal static readonly object PublicationGate = new();
        private static Snapshot? _snapshot;

        private sealed class Snapshot(Dictionary<string, string> table, bool external, DateTime? refreshed, bool failed)
        {
            internal readonly Dictionary<string, string> Table = table;
            internal readonly bool External = external;
            internal readonly DateTime? Refreshed = refreshed;
            internal readonly bool Failed = failed;
        }

        private static Snapshot Current
        {
            get
            {
                var snapshot = Volatile.Read(ref _snapshot);
                if (snapshot != null) return snapshot;
                lock (PublicationGate)
                {
                    snapshot = _snapshot;
                    if (snapshot == null)
                    {
                        snapshot = LoadCore(ExternalFilePath);
                        Volatile.Write(ref _snapshot, snapshot);
                    }
                    return snapshot;
                }
            }
        }

        /// <summary>Writable copy that overrides the embedded list once the user refreshes it.</summary>
        public static string ExternalFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "oui.txt");

        /// <summary>When the external list was last written; null when running on the bundled copy.</summary>
        public static DateTime? LastRefreshed => Current.Refreshed;

        /// <summary>True when the loaded list is the user-refreshed external file rather than the bundled one.</summary>
        public static bool UsingExternal => Current.External;

        /// <summary>Human-readable provenance for the About screen.</summary>
        public static string LastRefreshedDisplay
        {
            get
            {
                var snapshot = Current;
                return snapshot.Refreshed.HasValue ? $"refreshed {snapshot.Refreshed.Value:yyyy-MM-dd}" : "bundled with the app";
            }
        }

        public static void Load() => _ = Current;

        /// <summary>Re-reads the database from disk (used after an in-app refresh).</summary>
        public static void Reload() => ReloadFrom(ExternalFilePath);

        internal static bool ReloadFrom(string externalPath)
        {
            lock (PublicationGate)
            {
                var snapshot = LoadCore(externalPath);
                // A failed refresh never withdraws a complete table already serving readers.
                if (snapshot.Failed && _snapshot != null) return false;
                Volatile.Write(ref _snapshot, snapshot);
                return !snapshot.Failed;
            }
        }

        private static Snapshot LoadCore(string externalPath)
        {
            // Load whichever list is LARGER. A new app version can bundle a fresher database than
            // the user's last in-app refresh, and an upgrade should never keep serving older data
            // just because an external copy exists. A tie goes to the external copy, so a user
            // refresh that matches the bundle keeps its provenance and refresh date in About.
            var external = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool failed = false;
            DateTime? refreshed = null;
            try
            {
                using var r = new StreamReader(externalPath);
                LoadFrom(r, external);
                refreshed = File.GetLastWriteTime(externalPath);
                if (external.Count == 0) failed = true;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch { external.Clear(); failed = true; }

            var bundled = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var resourceName = assembly.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith("oui.txt"));
                if (resourceName == null) failed = true;
                else
                {
                    using var stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream != null)
                        using (var reader = new StreamReader(stream))
                            LoadFrom(reader, bundled);
                    else failed = true;
                }
            }
            catch { bundled.Clear(); failed = true; }

            bool useExternal = external.Count > 0 && external.Count >= bundled.Count;
            var chosen = useExternal ? external : bundled;
            return new Snapshot(chosen, useExternal, useExternal ? refreshed : null, failed);
        }

        private static void LoadFrom(StreamReader reader, Dictionary<string, string> table)
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split(['\t'], 2);
                if (parts.Length == 2)
                {
                    // Normalize the key to raw uppercase hex so MA-L (6 hex / 24-bit), MA-M (7 / 28-bit)
                    // and MA-S (9 / 36-bit) prefixes all live in one table and longest-match wins.
                    var key = parts[0].Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
                    if (key.Length >= 6) table[key] = parts[1];
                }
            }
        }

        public static string GetVendor(string macAddress)
        {
            var table = Current.Table;
            if (string.IsNullOrEmpty(macAddress)) return string.Empty;

            // Normalize the MAC to raw uppercase hex, then try the most specific block first:
            // MA-S (36-bit / 9 hex) -> MA-M (28-bit / 7 hex) -> MA-L (24-bit / 6 hex).
            var hex = new string([.. macAddress.Where(Uri.IsHexDigit)]).ToUpperInvariant();
            if (hex.Length < 6) return string.Empty;
            if (hex.Length >= 9 && table.TryGetValue(hex[..9], out var v9)) return v9;
            if (hex.Length >= 7 && table.TryGetValue(hex[..7], out var v7)) return v7;
            return table.TryGetValue(hex[..6], out var v6) ? v6 : string.Empty;
        }

        public static int Count => Current.Table.Count;
    }
}
