using System.IO;
using System.Text.Json;
using KillerScan.Models;

namespace KillerScan.Services
{
    internal static class DevicePreferences
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "devices.json");
        private static JsonPreferenceFile<DevicePreferenceData> _store = CreateStore();
        private static DevicePreferenceData _data => _store.Value;
        public static string? SaveError => _store.Error;
        private static JsonPreferenceFile<DevicePreferenceData> CreateStore() => new(FilePath,
            () => new(), value => { value.Normalize(); return value; });

        public static bool HasTrustedDevices => _data.Trusted.Count > 0;

        public static void Load()
        {
            // Demo mode starts from nothing and stays in memory, so the fabricated network cannot
            // trust, rename, or otherwise disturb the real devices on the machine it runs on.
            _store = CreateStore();
            if (!DemoData.Enabled) _store.Load();
        }

        public static void Apply(NetworkDevice device)
        {
            if (_data.Names.TryGetValue(DeviceIdentity.For(device), out string? name) &&
                !string.IsNullOrWhiteSpace(name))
                device.Hostname = name;
        }

        public static string? GetName(NetworkDevice device) =>
            _data.Names.TryGetValue(DeviceIdentity.For(device), out string? name) ? name : null;

        public static bool SetName(NetworkDevice device, string? name)
        {
            string identity = DeviceIdentity.For(device);
            return _store.Change(value =>
            {
                if (string.IsNullOrWhiteSpace(name)) value.Names.Remove(identity);
                else value.Names[identity] = name!.Trim();
            }, DemoData.Enabled);
        }

        public static bool IsTrusted(NetworkDevice device) => _data.Trusted.Contains(DeviceIdentity.For(device));

        public static bool SetTrusted(NetworkDevice device, bool trusted)
        {
            string identity = DeviceIdentity.For(device);
            return _store.Change(value =>
            {
                if (trusted) value.Trusted.Add(identity);
                else value.Trusted.Remove(identity);
            }, DemoData.Enabled);
        }

        public static bool TrustAll(IEnumerable<NetworkDevice> devices)
        {
            var identities = devices.Select(DeviceIdentity.For).ToArray();
            return _store.Change(value =>
            {
                foreach (string identity in identities) value.Trusted.Add(identity);
            }, DemoData.Enabled);
        }

        public static bool Retry() => DemoData.Enabled || _store.Save();

        private sealed class DevicePreferenceData
        {
            public Dictionary<string, string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Trusted { get; set; } = new(StringComparer.OrdinalIgnoreCase);

            public void Normalize()
            {
                Names = new Dictionary<string, string>(Names, StringComparer.OrdinalIgnoreCase);
                Trusted = new HashSet<string>(Trusted, StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
