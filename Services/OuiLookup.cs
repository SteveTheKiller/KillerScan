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
                    OuiTable[parts[0]] = parts[1];
                }
            }
            _loaded = true;
        }

        public static string GetVendor(string macAddress)
        {
            if (!_loaded) Load();

            if (string.IsNullOrEmpty(macAddress) || macAddress.Length < 8)
                return string.Empty;

            // Try the first 3 octets (XX:XX:XX)
            string prefix = macAddress[..8].ToUpperInvariant();
            return OuiTable.TryGetValue(prefix, out var vendor) ? vendor : string.Empty;
        }

        public static int Count => OuiTable.Count;
    }
}
