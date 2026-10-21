using System.IO;

namespace KillerScan.Services
{
    /// <summary>
    /// The app's Locations: the catalog, one shared data set per open Location (so two tabs on
    /// the same site see each other's changes), and the Location a new tab starts in.
    /// </summary>
    internal static class LocationService
    {
        /// <summary>Where Locations live. Isolated tests point it at a scratch folder before loading.</summary>
        internal static string Root { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerScan");
        private static LocationCatalog? _catalog;
        private static LocationData? _fallback;
        private static readonly Dictionary<string, LocationData> Open = new(StringComparer.Ordinal);

        /// <summary>False until <see cref="Load"/> runs. Hosts that never load keep the app-wide stores.</summary>
        public static bool Loaded { get; private set; }

        /// <summary>Why the catalog is unavailable. The original app-wide files stay in use meanwhile.</summary>
        public static string? LoadError => _catalog?.LoadError;
        public static string? SaveError => _catalog?.SaveError;

        /// <summary>Every Location, or just the original data as Unassigned when the catalog is unavailable.</summary>
        public static IReadOnlyList<LocationInfo> Items =>
            _fallback?.Info is { } fallback ? [fallback] : _catalog?.Items ?? [];

        public static event EventHandler? Changed;

        public static void Load(string unassignedName)
        {
            Open.Clear();
            _fallback = null;
            Loaded = true;
            if (DemoData.Enabled)
            {
                // Demo mode never creates or migrates real Locations.
                _catalog = null;
                return;
            }
            _catalog = new LocationCatalog(Root);
            _catalog.Load(unassignedName);
            if (_catalog.LoadError != null)
            {
                _fallback = LocationData.Shared(new LocationInfo { Id = LocationCatalog.UnassignedId, Name = unassignedName });
                DevicePreferences.Load(); DeviceOverrides.Load(); DeviceLogins.Load(); ScanProfiles.Load(); ScanHistory.Load();
            }
        }

        /// <summary>The data for a Location, shared by every tab that has it open.</summary>
        public static LocationData Get(LocationInfo info)
        {
            if (_fallback != null) return _fallback;
            if (!Loaded || _catalog == null) return LocationData.Shared(info);
            if (!Open.TryGetValue(info.Id, out var data))
                Open[info.Id] = data = _catalog.Open(info);
            return data;
        }

        /// <summary>
        /// Where a new tab starts: the last Location used, or a Temporary scan. Hosts that never
        /// loaded Locations get the app-wide stores, as before Locations existed.
        /// </summary>
        public static LocationData Initial()
        {
            if (!Loaded) return LocationData.Shared();
            if (DemoData.Enabled) return LocationData.Shared();
            string? last = App.GetSetting("LastLocation");
            var info = Items.FirstOrDefault(item => item.Id == last)
                ?? (last == null ? Items.FirstOrDefault(item => item.Id == LocationCatalog.UnassignedId) : null);
            return info == null ? LocationData.Temporary() : Get(info);
        }

        /// <summary>Remembers the Location picked most recently, for the next new tab.</summary>
        public static void Remember(LocationData data)
        {
            if (!Loaded || DemoData.Enabled) return;
            App.SetSetting("LastLocation", data.Info?.Id ?? string.Empty);
        }

        public static bool Create(string name, out LocationInfo? created)
        {
            created = null;
            if (_catalog == null || _fallback != null) return false;
            bool ok = _catalog.Create(name, out created);
            if (ok) Changed?.Invoke(null, EventArgs.Empty);
            return ok;
        }

        public static bool Delete(LocationInfo info, out string? kept)
        {
            kept = null;
            if (!Editable || !_catalog!.Delete(info.Id, out kept)) return false;
            Open.Remove(info.Id);
            Changed?.Invoke(null, EventArgs.Empty);
            return true;
        }

        public static bool Rename(LocationInfo info, string name) => Notify(_catalog != null && _fallback == null && _catalog.Rename(info.Id, name));
        public static bool SetNotes(LocationInfo info, string notes) => Notify(_catalog != null && _fallback == null && _catalog.SetNotes(info.Id, notes));

        /// <summary>Whether Locations can be created, renamed, exported and imported right now.</summary>
        public static bool Editable => Loaded && !DemoData.Enabled && _catalog != null && _fallback == null && _catalog.LoadError == null;

        public static byte[]? Export(LocationInfo info, bool includeUsernames) =>
            Editable ? LocationTransfer.Export(Find(info.Id) ?? info, _catalog!.DirectoryFor(info), includeUsernames) : null;

        public static bool Import(byte[] bytes, out LocationInfo? imported, out string? error)
        {
            imported = null;
            if (!Editable) { error = LoadError ?? "Locations are unavailable."; return false; }
            bool ok = LocationTransfer.Import(_catalog!, bytes, out imported, out error);
            if (ok) Changed?.Invoke(null, EventArgs.Empty);
            return ok;
        }

        /// <summary>Records a gateway MAC seen by a completed scan in this Location.</summary>
        public static void AddGatewayMac(LocationInfo info, string mac)
        {
            if (Editable) _catalog!.AddGatewayMac(info.Id, mac);
        }

        /// <summary>Remembers a Location's last targets and settings. Quiet: nothing on screen depends on it.</summary>
        public static void SetLastUsed(LocationInfo info, string targets, ScanOptions options)
        {
            if (_catalog != null && _fallback == null) _catalog.SetLastUsed(info.Id, targets, options);
        }

        private static bool Notify(bool changed)
        {
            if (changed) Changed?.Invoke(null, EventArgs.Empty);
            return changed;
        }

        /// <summary>The current catalog entry for an ID, after renames and reloads.</summary>
        public static LocationInfo? Find(string id) => Items.FirstOrDefault(item => item.Id == id);
    }
}
