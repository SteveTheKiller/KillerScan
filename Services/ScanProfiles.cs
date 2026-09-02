using System.IO;
using System.Text.Json;

namespace KillerScan.Services
{
    internal sealed class ScanProfile
    {
        public string Name { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public bool DeepScanAfter { get; set; }
    }

    internal static class ScanProfiles
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "profiles.json");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static List<ScanProfile> _items = [];

        public static IReadOnlyList<ScanProfile> Items => _items;

        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                _items = JsonSerializer.Deserialize<List<ScanProfile>>(File.ReadAllText(FilePath)) ?? [];
            }
            catch { _items = []; }
        }

        public static void Save(ScanProfile profile)
        {
            _items.RemoveAll(item => string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            _items.Add(profile);
            Persist();
        }

        public static void Delete(ScanProfile profile)
        {
            _items.Remove(profile);
            Persist();
        }

        public static void Update() => Persist();

        private static void Persist()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_items, JsonOptions));
            }
            catch { }
        }
    }
}
