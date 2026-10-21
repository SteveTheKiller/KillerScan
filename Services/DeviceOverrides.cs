using System.IO;
using System.Text.Json;

namespace KillerScan.Services
{
    /// <summary>
    /// Manual device-type overrides keyed by MAC address for one Location (overrides.json).
    /// </summary>
    internal sealed class DeviceOverrideStore
    {
        private readonly JsonPreferenceFile<Dictionary<string, string>> _store;
        private readonly bool _memoryOnly;
        private Dictionary<string, string> _overrides => _store.Value;
        public string? SaveError => _store.Error;
        private bool MemoryOnly => _memoryOnly || DemoData.Enabled;

        internal DeviceOverrideStore(string path, bool memoryOnly = false)
        {
            _memoryOnly = memoryOnly;
            _store = new(path, () => new(StringComparer.OrdinalIgnoreCase), value => new(value, StringComparer.OrdinalIgnoreCase));
        }

        public void Load()
        {
            if (!MemoryOnly) _store.Load();
        }

        public bool Save() => MemoryOnly || _store.Save();

        /// <summary>
        /// Set a manual override for a device by MAC address. Pass null to remove.
        /// </summary>
        public bool Set(string mac, string? deviceType)
        {
            if (string.IsNullOrEmpty(mac)) return true;
            string key = mac.ToUpperInvariant();
            return _store.Change(values =>
            {
                if (deviceType == null) values.Remove(key);
                else values[key] = deviceType;
            }, MemoryOnly);
        }

        /// <summary>
        /// Try to get the manual override for a MAC address.
        /// </summary>
        public string? Get(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return null;
            return _overrides.TryGetValue(mac.ToUpperInvariant(), out var val) ? val : null;
        }

        /// <summary>
        /// Check if a device has a manual override.
        /// </summary>
        public bool Has(string mac)
        {
            if (string.IsNullOrEmpty(mac)) return false;
            return _overrides.ContainsKey(mac.ToUpperInvariant());
        }
    }

    /// <summary>The app-wide override store, until every caller works through a Location.</summary>
    public static class DeviceOverrides
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "overrides.json");
        internal static DeviceOverrideStore Store { get; private set; } = new(FilePath);

        public static string? SaveError => Store.SaveError;
        public static void Load() { Store = new(FilePath); Store.Load(); }
        public static bool Save() => Store.Save();
        public static bool Set(string mac, string? deviceType) => Store.Set(mac, deviceType);
        public static string? Get(string mac) => Store.Get(mac);
        public static bool Has(string mac) => Store.Has(mac);
    }
}
