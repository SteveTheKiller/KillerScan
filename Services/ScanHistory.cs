using System.IO;
using KillerScan.Models;

namespace KillerScan.Services
{
    internal enum ScanCompletion { Complete, Partial, Canceled }

    /// <summary>What became of runs handed to another history. Saved means every run is in its file.</summary>
    internal enum AdoptResult { Saved, Failed, NotRetained }

    internal sealed class ScanHistoryEntry
    {
        public DateTimeOffset ScannedAt { get; set; }
        public string Target { get; set; } = string.Empty;

        /// <summary>A name the user gave this run ("LAN", "Guest Wi-Fi"), or empty. Lets one
        /// Location keep several networks apart in its history.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>What the history list shows: the name, or the target when there is none.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string DisplayName => (Name ?? string.Empty).Trim() is { Length: > 0 } name ? name : Target ?? string.Empty;
        public List<HistoricalDevice> Devices { get; set; } = [];
        public ScanCompletion Completion { get; set; }

        /// <summary>
        /// Every scanned target, normalized and sorted. <see cref="Target"/> is only a caption
        /// ("10.0.0.1 +2"), so two different target lists can share it. Null in entries saved
        /// before this was recorded.
        /// </summary>
        public string? TargetKey { get; set; }

        /// <summary>The checks this run made. Null in entries saved before this was recorded.</summary>
        public ScanOptions? Options { get; set; }

        /// <summary>True for a deep run (every port 1 to 1024 and more). Null in older entries.</summary>
        public bool? Deep { get; set; }

        /// <summary>The adapter the run's traffic routed through. Empty in older entries and command-line runs.</summary>
        public string Adapter { get; set; } = string.Empty;

        /// <summary>The gateway of the network scanned (<see cref="ScanNetwork"/>). Empty when unknown or in older entries.</summary>
        public string GatewayMac { get; set; } = string.Empty;

        /// <summary>The adapter's stable ID (<see cref="ScanNetwork"/>). Empty when unknown or in older entries.</summary>
        public string AdapterId { get; set; } = string.Empty;

        /// <summary>The Location label shown when History lists every Location. Never saved.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string LocationName { get; set; } = string.Empty;

        /// <summary>The history this entry was listed from, which picks its baseline. Never saved.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        internal HistoryStore? Owner { get; set; }

        /// <summary>The same run, in any copy: entries are read fresh from the file, so objects never match.</summary>
        internal bool IsSameRun(ScanHistoryEntry other) => ScannedAt == other.ScannedAt && Target == other.Target;

        /// <summary>The normalized key for a target list, or null when it does not parse.</summary>
        internal static string? KeyFor(string targets)
        {
            var parsed = ScanTargets.Parse(targets);
            return parsed.Ok ? string.Join(",", parsed.Targets.OrderBy(t => t, StringComparer.OrdinalIgnoreCase)) : null;
        }

        /// <summary>
        /// The key this entry compares by. Older entries recorded only the caption, which is
        /// exact for a single target but lossy for "first +N", so those have no key at all.
        /// </summary>
        internal string? ComparisonKey =>
            TargetKey ?? (System.Text.RegularExpressions.Regex.IsMatch(Target, @" \+\d+$") ? null : KeyFor(Target));

        /// <summary>True when this run can serve as the baseline for <paramref name="current"/>.</summary>
        internal bool IsBaselineFor(ScanHistoryEntry current) =>
            Completion == ScanCompletion.Complete && ComparisonKey is { } key &&
            string.Equals(key, current.ComparisonKey, StringComparison.OrdinalIgnoreCase) &&
            Equals(Options, current.Options) && Deep == current.Deep && IsSameNetwork(current);

        /// <summary>
        /// True when the evidence puts two runs on one network: the same router, or failing that
        /// the same adapter. Runs from before this was recorded have none, so they compare only
        /// with each other, as they always did; a run with evidence never assumes one without.
        /// </summary>
        private bool IsSameNetwork(ScanHistoryEntry other)
        {
            if (GatewayMac.Length > 0 && other.GatewayMac.Length > 0)
                return string.Equals(GatewayMac, other.GatewayMac, StringComparison.OrdinalIgnoreCase);
            if (AdapterId.Length > 0 && other.AdapterId.Length > 0)
                return string.Equals(AdapterId, other.AdapterId, StringComparison.OrdinalIgnoreCase);
            return GatewayMac.Length + AdapterId.Length + other.GatewayMac.Length + other.AdapterId.Length == 0;
        }

        /// <summary>What a screen reader announces for the entry's row in the history list.</summary>
        public override string ToString()
        {
            string text = ScannedAt.ToString("g", System.Globalization.CultureInfo.CurrentCulture) + ", " + Target;
            if (Completion == ScanCompletion.Complete) return text;
            string key = Completion == ScanCompletion.Canceled ? "Str_St_ScanCanceled" : "Str_History_Partial";
            return text + ", " + (System.Windows.Application.Current?.TryFindResource(key) as string ?? Completion.ToString());
        }
    }

    internal sealed class HistoricalDevice
    {
        public string Identity { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Hostname { get; set; } = string.Empty;
        public string MacAddress { get; set; } = string.Empty;
        public string Vendor { get; set; } = string.Empty;
        public string DeviceType { get; set; } = string.Empty;
        public List<int> OpenPorts { get; set; } = [];
    }

    internal sealed record ScanComparison(
        ScanHistoryEntry Current,
        ScanHistoryEntry? Previous,
        IReadOnlyList<HistoricalDevice> Added,
        IReadOnlyList<HistoricalDevice> Removed,
        IReadOnlyList<HistoricalDevice> Changed,
        bool Saved = true);

    /// <summary>The app-wide history, until every caller works through a Location.</summary>
    internal static class ScanHistory
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "history.json");
        internal static HistoryStore Store { get; private set; } = new(FilePath);

        public static IReadOnlyList<ScanHistoryEntry> Entries => Store.Entries;
        public static string? SaveError => Store.SaveError;
        public static string? LoadError => Store.LoadError;
        internal static string HistoryPath => Store.HistoryPath;
        public static bool Retry() => Store.Retry();
        internal static HistoryRetention Retention => Store.Retention;
        internal static bool ApplyRetention(HistoryRetention policy, Func<int, bool> confirmRemoval) => Store.ApplyRetention(policy, confirmRemoval);
        public static ScanComparison Compare(ScanHistoryEntry entry) => Store.Compare(entry);
        public static void Load() { Store = new(FilePath); Store.Load(); }
        internal static void SeedDemo(IEnumerable<ScanHistoryEntry> entries) => Store.SeedDemo(entries);
        public static ScanComparison Record(string target, IEnumerable<NetworkDevice> devices, ScanCompletion completion = ScanCompletion.Complete) =>
            Store.Record(target, devices, completion);
        public static ScanComparison Record(string caption, string targets, IEnumerable<NetworkDevice> devices,
            ScanCompletion completion, ScanOptions options, bool deep) => Store.Record(caption, targets, devices, completion, options, deep);
    }

    /// <summary>The scan history of one Location (history.json) and how runs compare.</summary>
    internal sealed class HistoryStore
    {
        private readonly string _path;
        private readonly bool _memoryOnly;
        private HistoryArchive _archive;
        private List<ScanHistoryEntry> _entries = [];
        private HistoryRetention _demoRetention = new(HistoryRetentionMode.Count);

        // Demo mode never reads or writes the real history. It is a screenshot build running
        // on somebody's actual machine, so the fabricated network must not touch their file.
        private bool MemoryOnly => _memoryOnly || DemoData.Enabled;

        internal HistoryStore(string path, bool memoryOnly = false)
        {
            _path = path;
            _memoryOnly = memoryOnly;
            _archive = new(path);
        }

        public IReadOnlyList<ScanHistoryEntry> Entries => _entries;
        public string? SaveError => _archive.SaveError;

        /// <summary>Why the history file could not be read, or null when it loaded (or does not exist yet).</summary>
        public string? LoadError => MemoryOnly ? null : _archive.LoadError;
        internal string HistoryPath => _path;

        public bool Retry()
        {
            if (MemoryOnly) return true;
            bool saved = _archive.Retry(DateTimeOffset.Now);
            _entries = _archive.Entries;
            return saved;
        }

        internal HistoryRetention Retention => MemoryOnly ? _demoRetention : _archive.Policy;

        /// <summary>Names one run (empty clears it); the history sidebar's Rename scan.</summary>
        internal bool Rename(ScanHistoryEntry entry, string name)
        {
            if (MemoryOnly) { entry.Name = name; return true; }
            bool renamed = _archive.Rename(entry, name);
            _entries = _archive.Entries;
            return renamed;
        }

        /// <summary>Deletes one run from this history (the history sidebar's right-click Delete).</summary>
        internal bool Remove(ScanHistoryEntry entry)
        {
            if (MemoryOnly) return _entries.Remove(entry);
            bool removed = _archive.Remove(entry);
            _entries = _archive.Entries;
            return removed;
        }

        internal bool ApplyRetention(HistoryRetention policy, Func<int, bool> confirmRemoval)
        {
            if (MemoryOnly)
            {
                var original = _entries;
                var kept = policy.Retained(original, DateTimeOffset.Now);
                int removed = original.Count - kept.Count;
                if (removed > 0 && !confirmRemoval(removed)) return false;
                if (!ReferenceEquals(original, _entries)) return ApplyRetention(policy, confirmRemoval);
                _entries = kept;
                _demoRetention = policy;
                return true;
            }
            bool applied = _archive.Apply(policy, DateTimeOffset.Now, confirmRemoval);
            if (applied) _entries = _archive.Entries;
            return applied;
        }

        public ScanComparison Compare(ScanHistoryEntry entry) => Compare(entry, Baseline(entry));

        // The latest earlier run that is a fair baseline, by when it ran. Runs adopted from a
        // Temporary scan keep their own time but are added at the end, so list order is not time.
        private ScanHistoryEntry? Baseline(ScanHistoryEntry current) => _entries
            .Where(candidate => !ReferenceEquals(candidate, current) && candidate.ScannedAt < current.ScannedAt && candidate.IsBaselineFor(current))
            .OrderBy(candidate => candidate.ScannedAt)
            .LastOrDefault();

        /// <summary>
        /// Rereads the file, such as after another process recorded a run, and keeps runs this
        /// process could not save yet so a later retry still has them. Load starts over instead.
        /// </summary>
        public void Reload()
        {
            if (MemoryOnly) return;
            _archive.Load(DateTimeOffset.Now);
            _entries = _archive.Entries;
        }

        public void Load()
        {
            if (MemoryOnly) { _entries = []; _demoRetention = new(HistoryRetentionMode.Count); return; }
            _archive = new(_path);
            _archive.Load(DateTimeOffset.Now);
            _entries = _archive.Entries;
        }

        /// <summary>Memory-only stores: seeds history without going near a real file.</summary>
        internal void SeedDemo(IEnumerable<ScanHistoryEntry> entries)
        {
            if (!MemoryOnly) return;
            _entries = [.. entries];
            _demoRetention = new(HistoryRetentionMode.SaveAll);
        }

        public ScanComparison Record(string target, IEnumerable<NetworkDevice> devices, ScanCompletion completion = ScanCompletion.Complete) =>
            Record(target, target, devices, completion, new ScanOptions(), false);

        /// <summary>
        /// Records one run. <paramref name="caption"/> is what the list shows; <paramref name="targets"/>
        /// is the full target text, and with <paramref name="options"/> and <paramref name="deep"/>
        /// decides which earlier runs are fair baselines.
        /// </summary>
        public ScanComparison Record(string caption, string targets, IEnumerable<NetworkDevice> devices,
            ScanCompletion completion, ScanOptions options, bool deep, string adapter = "", ScanNetwork? network = null)
        {
            var current = new ScanHistoryEntry
            {
                ScannedAt = DateTimeOffset.Now,
                Adapter = adapter,
                GatewayMac = network?.GatewayMac ?? string.Empty,
                AdapterId = network?.AdapterId ?? string.Empty,
                Target = caption,
                TargetKey = ScanHistoryEntry.KeyFor(targets),
                Options = options,
                Deep = deep,
                Completion = completion,
                Devices = [.. devices.Select(ToHistorical).OrderBy(device => device.IpAddress)]
            };
            var comparison = Compare(current, Baseline(current));
            if (MemoryOnly) _entries = _demoRetention.Retained(_entries.Concat([current]), current.ScannedAt);
            else
            {
                comparison = comparison with { Saved = _archive.Append(current, current.ScannedAt) };
                _entries = _archive.Entries;
            }
            return comparison;
        }

        /// <summary>
        /// Adds runs recorded elsewhere, such as a scan saved to a Location, all of them or none.
        /// Saved means they are in this history's file, so the caller may let go of its own copy.
        /// </summary>
        public AdoptResult Adopt(IReadOnlyList<ScanHistoryEntry> runs)
        {
            var copies = runs.Select(entry => new ScanHistoryEntry
            {
                ScannedAt = entry.ScannedAt, Target = entry.Target, TargetKey = entry.TargetKey, Options = entry.Options,
                Deep = entry.Deep, Completion = entry.Completion, Adapter = entry.Adapter, GatewayMac = entry.GatewayMac,
                AdapterId = entry.AdapterId, Name = entry.Name, Devices = [.. entry.Devices]
            }).ToList();
            if (MemoryOnly)
            {
                if (_demoRetention.Adopting(_entries, copies, DateTimeOffset.Now) is not { } kept) return AdoptResult.NotRetained;
                _entries = kept;
                return AdoptResult.Saved;
            }
            var result = _archive.Adopt(copies, DateTimeOffset.Now);
            _entries = _archive.Entries;
            return result;
        }

        private static ScanComparison Compare(ScanHistoryEntry current, ScanHistoryEntry? previous)
        {
            if (previous == null)
                return new ScanComparison(current, null, current.Devices, [], []);

            var before = previous.Devices.GroupBy(device => device.Identity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var after = current.Devices.GroupBy(device => device.Identity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var added = after.Where(pair => !before.ContainsKey(pair.Key)).Select(pair => pair.Value).ToList();
            // A canceled or partial run did not check everything, so a missing device or port is
            // "not checked", never "removed". Only what it did see can count as new or changed.
            bool complete = current.Completion == ScanCompletion.Complete;
            var removed = complete
                ? before.Where(pair => !after.ContainsKey(pair.Key)).Select(pair => pair.Value).ToList()
                : [];
            var changed = after.Where(pair => before.TryGetValue(pair.Key, out var old) && HasChanged(old, pair.Value, complete))
                .Select(pair => pair.Value).ToList();
            return new ScanComparison(current, previous, added, removed, changed);
        }

        private static bool HasChanged(HistoricalDevice before, HistoricalDevice after, bool complete) =>
            !string.Equals(before.IpAddress, after.IpAddress, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.Hostname, after.Hostname, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.Vendor, after.Vendor, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.DeviceType, after.DeviceType, StringComparison.OrdinalIgnoreCase) ||
            (complete
                ? !before.OpenPorts.OrderBy(port => port).SequenceEqual(after.OpenPorts.OrderBy(port => port))
                : after.OpenPorts.Except(before.OpenPorts).Any());

        private static HistoricalDevice ToHistorical(NetworkDevice device) => new()
        {
            Identity = DeviceIdentity.For(device),
            IpAddress = device.IpAddress,
            Hostname = device.Hostname,
            MacAddress = device.MacAddress,
            Vendor = device.Vendor,
            DeviceType = device.DeviceType,
            OpenPorts = [.. device.OpenPorts.OrderBy(port => port)]
        };

    }

    internal static class DeviceIdentity
    {
        public static string For(NetworkDevice device) =>
            !string.IsNullOrWhiteSpace(device.MacAddress)
                ? "mac:" + device.MacAddress.Replace("-", ":").ToUpperInvariant()
                : "ip:" + device.IpAddress;
    }
}
