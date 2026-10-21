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

        /// <summary>What a screen reader announces for the profile's row in the profiles list.</summary>
        public override string ToString() => Name + ", " + Target;
    }

    /// <summary>The saved scans of one Location (profiles.json).</summary>
    internal sealed class SavedScanStore
    {
        private readonly JsonPreferenceFile<List<ScanProfile>> _store;
        private readonly bool _memoryOnly;
        private List<ScanProfile> _items => _store.Value;
        public string? SaveError => _store.Error;

        // Demo mode keeps its profiles in memory: a screenshot build must not add fabricated
        // profiles to whatever the person running it has saved.
        private bool MemoryOnly => _memoryOnly || DemoData.Enabled;

        internal SavedScanStore(string path, bool memoryOnly = false)
        {
            _memoryOnly = memoryOnly;
            _store = new(path, () => [], items =>
            {
                items.RemoveAll(item => item == null);
                foreach (var item in items)
                {
                    item.Name ??= string.Empty;
                    item.Target ??= string.Empty;
                    item.Options ??= new ScanOptions();
                }
                return items;
            });
        }

        public IReadOnlyList<ScanProfile> Items => _items;

        public void Load()
        {
            if (DemoData.Enabled) { _items.AddRange(DemoData.Profiles()); return; }
            if (!MemoryOnly) _store.Load();
        }

        public bool Save(ScanProfile profile)
        {
            var saved = new ScanProfile
            {
                Name = profile.Name, Target = profile.Target, DeepScanAfter = profile.DeepScanAfter,
                Options = profile.Options ?? new ScanOptions()
            };
            return _store.Change(items =>
            {
                items.RemoveAll(item => string.Equals(item.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
                items.Add(saved);
            }, MemoryOnly);
        }

        /// <summary>
        /// The Save profile dialog edits only the target and options. Saving over an existing
        /// name (any case) keeps the fields the dialog does not show, such as Deep Scan.
        /// </summary>
        public bool SaveEdited(string name, string target, ScanOptions options) =>
            _store.Change(items =>
            {
                var existing = items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                items.RemoveAll(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                items.Add(new ScanProfile
                {
                    Name = name, Target = target, Options = options ?? new ScanOptions(),
                    DeepScanAfter = existing?.DeepScanAfter ?? false
                });
            }, MemoryOnly);

        public bool Delete(ScanProfile profile) => _store.Change(items =>
            items.RemoveAll(item => string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase)), MemoryOnly);

        public bool Update(ScanProfile profile) => Save(profile);
        public bool Retry() => MemoryOnly || _store.Save();
    }

    /// <summary>The app-wide saved scans, until every caller works through a Location.</summary>
    internal static class ScanProfiles
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "profiles.json");
        internal static SavedScanStore Store { get; private set; } = new(FilePath);

        public static string? SaveError => Store.SaveError;
        public static IReadOnlyList<ScanProfile> Items => Store.Items;
        public static void Load() { Store = new(FilePath); Store.Load(); }
        public static bool Save(ScanProfile profile) => Store.Save(profile);
        public static bool SaveEdited(string name, string target, ScanOptions options) => Store.SaveEdited(name, target, options);
        public static bool Delete(ScanProfile profile) => Store.Delete(profile);
        public static bool Update(ScanProfile profile) => Store.Update(profile);
        public static bool Retry() => Store.Retry();
    }
}
