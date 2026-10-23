using System.IO;
using System.Text.Json;
using KillerScan.Models;

namespace KillerScan.Services
{
    internal static class DevicePreferences
    {
        private static string FilePath => Locations.DataFile("devices.json");
        private static bool _readable = true;
        internal static bool IsReadable => _readable;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static DevicePreferenceData _data = new();

        public static bool HasTrustedDevices => _data.Trusted.Count > 0;

        public static void Load()
        {
            _data = new(); _readable = true;
            // Demo mode starts from nothing and stays in memory, so the fabricated network cannot
            // trust, rename, or otherwise disturb the real devices on the machine it runs on.
            if (DemoData.Enabled) { _data = new DevicePreferenceData(); return; }
            try
            {
                if (!File.Exists(FilePath)) return;
                _data = JsonSerializer.Deserialize<DevicePreferenceData>(File.ReadAllText(FilePath)) ?? new();
                _data.Normalize();
            }
            catch
            {
                _readable = false;
                _data = new DevicePreferenceData();
            }
        }

        public static void Apply(NetworkDevice device)
        {
            if (device.LocationId != null && device.LocationId != Locations.Current.Id) return;
            if (_data.Names.TryGetValue(DeviceIdentity.For(device), out string? name) &&
                !string.IsNullOrWhiteSpace(name))
                device.Hostname = name;
        }

        internal static Action<NetworkDevice> CaptureApply()
        {
            var names = new Dictionary<string, string>(_data.Names, StringComparer.OrdinalIgnoreCase);
            return device =>
            {
                if (names.TryGetValue(DeviceIdentity.For(device), out string? name) && !string.IsNullOrWhiteSpace(name))
                    device.Hostname = name;
            };
        }

        public static string? GetName(NetworkDevice device) =>
            (device.LocationId == null || device.LocationId == Locations.Current.Id) && _data.Names.TryGetValue(DeviceIdentity.For(device), out string? name) ? name : null;

        public static void SetName(NetworkDevice device, string? name)
        {
            if (device.LocationId != null && device.LocationId != Locations.Current.Id) return;
            string identity = DeviceIdentity.For(device);
            if (string.IsNullOrWhiteSpace(name)) _data.Names.Remove(identity);
            else _data.Names[identity] = name!.Trim();
            Save();
        }

        public static bool IsTrusted(NetworkDevice device) => (device.LocationId == null || device.LocationId == Locations.Current.Id) && _data.Trusted.Contains(DeviceIdentity.For(device));

        public static void SetTrusted(NetworkDevice device, bool trusted)
        {
            if (device.LocationId != null && device.LocationId != Locations.Current.Id) return;
            string identity = DeviceIdentity.For(device);
            if (trusted) _data.Trusted.Add(identity);
            else _data.Trusted.Remove(identity);
            Save();
        }

        public static void TrustAll(IEnumerable<NetworkDevice> devices)
        {
            foreach (var device in devices.Where(device => device.LocationId == null || device.LocationId == Locations.Current.Id)) _data.Trusted.Add(DeviceIdentity.For(device));
            Save();
        }

        private static void Save()
        {
            if (DemoData.Enabled || !_readable) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                LocationCatalog.WriteAtomic(FilePath, JsonSerializer.Serialize(_data, JsonOptions));
            }
            catch { }
        }

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
