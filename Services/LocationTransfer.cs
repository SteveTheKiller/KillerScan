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
            TransferFile? file;
            try { file = JsonSerializer.Deserialize<TransferFile>(bytes); }
            catch (JsonException) { file = null; }
            if (file == null || file.Format != Format) { error = "This is not a KillerScan Location file."; return false; }
            if (file.Version > Version) { error = "This Location file is from a newer version of KillerScan."; return false; }
            if (file.Version != Version || string.IsNullOrWhiteSpace(file.Name)) { error = "This Location file is damaged."; return false; }
            var files = file.Files ?? [];
            if (files.Keys.Any(name => !LocationData.StoreFiles.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                error = "This Location file contains something other than Location data.";
                return false;
            }
            // Every saved file is a JSON object or list. Anything else, such as null, is refused
            // here rather than becoming a Location that cannot be opened.
            if (files.Values.Any(value => value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)))
            {
                error = "This Location file is damaged.";
                return false;
            }

            string staging = Path.Combine(catalog.LocationsDirectory, ".import-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                foreach (var pair in files)
                {
                    byte[] content = Encoding.UTF8.GetBytes(pair.Value.GetRawText());
                    JsonPreferenceFile<object>.Publish(Path.Combine(staging, pair.Key.ToLowerInvariant()), content);
                }
                // Open the staged copy the way the app will. A file the stores cannot read is
                // refused before the catalog changes, so no unusable Location is ever listed.
                string? unreadable;
                try { unreadable = LocationData.Open(new LocationInfo { Id = "import", Name = file.Name! }, staging).ReadError; }
                catch (Exception ex) { unreadable = ex.Message; }
                if (unreadable != null) { error = "This Location file is damaged. " + unreadable; return false; }
                if (!catalog.Create(file.Name!, file.Customer ?? string.Empty, file.Notes ?? string.Empty, out var created) || created == null)
                {
                    error = catalog.SaveError ?? catalog.LoadError ?? "The Location could not be added.";
                    return false;
                }
                if (LocationIcons.IsKnown(file.Icon)) catalog.SetIcon(created.Id, file.Icon!);
                try { MoveDirectory(staging, catalog.DirectoryFor(created)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Its files never arrived, so the entry comes off the list again.
                    catalog.Delete(created.Id, out _);
                    throw;
                }
                imported = catalog.Items.FirstOrDefault(item => item.Id == created.Id) ?? created;
                error = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (Directory.Exists(staging))
                    try { Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
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
