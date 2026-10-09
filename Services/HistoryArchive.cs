using System.IO;
using System.Text.Json;

namespace KillerScan.Services
{
    // The archive owns persistence and retention together, so a canceled or failed settings
    // change cannot commit a policy separately from the history it would remove.
    internal sealed class HistoryArchive
    {
        private readonly string _path;
        private bool _readable = true;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        internal List<ScanHistoryEntry> Entries { get; private set; } = [];
        internal HistoryRetention Policy { get; private set; } = new(HistoryRetentionMode.Count);

        internal HistoryArchive(string path) => _path = path;

        internal void Load(DateTimeOffset now)
        {
            _readable = true;
            Entries = [];
            Policy = new(HistoryRetentionMode.Count);
            if (!File.Exists(_path)) return;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllText(_path));
                if (json.RootElement.ValueKind == JsonValueKind.Array)
                {
                    Entries = JsonSerializer.Deserialize<List<ScanHistoryEntry>>(json.RootElement.GetRawText()) ?? [];
                    // Existing archives had no user-selected policy. Preserve them until the
                    // user chooses one; only new installations default to the latest 100.
                    Policy = new(HistoryRetentionMode.SaveAll);
                }
                else
                {
                    var saved = JsonSerializer.Deserialize<HistoryFile>(json.RootElement.GetRawText());
                    if (saved?.Version != 1 || saved.Entries == null || saved.Policy?.IsValid != true)
                        throw new InvalidDataException("Invalid scan history archive.");
                    Entries = saved.Entries;
                    Policy = saved.Policy;
                }
                Entries.RemoveAll(entry => entry == null);
                foreach (var entry in Entries)
                {
                    entry.Target ??= string.Empty;
                    entry.Devices ??= [];
                    entry.Devices.RemoveAll(device => device == null || string.IsNullOrWhiteSpace(device.Identity));
                    foreach (var device in entry.Devices) device.OpenPorts ??= [];
                }
            }
            catch
            {
                // Keep an unreadable file intact rather than overwriting it on the next scan.
                Entries = [];
                _readable = false;
                Policy = new(HistoryRetentionMode.SaveAll);
                return;
            }
            if (Policy.Mode == HistoryRetentionMode.Time)
            {
                var kept = Policy.Retained(Entries, now);
                if (kept.Count != Entries.Count)
                {
                    try { Persist(kept, Policy); Entries = kept; } catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        internal bool Apply(HistoryRetention policy, DateTimeOffset now, Func<int, bool> confirmRemoval)
        {
            var original = Entries;
            var kept = policy.Retained(original, now);
            int removed = original.Count - kept.Count;
            if (removed > 0 && !confirmRemoval(removed)) return false;
            // A scan can finish while a modal confirmation runs its dispatcher.
            if (!ReferenceEquals(original, Entries)) return Apply(policy, now, confirmRemoval);
            Persist(kept, policy);
            Entries = kept;
            Policy = policy;
            return true;
        }

        internal void Append(ScanHistoryEntry entry, DateTimeOffset now)
        {
            var kept = Policy.Retained(Entries.Concat([entry]), now);
            // A scan result still belongs in this session if its disk write fails.
            Entries = kept;
            try { Persist(kept, Policy); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void Persist(List<ScanHistoryEntry> entries, HistoryRetention policy)
        {
            if (!_readable) throw new IOException("The existing scan history file could not be read.");
            string directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new HistoryFile { Entries = entries, Policy = policy }, JsonOptions));
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private sealed class HistoryFile
        {
            public HistoryFile() { }
            public int Version { get; set; } = 1;
            public HistoryRetention? Policy { get; set; }
            public List<ScanHistoryEntry>? Entries { get; set; }
        }
    }
}
