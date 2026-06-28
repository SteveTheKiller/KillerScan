using System.IO;
using System.Reflection;

namespace KillerScan.Services
{
    /// <summary>
    /// MAC OUI vendor lookup using the full IEEE OUI database (~39k entries).
    /// Loaded once from embedded resource at startup.
    /// </summary>
    public static class OuiLookup
    {
        private static readonly Dictionary<string, string> OuiTable = new(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("oui.txt"));

            if (resourceName == null) return;

            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return;

            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split(new[] { '\t' }, 2);
                if (parts.Length == 2)
                {
                    // Normalise the key to raw uppercase hex so MA-L (6 hex / 24-bit), MA-M (7 / 28-bit)
                    // and MA-S (9 / 36-bit) prefixes all live in one table and longest-match wins.
                    var key = parts[0].Replace(":", "").Replace("-", "").Trim().ToUpperInvariant();
                    if (key.Length >= 6) OuiTable[key] = parts[1];
                }
            }
            _loaded = true;
        }

        public static string GetVendor(string macAddress)
        {
            if (!_loaded) Load();
            if (string.IsNullOrEmpty(macAddress)) return string.Empty;

            // Normalise the MAC to raw uppercase hex, then try the most specific block first:
            // MA-S (36-bit / 9 hex) -> MA-M (28-bit / 7 hex) -> MA-L (24-bit / 6 hex).
            var hex = new string(macAddress.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
            if (hex.Length < 6) return string.Empty;
            if (hex.Length >= 9 && OuiTable.TryGetValue(hex.Substring(0, 9), out var v9)) return v9;
            if (hex.Length >= 7 && OuiTable.TryGetValue(hex.Substring(0, 7), out var v7)) return v7;
            return OuiTable.TryGetValue(hex.Substring(0, 6), out var v6) ? v6 : string.Empty;
        }

        public static int Count => OuiTable.Count;
    }
}
