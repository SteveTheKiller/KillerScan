using System.IO;

namespace KillerScan.Services
{
    /// <summary>
    /// A customer site or named network workspace. The ID never changes, so renaming moves no
    /// data; everything the Location owns lives in locations\&lt;id&gt;\.
    /// </summary>
    internal sealed class LocationInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Customer { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;

        /// <summary>The targets and settings of the last scan run here, restored when it is picked.</summary>
        public string LastTargets { get; set; } = string.Empty;
        public ScanOptions? LastOptions { get; set; }

        /// <summary>
        /// Gateway MAC addresses seen by completed scans here, normalized. They only ever suggest
        /// this Location; addresses like 192.168.1.x are reused too widely to suggest anything.
        /// </summary>
        public List<string> GatewayMacs { get; set; } = [];

        internal static string NormalizeMac(string? mac) => (mac ?? string.Empty).Trim().Replace("-", ":").ToUpperInvariant();

        /// <summary>What a screen reader announces for the Location's row.</summary>
        public override string ToString() => Customer.Length == 0 ? Name : Name + ", " + Customer;
    }

    /// <summary>
    /// The five stores one Location owns. A Temporary scan gets a set that lives only in memory,
    /// so it reads no saved names or approvals and writes nothing until it is assigned.
    /// </summary>
    internal sealed class LocationData
    {
        internal static readonly string[] StoreFiles = ["devices.json", "overrides.json", "logins.json", "profiles.json", "history.json"];

        private readonly Func<DevicePreferenceStore> _devices;
        private readonly Func<DeviceOverrideStore> _overrides;
        private readonly Func<DeviceLoginStore> _logins;
        private readonly Func<SavedScanStore> _savedScans;
        private readonly Func<HistoryStore> _history;

        public LocationInfo? Info { get; }
        public bool IsTemporary => Info == null && !IsShared;

        /// <summary>The app-wide stores from before Locations. See <see cref="Shared"/>.</summary>
        public bool IsShared { get; }
        public DevicePreferenceStore Devices => _devices();
        public DeviceOverrideStore Overrides => _overrides();
        public DeviceLoginStore Logins => _logins();
        public SavedScanStore SavedScans => _savedScans();
        public HistoryStore History => _history();

        private LocationData(LocationInfo? info, string directory, bool memoryOnly)
        {
            Info = info;
            var devices = new DevicePreferenceStore(Path.Combine(directory, "devices.json"), memoryOnly);
            var overrides = new DeviceOverrideStore(Path.Combine(directory, "overrides.json"), memoryOnly);
            var logins = new DeviceLoginStore(Path.Combine(directory, "logins.json"), memoryOnly);
            var savedScans = new SavedScanStore(Path.Combine(directory, "profiles.json"), memoryOnly);
            var history = new HistoryStore(Path.Combine(directory, "history.json"), memoryOnly);
            _devices = () => devices; _overrides = () => overrides; _logins = () => logins;
            _savedScans = () => savedScans; _history = () => history;
        }

        private LocationData(LocationInfo? info)
        {
            Info = info;
            IsShared = true;
            _devices = () => DevicePreferences.Store; _overrides = () => DeviceOverrides.Store;
            _logins = () => DeviceLogins.Store; _savedScans = () => ScanProfiles.Store; _history = () => ScanHistory.Store;
        }

        /// <summary>
        /// The app-wide stores, read through each time so a reload is always seen. Used when the
        /// Location catalog is unavailable (the original files stay in use) and by hosts that
        /// never load Locations, such as isolated tests.
        /// </summary>
        internal static LocationData Shared(LocationInfo? info = null) => new(info);

        internal static LocationData Open(LocationInfo info, string directory)
        {
            var data = new LocationData(info, directory, memoryOnly: false);
            data.Load();
            return data;
        }

        internal static LocationData Temporary()
        {
            // The path is never written: every store is memory-only.
            var data = new LocationData(null, Path.Combine(Path.GetTempPath(), "KillerScan-temporary"), memoryOnly: true);
            data.Load();
            return data;
        }

        /// <summary>The first store that could not be read when this Location was opened, if any.</summary>
        internal string? ReadError => Devices.SaveError ?? Overrides.SaveError ?? Logins.SaveError ?? SavedScans.SaveError ?? History.LoadError;

        private void Load()
        {
            Devices.Load();
            Overrides.Load();
            Logins.Load();
            SavedScans.Load();
            History.Load();
        }
    }

    /// <summary>
    /// The list of Locations (locations.json). Its first load on an existing install moves the
    /// app-wide stores into a Location named Unassigned.
    /// </summary>
    internal sealed class LocationCatalog
    {
        internal const string UnassignedId = "unassigned";
        private readonly string _root;
        private readonly string _path;
        private JsonPreferenceFile<CatalogFile> _store;

        internal LocationCatalog(string root)
        {
            _root = Path.GetFullPath(root);
            _path = Path.Combine(_root, "locations.json");
            _store = CreateStore();
        }

        public IReadOnlyList<LocationInfo> Items => LoadError == null ? _store.Value.Locations : [];

        /// <summary>Why the catalog could not be read or migrated. While set, nothing is changed.</summary>
        public string? LoadError { get; private set; }
        public string? SaveError => _store.Error;

        private JsonPreferenceFile<CatalogFile> CreateStore() => new(_path, () => new(), value =>
        {
            if (value.Version != 1) throw new InvalidDataException("Unsupported Locations catalog version.");
            value.Locations ??= [];
            value.Locations.RemoveAll(item => item == null || string.IsNullOrWhiteSpace(item.Id));
            foreach (var item in value.Locations) item.GatewayMacs ??= [];
            return value;
        });

        public void Load(string unassignedName)
        {
            LoadError = null;
            _store = CreateStore();
            if (File.Exists(_path))
            {
                _store.Load();
                LoadError = _store.Error;
                return;
            }
            try { MigrateOnce(unassignedName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The app keeps using the original files; the next start tries again.
                LoadError = ex.Message;
            }
        }

        // Two windows starting together would otherwise both migrate, and the second could
        // delete the folder the first had just published. One named mutex per catalog path
        // serializes them, and whoever waits finds the catalog already there and just loads it.
        private void MigrateOnce(string unassignedName)
        {
            using var hash = System.Security.Cryptography.SHA256.Create();
            string key = BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(_path.ToUpperInvariant()))).Replace("-", "");
            using var mutex = new System.Threading.Mutex(false, "Global\\KillerScan.Migrate." + key);
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
                catch (System.Threading.AbandonedMutexException) { owned = true; }
                if (!owned) throw new IOException("Another KillerScan window is still moving your data into Locations. Restart KillerScan to try again.");
                if (File.Exists(_path))
                {
                    _store.Load();
                    LoadError = _store.Error;
                    return;
                }
                Migrate(unassignedName);
            }
            finally { if (owned) mutex.ReleaseMutex(); }
        }

        // Copy into a staging folder, verify every byte, move it into place, then publish the
        // catalog last. Any step can fail or be interrupted: until the catalog exists, the next
        // start repeats the whole copy from the untouched originals.
        private void Migrate(string unassignedName)
        {
            var existing = LocationData.StoreFiles.Where(name => File.Exists(Path.Combine(_root, name))).ToList();
            if (existing.Count > 0)
            {
                string locations = Path.Combine(_root, "locations");
                string staging = Path.Combine(locations, ".migrating");
                string target = Path.Combine(locations, UnassignedId);
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
                Directory.CreateDirectory(staging);
                foreach (string name in existing)
                {
                    string source = Path.Combine(_root, name), copy = Path.Combine(staging, name);
                    File.Copy(source, copy);
                    if (!File.ReadAllBytes(source).SequenceEqual(File.ReadAllBytes(copy)))
                        throw new IOException("The copy of " + name + " did not match the original.");
                }
                // A folder already here is left by a migration interrupted after the move but
                // before the catalog. It is set aside, never deleted, in case anything wrote to it.
                if (Directory.Exists(target))
                {
                    string deleted = Path.Combine(locations, ".deleted");
                    Directory.CreateDirectory(deleted);
                    Directory.Move(target, Path.Combine(deleted, UnassignedId + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")));
                }
                Directory.Move(staging, target);
            }
            if (!_store.Commit(value =>
            {
                if (existing.Count > 0 && !value.Locations.Any(item => item.Id == UnassignedId))
                    value.Locations.Add(new LocationInfo { Id = UnassignedId, Name = unassignedName });
            })) throw new IOException(_store.Error ?? "The Locations catalog could not be saved.");
        }

        /// <summary>True once migration has published the catalog, even if it cannot be read now.</summary>
        internal bool Published => File.Exists(_path);

        internal string LocationsDirectory => Path.Combine(_root, "locations");
        internal string DirectoryFor(LocationInfo info) => Path.Combine(LocationsDirectory, info.Id);

        public LocationData Open(LocationInfo info) => LocationData.Open(info, DirectoryFor(info));

        public bool Create(string name, out LocationInfo? created) => Create(name, string.Empty, string.Empty, out created);

        /// <summary>Adds a Location with its customer label and notes in one save, so it is never listed half-made.</summary>
        public bool Create(string name, string customer, string notes, out LocationInfo? created)
        {
            created = null;
            if (LoadError != null) return false;
            var info = new LocationInfo { Id = Guid.NewGuid().ToString("N"), Name = name.Trim(), Customer = customer.Trim(), Notes = notes };
            if (!_store.Commit(value => value.Locations.Add(info))) return false;
            created = _store.Value.Locations.FirstOrDefault(item => item.Id == info.Id) ?? info;
            return true;
        }

        /// <summary>
        /// Removes a Location from the list and moves its folder to locations\.deleted, so a
        /// mistaken delete can be recovered by hand. <paramref name="kept"/> is where it went.
        /// </summary>
        public bool Delete(string id, out string? kept)
        {
            kept = null;
            if (LoadError != null || Items.FirstOrDefault(item => item.Id == id) is not { } info) return false;
            if (!_store.Commit(value => value.Locations.RemoveAll(item => item.Id == id))) return false;
            string folder = DirectoryFor(info);
            if (!Directory.Exists(folder)) return true;
            string deleted = Path.Combine(LocationsDirectory, ".deleted");
            Directory.CreateDirectory(deleted);
            kept = Path.Combine(deleted, id + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            try { Directory.Move(folder, kept); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The Location is already off the list; its files simply stay where they were.
                kept = folder;
            }
            return true;
        }

        public bool Rename(string id, string name) => Update(id, item => item.Name = name.Trim());
        public bool SetCustomer(string id, string customer) => Update(id, item => item.Customer = customer.Trim());
        public bool SetNotes(string id, string notes) => Update(id, item => item.Notes = notes);
        public bool AddGatewayMac(string id, string mac)
        {
            string normalized = LocationInfo.NormalizeMac(mac);
            if (normalized.Length == 0) return true;
            if (Items.FirstOrDefault(item => item.Id == id)?.GatewayMacs.Contains(normalized) == true) return true;
            return Update(id, item =>
            {
                if (!item.GatewayMacs.Contains(normalized)) item.GatewayMacs.Add(normalized);
            });
        }

        /// <summary>The Locations whose completed scans have seen this gateway MAC.</summary>
        internal static IReadOnlyList<LocationInfo> Matching(IEnumerable<LocationInfo> items, string gatewayMac)
        {
            string normalized = LocationInfo.NormalizeMac(gatewayMac);
            return normalized.Length == 0 ? [] : items.Where(item => item.GatewayMacs.Contains(normalized)).ToList();
        }

        public bool SetLastUsed(string id, string targets, ScanOptions options) =>
            Update(id, item => { item.LastTargets = targets; item.LastOptions = options; });

        private bool Update(string id, Action<LocationInfo> change) =>
            LoadError == null && _store.Commit(value =>
            {
                if (value.Locations.FirstOrDefault(item => item.Id == id) is { } item) change(item);
            });

        private sealed class CatalogFile
        {
            public int Version { get; set; } = 1;
            public List<LocationInfo> Locations { get; set; } = [];
        }
    }
}
