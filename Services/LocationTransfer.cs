using System.IO;
using System.Text;
using System.Text.Json;

namespace KillerScan.Services
{
    /// <summary>
    /// Moves a Location to another PC or teammate as one .ksloc file: its name, customer label,
    /// notes and saved files. The file is data only. Importing never runs, opens or scans
    /// anything, and always creates a new Location rather than merging into one.
    /// </summary>
    internal static class LocationTransfer
    {
        internal const string Extension = ".ksloc";
        private const string Format = "KillerScan.Location";
        private const int Version = 1;
        private const string Logins = "logins.json";

        /// <summary>
        /// The Location as a .ksloc file. SSH usernames are included only when asked; passwords
        /// are never stored, so they never travel.
        /// </summary>
        internal static byte[] Export(LocationInfo info, string directory, bool includeUsernames)
        {
            var files = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in LocationData.StoreFiles)
            {
                if (name == Logins && !includeUsernames) continue;
                string path = Path.Combine(directory, name);
                if (!File.Exists(path)) continue;
                using var json = JsonDocument.Parse(File.ReadAllText(path));
                files[name] = json.RootElement.Clone();
            }
            var file = new TransferFile
            {
                Format = Format, Version = Version,
                Name = info.Name, Customer = info.Customer, Notes = info.Notes, Icon = info.Icon, Files = files
            };
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// Adds the file's Location to the catalog as a new Location. Everything is checked and
        /// staged before the catalog changes, so a refused file adds nothing and writes nothing
        /// outside its own staging folder.
        /// </summary>
        internal static bool Import(LocationCatalog catalog, byte[] bytes, out LocationInfo? imported, out string? error)
        {
            imported = null;
            using var prepared = Prepare(catalog, bytes, out error);
            return prepared != null && Commit(catalog, prepared, out imported, out error);
        }

        /// <summary>A checked Location file staged on disk, not yet in the catalog. Disposing it
        /// removes whatever is still staged.</summary>
        internal sealed class Prepared : IDisposable
        {
            internal string Staging { get; init; } = string.Empty;
            internal string Name { get; init; } = string.Empty;
            internal string Customer { get; init; } = string.Empty;
            internal string Notes { get; init; } = string.Empty;
            internal string? Icon { get; init; }

            public void Dispose()
            {
                if (Directory.Exists(Staging))
                    try { Directory.Delete(Staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// Reads, checks and stages a Location file without touching the catalog, so it can run off
        /// the UI thread. Null, with <paramref name="error"/>, when the file is refused.
        /// </summary>
        internal static Prepared? Prepare(LocationCatalog catalog, byte[] bytes, out string? error)
        {
            TransferFile? file;
            try { file = JsonSerializer.Deserialize<TransferFile>(bytes); }
            catch (JsonException) { file = null; }
            if (file == null || file.Format != Format) { error = "This is not a KillerScan Location file."; return null; }
            if (file.Version > Version) { error = "This Location file is from a newer version of KillerScan."; return null; }
            if (file.Version != Version || string.IsNullOrWhiteSpace(file.Name)) { error = "This Location file is damaged."; return null; }
            var files = file.Files ?? [];
            if (files.Keys.Any(name => !LocationData.StoreFiles.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                error = "This Location file contains something other than Location data.";
                return null;
            }
            // Every saved file is a JSON object or list. Anything else, such as null, is refused
            // here rather than becoming a Location that cannot be opened.
            if (files.Values.Any(value => value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
            {
                error = "This Location file is damaged.";
                return null;
            }

            var prepared = new Prepared
            {
                Staging = Path.Combine(catalog.LocationsDirectory, ".import-" + Guid.NewGuid().ToString("N")),
                Name = file.Name!, Customer = file.Customer ?? string.Empty, Notes = file.Notes ?? string.Empty, Icon = file.Icon
            };
            try
            {
                Directory.CreateDirectory(prepared.Staging);
                foreach (var pair in files)
                {
                    byte[] content = Encoding.UTF8.GetBytes(pair.Value.GetRawText());
                    JsonPreferenceFile<object>.Publish(Path.Combine(prepared.Staging, pair.Key.ToLowerInvariant()), content);
                }
                // Open the staged copy the way the app will. A file the stores cannot read is
                // refused before the catalog changes, so no unusable Location is ever listed.
                string? unreadable;
                try { unreadable = LocationData.Open(new LocationInfo { Id = "import", Name = prepared.Name }, prepared.Staging).ReadError; }
                catch (Exception ex) { unreadable = ex.Message; }
                if (unreadable != null) { error = "This Location file is damaged. " + unreadable; prepared.Dispose(); return null; }
                error = null;
                return prepared;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                prepared.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Moves a prepared Location's files into place, then lists it in one save, so it is
        /// listed complete or not at all, whatever happens in between. A process that dies between
        /// the two leaves an unlisted folder and the .ksloc file as it was, so the import just
        /// runs again. Runs on the UI thread, which every other reader of the catalog uses.
        /// </summary>
        internal static bool Commit(LocationCatalog catalog, Prepared prepared, out LocationInfo? imported, out string? error)
        {
            imported = null;
            try
            {
                var entry = LocationCatalog.NewEntry(prepared.Name, prepared.Customer, prepared.Notes,
                    LocationIcons.IsKnown(prepared.Icon) ? prepared.Icon! : string.Empty);
                string folder = catalog.DirectoryFor(entry);
                MoveDirectory(prepared.Staging, folder);
                if (!catalog.Publish(entry))
                {
                    error = catalog.SaveError ?? catalog.LoadError ?? "The Location could not be added.";
                    // Back to staging, which disposing clears. If it cannot go, nothing lists it.
                    try { MoveDirectory(folder, prepared.Staging); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                    return false;
                }
                imported = catalog.Items.FirstOrDefault(item => item.Id == entry.Id) ?? entry;
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
            finally { prepared.Dispose(); }
        }

        /// <summary>Moves the staged folder into place. Tests replace it to make the move fail.</summary>
        internal static Action<string, string> MoveDirectory { get; set; } = Directory.Move;

        private sealed class TransferFile
        {
            public string? Format { get; set; }
            public int Version { get; set; }
            public string? Name { get; set; }
            public string? Customer { get; set; }
            public string? Notes { get; set; }
            public string? Icon { get; set; }
            public Dictionary<string, JsonElement>? Files { get; set; }
        }
    }
}
