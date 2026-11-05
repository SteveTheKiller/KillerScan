using System.IO;
using System.Text.Json;
using KillerScan.Models;

namespace KillerScan.Services
{
    /// <summary>Saved device names and trust for one Location (devices.json).</summary>
    internal sealed class DevicePreferenceStore
    {
        private readonly JsonPreferenceFile<DevicePreferenceData> _store;
        private readonly bool _memoryOnly;
        private DevicePreferenceData _data => _store.Value;
        public string? SaveError => _store.Error;

        // Demo mode starts from nothing and stays in memory, so the fabricated network cannot
        // trust, rename, or otherwise disturb the real devices on the machine it runs on.
        private bool MemoryOnly => _memoryOnly || DemoData.Enabled;

        internal DevicePreferenceStore(string path, bool memoryOnly = false)
        {
            _memoryOnly = memoryOnly;
            _store = new(path, () => new(), value => { value.Normalize(); return value; });
        }

        public bool HasTrustedDevices => _data.Trusted.Count > 0;

        /// <summary>True once a completed scan has set this Location's Known baseline.</summary>
        public bool HasBaseline => _data.Baseline;

        /// <summary>Seen in this Location before. Known says nothing about whether it is approved.</summary>
        public bool IsKnown(NetworkDevice device) => _data.Known.Contains(DeviceIdentity.For(device));

        /// <summary>
        /// Records a completed scan's devices as Known. The first one sets the baseline and flags
        /// nothing; after that, only devices outside the Known list are returned as new. Approval
        /// is never changed here: nothing becomes Approved without the technician.
        /// </summary>
        public Observation Observe(IReadOnlyCollection<NetworkDevice> devices)
        {
            // A file that was briefly locked when the Location opened loaded as empty. It is read
            // again first, or this run would be taken as a fresh baseline and flag nothing as new.
            // Still unreadable, nothing can be said: these devices stay unseen for the next scan.
            if (!MemoryOnly && _store.Error != null && !_store.Refresh()) return new Observation(false, [], false);
            var identities = devices.Select(DeviceIdentity.For).ToArray();
            bool baseline = false;
            IReadOnlyList<NetworkDevice> fresh = [];
            // Worked out over the latest file under its lock, not this window's copy: another
            // KillerScan may have scanned this Location since this one loaded it.
            void Record(DevicePreferenceData value)
            {
                baseline = !value.Baseline;
                fresh = baseline ? [] : devices.Where(device => !value.Known.Contains(DeviceIdentity.For(device))).ToList();
                foreach (string identity in identities) value.Known.Add(identity);
                value.Baseline = true;
            }
            // A file that cannot be written falls back on what this window has, kept for a retry.
            bool saved = !MemoryOnly && _store.Commit(Record);
            if (!saved) saved = _store.Change(Record, MemoryOnly);
            return new Observation(baseline, fresh, saved);
        }

        public void Load()
        {
            if (!MemoryOnly) _store.Load();
        }

        public void Apply(NetworkDevice device)
        {
            if (_data.Names.TryGetValue(DeviceIdentity.For(device), out string? name) &&
                !string.IsNullOrWhiteSpace(name))
                Rename(device, name);
        }

        // The name a scan found (reverse DNS, mDNS, NetBIOS) before a custom name replaced it, so
        // clearing the custom name brings it back. Held beside the device, never saved with it.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NetworkDevice, string> Discovered = new();

        /// <summary>Shows a custom name, remembering the discovered one the first time.</summary>
        internal static void Rename(NetworkDevice device, string name)
        {
            if (!Discovered.TryGetValue(device, out _)) Discovered.Add(device, device.Hostname);
            device.Hostname = name.Trim();
        }

        /// <summary>Puts back the name the scan found, or the NetBIOS name when nothing was kept.</summary>
        internal static void Restore(NetworkDevice device)
        {
            if (Discovered.TryGetValue(device, out string? found)) { device.Hostname = found; Discovered.Remove(device); }
            else device.Hostname = string.IsNullOrWhiteSpace(device.NetbiosName) ? string.Empty : device.NetbiosName;
        }

        public string? GetName(NetworkDevice device) =>
            _data.Names.TryGetValue(DeviceIdentity.For(device), out string? name) ? name : null;

        public bool SetName(NetworkDevice device, string? name)
        {
            string identity = DeviceIdentity.For(device);
            return _store.Change(value =>
            {
                if (string.IsNullOrWhiteSpace(name)) value.Names.Remove(identity);
                else value.Names[identity] = name!.Trim();
            }, MemoryOnly);
        }

        /// <summary>Every custom name, by device identity.</summary>
        internal IReadOnlyDictionary<string, string> Names => _data.Names;

        /// <summary>Adds names by device identity in one save.</summary>
        internal bool SetNames(IReadOnlyDictionary<string, string> names)
        {
            if (names.Count == 0) return true;
            var added = names.ToArray();
            return _store.Change(value =>
            {
                foreach (var pair in added) value.Names[pair.Key] = pair.Value;
            }, MemoryOnly);
        }

        public bool IsTrusted(NetworkDevice device) => _data.Trusted.Contains(DeviceIdentity.For(device));

        public bool SetTrusted(NetworkDevice device, bool trusted)
        {
            string identity = DeviceIdentity.For(device);
            return _store.Change(value =>
            {
                if (trusted) value.Trusted.Add(identity);
                else value.Trusted.Remove(identity);
            }, MemoryOnly);
        }

        public bool TrustAll(IEnumerable<NetworkDevice> devices)
        {
            var identities = devices.Select(DeviceIdentity.For).ToArray();
            return _store.Change(value =>
            {
                foreach (string identity in identities) value.Trusted.Add(identity);
            }, MemoryOnly);
        }

        public bool Retry() => MemoryOnly || _store.Save();

        private sealed class DevicePreferenceData
        {
            public Dictionary<string, string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            /// <summary>Approved devices. The JSON name predates Approved and is kept for older files.</summary>
            public HashSet<string> Trusted { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Known { get; set; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>Stored on its own so an empty approval list never means "no baseline yet".</summary>
            public bool Baseline { get; set; }

            public void Normalize()
            {
                Names = new Dictionary<string, string>(Names ?? [], StringComparer.OrdinalIgnoreCase);
                Trusted = new HashSet<string>(Trusted ?? [], StringComparer.OrdinalIgnoreCase);
                Known = new HashSet<string>(Known ?? [], StringComparer.OrdinalIgnoreCase);
                // Files from before Known existed: their trusted list was the baseline.
                if (!Baseline && Trusted.Count > 0) { Known.UnionWith(Trusted); Baseline = true; }
            }
        }
    }

    /// <summary>The result of recording a scan as Known.</summary>
    internal sealed record Observation(bool BaselineCreated, IReadOnlyList<NetworkDevice> NewDevices, bool Saved);

    /// <summary>The app-wide device store, until every caller works through a Location.</summary>
    internal static class DevicePreferences
    {
        internal static string FilePath => AppDataFolder.File("devices.json");
        internal static DevicePreferenceStore Store { get; private set; } = new(FilePath);

        public static string? SaveError => Store.SaveError;
        public static bool HasTrustedDevices => Store.HasTrustedDevices;
        public static void Load() { Store = new(FilePath); Store.Load(); }
        public static void Apply(NetworkDevice device) => Store.Apply(device);
        public static string? GetName(NetworkDevice device) => Store.GetName(device);
        public static bool SetName(NetworkDevice device, string? name) => Store.SetName(device, name);
        public static bool IsTrusted(NetworkDevice device) => Store.IsTrusted(device);
        public static bool SetTrusted(NetworkDevice device, bool trusted) => Store.SetTrusted(device, trusted);
        public static bool TrustAll(IEnumerable<NetworkDevice> devices) => Store.TrustAll(devices);
        public static bool Retry() => Store.Retry();
    }
}
