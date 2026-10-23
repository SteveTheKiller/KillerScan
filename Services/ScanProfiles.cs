using System.IO;
using System.Text.Json;

namespace KillerScan.Services
{
    internal sealed class ScanProfile
    {
        public string Name { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public bool DeepScanAfter { get; set; }
        public ScanOptions Options { get; set; } = new();
        public string InterfaceId { get; set; } = "";
        public string SourceAddress { get; set; } = "";
        public string LocationId { get; set; } = LocationCatalog.LegacyId;

        /// <summary>What a screen reader announces for the profile's row in the profiles list.</summary>
        public override string ToString() => Name + ", " + Target;
    }

    internal static class ScanProfiles
    {
        private static string FilePath => Locations.DataFile("profiles.json");
        private static bool _readable = true;
        internal static bool IsReadable => _readable;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static List<ScanProfile> _items = [];

        public static IReadOnlyList<ScanProfile> Items => _items;

        public static void Load()
        {
            _items = []; _readable = true;
            // Demo mode keeps its profiles in memory: a screenshot build must not add fabricated
            // profiles to whatever the person running it has saved.
            if (DemoData.Enabled)
            {
                _items = [.. DemoData.Profiles()];
                foreach (var item in _items) item.LocationId = Locations.Current.Id;
                return;
            }
            try
            {
                if (!File.Exists(FilePath)) return;
                _items = JsonSerializer.Deserialize<List<ScanProfile>>(File.ReadAllText(FilePath)) ?? [];
                _items.RemoveAll(item => item == null);
                if (_items.Any(item => item.LocationId != Locations.Current.Id)) throw new InvalidDataException("Profiles belong to a different location.");
                foreach (var item in _items)
                {
                    item.Name ??= string.Empty;
                    item.Target ??= string.Empty;
                    item.Options ??= new ScanOptions();
                    item.InterfaceId ??= "";
                    item.SourceAddress ??= "";
                }
            }
            catch { _items = []; _readable = false; }
        }

        public static void Save(ScanProfile profile)
        {
            if (profile.LocationId != Locations.Current.Id) throw new InvalidOperationException("A profile belongs to a different location.");
            _items.RemoveAll(item => string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
            _items.Add(profile);
            Persist();
        }

        public static void Delete(ScanProfile profile)
        {
            if (profile.LocationId != Locations.Current.Id) return;
            _items.Remove(profile);
            Persist();
        }

        public static void Update() => Persist();

        private static void Persist()
        {
            if (DemoData.Enabled || !_readable) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                LocationCatalog.WriteAtomic(FilePath, JsonSerializer.Serialize(_items.Where(item => item.LocationId == Locations.Current.Id), JsonOptions));
            }
            catch { }
        }
    }
}
