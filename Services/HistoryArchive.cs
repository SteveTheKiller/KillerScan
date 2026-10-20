using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace KillerScan.Services
{
    // The archive owns persistence and retention together, so a canceled or failed settings
    // change cannot commit a policy separately from the history it would remove.
    internal sealed class HistoryArchive
    {
        private readonly string _path;
        private bool _readable = true;
        private string? _revision;
        private readonly List<ScanHistoryEntry> _pending = [];
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        internal List<ScanHistoryEntry> Entries { get; private set; } = [];
        internal HistoryRetention Policy { get; private set; } = new(HistoryRetentionMode.Count);

        internal HistoryArchive(string path) => _path = Path.GetFullPath(path);

        internal void Load(DateTimeOffset now)
        {
            try
            {
                Locked(() =>
                {
                    ReadCurrent();
                    if (_readable && Policy.Mode == HistoryRetentionMode.Time)
                    {
                        var kept = Policy.Retained(Entries, now);
                        if (kept.Count != Entries.Count) { Persist(kept, Policy); Entries = kept; }
                    }
                    return true;
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void ReadCurrent()
        {
            _readable = true;
            _revision = null;
            Entries = [];
            Policy = new(HistoryRetentionMode.Count);
            if (!File.Exists(_path)) { Entries.AddRange(_pending); return; }
            try
            {
                _revision = File.ReadAllText(_path);
                using var json = JsonDocument.Parse(_revision);
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
            Entries.AddRange(_pending);
        }

        internal bool Apply(HistoryRetention policy, DateTimeOffset now, Func<int, bool> confirmRemoval)
        {
            if (!policy.IsValid) throw new ArgumentOutOfRangeException(nameof(policy));
            while (true)
            {
                string? revision = Locked(() => { ReadCurrent(); EnsureReadable(); return _revision; });
                var original = Entries;
                var kept = policy.Retained(original, now);
                int removed = original.Count - kept.Count;
                // Never hold the process lock while a confirmation pumps the UI dispatcher.
                if (removed > 0 && !confirmRemoval(removed)) return false;
                if (Locked(() =>
                {
                    bool changedInSession = !ReferenceEquals(original, Entries);
                    ReadCurrent(); EnsureReadable();
                    if (changedInSession || revision != _revision) return false;
                    Persist(kept, policy);
                    Entries = kept;
                    Policy = policy;
                    _pending.Clear();
                    return true;
                })) return true;
            }
        }

        internal void Append(ScanHistoryEntry entry, DateTimeOffset now)
        {
            _pending.Add(entry);
            try
            {
                Locked(() =>
                {
                    ReadCurrent(); EnsureReadable();
                    var kept = Policy.Retained(Entries, now);
                    Persist(kept, Policy);
                    Entries = kept;
                    _pending.Clear();
                    return true;
                });
            }
            catch (IOException) { Entries = Policy.Retained(Entries.Concat(_pending).Distinct(), now); }
            catch (UnauthorizedAccessException) { }
        }

        private void EnsureReadable()
        {
            if (!_readable) throw new IOException("The existing scan history file could not be read.");
        }

        private T Locked<T>(Func<T> operation)
        {
            using var hash = SHA256.Create();
            string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(_path.ToUpperInvariant()))).Replace("-", "");
            using var mutex = new Mutex(false, "Global\\KillerScan.History." + key);
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new IOException("The scan history file is busy. Try again.");
                return operation();
            }
            finally { if (owned) mutex.ReleaseMutex(); }
        }

        private void Persist(List<ScanHistoryEntry> entries, HistoryRetention policy)
        {
            EnsureReadable();
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
