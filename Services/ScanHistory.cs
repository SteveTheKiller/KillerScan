using System.IO;
using KillerScan.Models;

namespace KillerScan.Services
{
    internal enum ScanCompletion { Complete, Partial, Canceled }

    internal sealed class ScanHistoryEntry
    {
        public DateTimeOffset ScannedAt { get; set; }
        public string Target { get; set; } = string.Empty;
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
            Equals(Options, current.Options) && Deep == current.Deep;

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

    internal static class ScanHistory
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "history.json");
        private static HistoryArchive _archive = new(FilePath);
        private static List<ScanHistoryEntry> _entries = [];
        private static HistoryRetention _demoRetention = new(HistoryRetentionMode.Count);

        public static IReadOnlyList<ScanHistoryEntry> Entries => _entries;
        public static string? SaveError => _archive.SaveError;

        /// <summary>Why the history file could not be read, or null when it loaded (or does not exist yet).</summary>
        public static string? LoadError => DemoData.Enabled ? null : _archive.LoadError;
        internal static string HistoryPath => FilePath;

        public static bool Retry()
        {
            if (DemoData.Enabled) return true;
            bool saved = _archive.Retry(DateTimeOffset.Now);
            _entries = _archive.Entries;
            return saved;
        }

        internal static HistoryRetention Retention => DemoData.Enabled ? _demoRetention : _archive.Policy;

        internal static bool ApplyRetention(HistoryRetention policy, Func<int, bool> confirmRemoval)
        {
            if (DemoData.Enabled)
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

        public static ScanComparison Compare(ScanHistoryEntry entry)
        {
            int index = _entries.IndexOf(entry);
            ScanHistoryEntry? previous = index > 0
                ? _entries.Take(index).LastOrDefault(candidate => candidate.IsBaselineFor(entry))
                : null;
            return Compare(entry, previous);
        }

        public static void Load()
        {
            // Demo mode never reads or writes the real history. It is a screenshot build running
            // on somebody's actual machine, so the fabricated network must not touch their file.
            if (DemoData.Enabled) { _entries = []; _demoRetention = new(HistoryRetentionMode.Count); return; }
            _archive = new(FilePath);
            _archive.Load(DateTimeOffset.Now);
            _entries = _archive.Entries;
        }

        /// <summary>Demo mode only: seeds fabricated history without going near the real file.</summary>
        internal static void SeedDemo(IEnumerable<ScanHistoryEntry> entries)
        {
            if (!DemoData.Enabled) return;
            _entries = [.. entries];
            _demoRetention = new(HistoryRetentionMode.SaveAll);
        }

        public static ScanComparison Record(string target, IEnumerable<NetworkDevice> devices, ScanCompletion completion = ScanCompletion.Complete) =>
            Record(target, target, devices, completion, new ScanOptions(), false);

        /// <summary>
        /// Records one run. <paramref name="caption"/> is what the list shows; <paramref name="targets"/>
        /// is the full target text, and with <paramref name="options"/> and <paramref name="deep"/>
        /// decides which earlier runs are fair baselines.
        /// </summary>
        public static ScanComparison Record(string caption, string targets, IEnumerable<NetworkDevice> devices,
            ScanCompletion completion, ScanOptions options, bool deep)
        {
            var current = new ScanHistoryEntry
            {
                ScannedAt = DateTimeOffset.Now,
                Target = caption,
                TargetKey = ScanHistoryEntry.KeyFor(targets),
                Options = options,
                Deep = deep,
                Completion = completion,
                Devices = [.. devices.Select(ToHistorical).OrderBy(device => device.IpAddress)]
            };
            var previous = _entries.LastOrDefault(entry => entry.IsBaselineFor(current));
            var comparison = Compare(current, previous);
            if (DemoData.Enabled) _entries = _demoRetention.Retained(_entries.Concat([current]), current.ScannedAt);
            else
            {
                comparison = comparison with { Saved = _archive.Append(current, current.ScannedAt) };
                _entries = _archive.Entries;
            }
            return comparison;
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
