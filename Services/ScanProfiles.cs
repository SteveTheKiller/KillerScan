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

        /// <summary>
        /// The adapter this scan was set up on, by its Windows name. A preference only: it is
        /// checked against the route again on every run, and a mismatch asks before scanning.
        /// </summary>
        public string Adapter { get; set; } = string.Empty;

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
                    item.Adapter ??= string.Empty;
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
                Options = profile.Options ?? new ScanOptions(), Adapter = profile.Adapter ?? string.Empty
            };
            return _store.Change(items =>
            {
                items.RemoveAll(item => string.Equals(item.Name, saved.Name, StringComparison.OrdinalIgnoreCase));
                items.Add(saved);
            }, MemoryOnly);
        }

        /// <summary>
        /// The Save profile dialog edits the target and options, and the adapter comes from the
        /// tab's current choice (empty is Automatic), so it is required. Saving over an existing
        /// name (any case) keeps only what the dialog does not show, such as Deep Scan.
        /// </summary>
        public bool SaveEdited(string name, string target, ScanOptions options, string adapter) =>
            _store.Change(items =>
            {
                var existing = items.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                items.RemoveAll(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
                items.Add(new ScanProfile
                {
                    Name = name, Target = target, Options = options ?? new ScanOptions(),
                    DeepScanAfter = existing?.DeepScanAfter ?? false,
                    Adapter = adapter ?? string.Empty
                });
            }, MemoryOnly);

        /// <summary>
        /// Renames one saved scan in place, keeping everything else it holds (Deep Scan included).
        /// A name only differing in case is a plain rename. A name another saved scan already uses
        /// is refused rather than replacing that scan; <paramref name="taken"/> says so. Worked out
        /// over the latest file, so a name another window just took counts, and a failed save
        /// changes nothing here: repeat the call to retry. False with no error and not taken means
        /// the scan is gone from the file.
        /// </summary>
        public bool Rename(ScanProfile profile, string name, out bool taken)
        {
            // Read once, because the change runs over more than one copy of the list and the
            // object passed in may be among them.
            string original = profile.Name;
            bool collision = false, found = false;
            static bool Named(ScanProfile item, string text) => string.Equals(item.Name, text, StringComparison.OrdinalIgnoreCase);
            void Apply(List<ScanProfile> items)
            {
                var match = items.FirstOrDefault(item => Named(item, original));
                found = match != null;
                collision = items.Any(item => !Named(item, original) && Named(item, name));
                if (match != null && !collision) match.Name = name;
            }
            bool saved = MemoryOnly ? _store.Change(Apply, memoryOnly: true) : _store.Commit(Apply);
            taken = collision;
            return saved && found && !collision;
        }

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
        public static bool SaveEdited(string name, string target, ScanOptions options, string adapter = "") => Store.SaveEdited(name, target, options, adapter);
        public static bool Delete(ScanProfile profile) => Store.Delete(profile);
        public static bool Update(ScanProfile profile) => Store.Update(profile);
        public static bool Retry() => Store.Retry();
    }
}
