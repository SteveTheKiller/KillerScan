using System.IO;
using System.Text.Json;

namespace KillerScan.Services
{
    /// <summary>
    /// Persists manual device-type overrides keyed by MAC address to a local JSON file.
    /// </summary>
    public static class DeviceOverrides
    {
        private static string FilePath => Locations.DataFile("overrides.json");
        private static bool _readable = true;
        internal static bool IsReadable => _readable;

        private static Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

        public static void Load()
        {
            _overrides = new(StringComparer.OrdinalIgnoreCase); _readable = true;
            if (DemoData.Enabled) return;
            try
            {
                if (!File.Exists(FilePath)) return;
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (loaded != null)
                    _overrides = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch { _readable = false; }
        }

        public static void Save()
        {
            if (DemoData.Enabled || !_readable) return;
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var options = new JsonSerializerOptions { WriteIndented = true };
                LocationCatalog.WriteAtomic(FilePath, JsonSerializer.Serialize(_overrides, options));
            }
            catch { }
        }

        /// <summary>
        /// Set a manual override for a device by MAC address. Pass null to remove.
        /// </summary>
        public static void Set(string mac, string? deviceType)
        {
            if (string.IsNullOrEmpty(mac)) return;
            string key = mac.ToUpperInvariant();
            if (deviceType == null)
                _overrides.Remove(key);
            else
                _overrides[key] = deviceType;
            Save();
        }

        /// <summary>
        /// Try to get the manual override for a MAC address.
        /// </summary>
        public static string? Get(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            return _overrides.TryGetValue(mac.ToUpperInvariant(), out var val) ? val : null;
        }

        /// <summary>
        /// Check if a device has a manual override.
        /// </summary>
        public static bool Has(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return false;
            return _overrides.ContainsKey(mac.ToUpperInvariant());
        }

        internal static Func<string, string?> CaptureLookup()
        {
            var types = new Dictionary<string, string>(_overrides, StringComparer.OrdinalIgnoreCase);
            return mac => !string.IsNullOrEmpty(mac) && types.TryGetValue(mac, out string? value) ? value : null;
        }
    }
}
