using System.IO;
using System.Text.Json;

namespace KillerScan.Services
{
    internal sealed record ScanLocation(string Id, string Name);

    internal sealed class LocationNameException(string message, string resourceKey) : ArgumentException(message)
    {
        internal string ResourceKey { get; } = resourceKey;
    }

    /// <summary>Explicit identities, never inferred from a subnet, adapter or SSID.</summary>
    internal sealed class LocationCatalog
    {
        internal const string LegacyId = "legacy";
        internal static readonly string[] LegacyFiles = ["history.json", "devices.json", "overrides.json", "logins.json", "profiles.json"];
        private readonly string _root;
        private readonly bool _memoryOnly;
        private List<ScanLocation> _items = [];
        internal IReadOnlyList<ScanLocation> Items => _items;
        internal ScanLocation Current { get; private set; } = new(LegacyId, "Legacy data");
        private string Manifest => Path.Combine(_root, "locations.json");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private sealed class ManifestData
        {
            public ManifestData() { }
            public int Version { get; set; } = 1;
            public string SelectedId { get; set; } = LegacyId;
            public List<ScanLocation> Locations { get; set; } = [];
        }

        internal LocationCatalog(string root, bool memoryOnly = false)
        {
            _root = Path.GetFullPath(root);
            _memoryOnly = memoryOnly;
            if (memoryOnly) { _items = [Current]; return; }
            Directory.CreateDirectory(_root);
            using var lease = Lock();
            if (File.Exists(Manifest)) Read();
            else
            {
                string locations = Path.Combine(_root, "locations");
                if (Directory.Exists(locations) && Directory.GetDirectories(locations).Any(path => Path.GetFileName(path) != LegacyId))
                    throw new InvalidDataException("The location catalog is missing but location data exists. Restore the catalog backup before continuing.");
                MigrateLegacy();
                _items = [Current];
                Save(_items, Current.Id);
            }
        }

        private FileStream Lock() => new(Path.Combine(_root, "locations.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        internal static bool ValidId(string? id) => id == LegacyId || (id != null && Guid.TryParseExact(id, "N", out _));
        internal bool Contains(string id) => _items.Any(item => item.Id == id);
        internal string DataFile(string name)
        {
            if (!LegacyFiles.Contains(name)) throw new ArgumentException("Unknown location data file.", nameof(name));
            return Path.Combine(_root, "locations", Current.Id, name);
        }

        private void Read()
        {
            var data = JsonSerializer.Deserialize<ManifestData>(File.ReadAllText(Manifest));
            if (data?.Version != 1 || data.Locations == null || data.Locations.Count == 0 ||
                data.Locations.Any(item => item == null || !ValidId(item.Id) || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 80 || item.Name.Any(char.IsControl)) ||
                data.Locations.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != data.Locations.Count ||
                !data.Locations.Any(item => item.Id == LegacyId) || !data.Locations.Any(item => item.Id == data.SelectedId))
                throw new InvalidDataException("The location catalog is invalid. Restore its backup before continuing.");
            _items = data.Locations;
            Current = _items.Single(item => item.Id == data.SelectedId);
        }

        private void Save(List<ScanLocation> items, string selected)
        {
            if (!_memoryOnly)
                WriteAtomic(Manifest, JsonSerializer.Serialize(new ManifestData { Locations = items, SelectedId = selected }, JsonOptions));
            _items = items;
            Current = items.Single(item => item.Id == selected);
        }

        private void RefreshKeepingSelection()
        {
            var selected = Current;
            Read();
            Current = _items.FirstOrDefault(item => item.Id == selected.Id) ?? selected;
            if (!Contains(selected.Id)) throw new InvalidDataException("The active location was removed from the catalog. Restore the catalog before continuing.");
        }

        internal ScanLocation Create(string name)
        {
            name = CheckName(name);
            using var lease = _memoryOnly ? null : Lock();
            string selected = Current.Id;
            if (!_memoryOnly) RefreshKeepingSelection();
            if (_items.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new LocationNameException("A location with this name already exists.", "Str_Location_DuplicateName");
            var location = new ScanLocation(Guid.NewGuid().ToString("N"), name);
            Save([.. _items, location], selected);
            return location;
        }

        internal void Rename(string id, string name)
        {
            name = CheckName(name);
            using var lease = _memoryOnly ? null : Lock();
            string selected = Current.Id;
            if (!_memoryOnly) RefreshKeepingSelection();
            if (!Contains(id) || id == LegacyId) throw new ArgumentException("This location cannot be renamed.");
            if (_items.Any(item => item.Id != id && string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new LocationNameException("A location with this name already exists.", "Str_Location_DuplicateName");
            Save(_items.Select(item => item.Id == id ? item with { Name = name } : item).ToList(), selected);
        }

        internal void Select(string id, bool remember = true)
        {
            if (!ValidId(id) || !Contains(id)) throw new ArgumentException("Unknown location. Select an existing location explicitly.");
            if (!remember) { Current = _items.Single(item => item.Id == id); return; }
            using var lease = _memoryOnly ? null : Lock();
            if (!_memoryOnly) RefreshKeepingSelection();
            if (!Contains(id)) throw new ArgumentException("Unknown location.");
            Save(_items, id);
        }

        private static string CheckName(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 80 || name.Any(char.IsControl))
                throw new LocationNameException("Use a location name of 1 to 80 characters without control characters.", "Str_Location_InvalidName");
            return name;
        }

        private void MigrateLegacy()
        {
            // Copy, never move or overwrite the originals. Publish the catalog only after all
            // copies succeeded. An interrupted migration can be retried without discarding data.
            var files = LegacyFiles.Where(name => File.Exists(Path.Combine(_root, name))).ToArray();
            var original = files.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(_root, name)));
            foreach (string name in files)
                using (JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(original[name]).TrimStart('\uFEFF'))) { }
            string destination = Path.Combine(_root, "locations", LegacyId);
            if (LegacyFiles.Any(name => !original.ContainsKey(name) && File.Exists(Path.Combine(destination, name))))
                throw new IOException("An unfinished migration contains data absent from the originals. Keep both copies and recover them before continuing.");
            foreach (string name in files)
            {
                string existing = Path.Combine(destination, name);
                if (File.Exists(existing) && !File.ReadAllBytes(existing).SequenceEqual(original[name]))
                    throw new IOException("An unfinished location migration contains different data. Keep both copies and recover them before continuing.");
            }
            if (files.Length > 0)
            {
                string backup = Path.Combine(_root, "backups", "before-locations-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backup);
                foreach (string name in files) File.WriteAllBytes(Path.Combine(backup, name), original[name]);
                File.WriteAllText(Path.Combine(backup, "RESTORE.txt"),
                    "Close KillerScan. These are the original files before location migration. " +
                    "The original root files were also left intact. Keep locations/ before restoring; it contains any later changes. " +
                    "Use the previous application with the root files to roll back. No location was inferred from network addresses.");
            }
            Directory.CreateDirectory(destination);
            foreach (string name in files)
                if (!File.Exists(Path.Combine(destination, name))) File.WriteAllBytes(Path.Combine(destination, name), original[name]);
            if (files.Any(name => !original[name].SequenceEqual(File.ReadAllBytes(Path.Combine(_root, name)))) ||
                LegacyFiles.Any(name => !original.ContainsKey(name) && File.Exists(Path.Combine(_root, name))))
                throw new IOException("Legacy data changed during migration. Close other KillerScan copies and recover the preserved files before continuing.");
        }

        internal static void WriteAtomic(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string pending = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(pending, content);
                if (File.Exists(path)) File.Replace(pending, path, path + ".bak");
                else File.Move(pending, path);
            }
            finally { if (File.Exists(pending)) File.Delete(pending); }
        }
    }

    internal static class Locations
    {
        // Keep CLI diagnostics stable, but show localized guidance in GUI error dialogs.
        internal static string ErrorText(Exception error)
        {
            string key = error is LocationNameException name ? name.ResourceKey
                : error is ArgumentException ? "Str_Location_SelectionError" : "Str_Location_DataError";
            return System.Windows.Application.Current?.TryFindResource(key) as string ?? error.Message;
        }

        private static readonly object Gate = new();
        private static LocationCatalog? _catalog;
        private static int _runs;
        internal static string DataRoot => Environment.GetEnvironmentVariable("KILLERSCAN_DATA_ROOT") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerScan");
        internal static LocationCatalog Catalog
        {
            get { lock (Gate) return _catalog ??= new LocationCatalog(DataRoot, DemoData.Enabled); }
        }
        internal static ScanLocation Current => Catalog.Current;
        internal static bool IsBusy { get { lock (Gate) return _runs > 0; } }
        internal static string DataFile(string name) => Catalog.DataFile(name);
        internal static void LoadData()
        {
            DeviceOverrides.Load(); DeviceLogins.Load(); DevicePreferences.Load(); ScanHistory.Load(); ScanProfiles.Load();
            if (!DeviceOverrides.IsReadable || !DeviceLogins.IsReadable || !DevicePreferences.IsReadable || !ScanHistory.IsReadable || !ScanProfiles.IsReadable)
                throw new IOException("Saved location data could not be read or belongs to a different location. The existing files were preserved. Restore their backup before continuing.");
        }
        internal static bool TrySelect(string id, bool remember = true)
        {
            lock (Gate)
            {
                if (_runs != 0) return false;
                string previous = Current.Id;
                try { Catalog.Select(id, remember); LoadData(); }
                catch { Catalog.Select(previous, remember); LoadData(); throw; }
                return true;
            }
        }
        internal static IDisposable BeginRun()
        {
            lock (Gate) { _ = Current; _runs++; return new RunLease(); }
        }
        private sealed class RunLease : IDisposable
        {
            private bool _disposed;
            public void Dispose() { lock (Gate) { if (!_disposed) { _disposed = true; _runs--; } } }
        }
    }
}
