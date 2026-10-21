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

    internal static class ScanProfiles
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "profiles.json");
        private static JsonPreferenceFile<List<ScanProfile>> _store = CreateStore();
        private static List<ScanProfile> _items => _store.Value;
        public static string? SaveError => _store.Error;
        private static JsonPreferenceFile<List<ScanProfile>> CreateStore() => new(FilePath, () => [], items =>
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

        public static IReadOnlyList<ScanProfile> Items => _items;

        public static void Load()
        {
            // Demo mode keeps its profiles in memory: a screenshot build must not add fabricated
            // profiles to whatever the person running it has saved.
            _store = CreateStore();
            if (DemoData.Enabled) { _items.AddRange(DemoData.Profiles()); return; }
            _store.Load();
        }

        public static bool Save(ScanProfile profile)
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
            }, DemoData.Enabled);
        }

        /// <summary>
        /// The Save profile dialog edits only the target and options. Saving over an existing
        /// name (any case) keeps the fields the dialog does not show, such as Deep Scan.
        /// </summary>
        public static bool SaveEdited(string name, string target, ScanOptions options) =>
            _store.Change(items =>
            {
                var existing = items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                items.RemoveAll(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                items.Add(new ScanProfile
                {
                    Name = name, Target = target, Options = options ?? new ScanOptions(),
                    DeepScanAfter = existing?.DeepScanAfter ?? false
                });
            }, DemoData.Enabled);

        public static bool Delete(ScanProfile profile) => _store.Change(items =>
            items.RemoveAll(item => string.Equals(item.Name, profile.Name, StringComparison.OrdinalIgnoreCase)), DemoData.Enabled);

        public static bool Update(ScanProfile profile) => Save(profile);
        public static bool Retry() => DemoData.Enabled || _store.Save();
    }
}
