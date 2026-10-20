using System.IO;
using System.Text.Json;

namespace KillerScan.Services
{
    /// <summary>
    /// Persists manual device-type overrides keyed by MAC address to a local JSON file.
    /// </summary>
    public static class DeviceOverrides
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "overrides.json");

        private static JsonPreferenceFile<Dictionary<string, string>> _store = CreateStore();
        private static Dictionary<string, string> _overrides => _store.Value;
        public static string? SaveError => _store.Error;
        private static JsonPreferenceFile<Dictionary<string, string>> CreateStore() => new(FilePath,
            () => new(StringComparer.OrdinalIgnoreCase), value => new(value, StringComparer.OrdinalIgnoreCase));

        public static void Load()
        {
            _store = CreateStore();
            if (!DemoData.Enabled) _store.Load();
        }

        public static bool Save() => DemoData.Enabled || _store.Save();

        /// <summary>
        /// Set a manual override for a device by MAC address. Pass null to remove.
        /// </summary>
        public static bool Set(string mac, string? deviceType)
        {
            if (string.IsNullOrEmpty(mac)) return true;
            string key = mac.ToUpperInvariant();
            return _store.Change(values =>
            {
                if (deviceType == null) values.Remove(key);
                else values[key] = deviceType;
            }, DemoData.Enabled);
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
    }
}
