using System.IO;
using KillerScan.Models;

namespace KillerScan.Services
{
    internal sealed class ScanHistoryEntry
    {
        public DateTimeOffset ScannedAt { get; set; }
        public string Target { get; set; } = string.Empty;
        public string LocationId { get; set; } = LocationCatalog.LegacyId;
        public string LocationName { get; set; } = "Legacy data";
        public List<HistoricalDevice> Devices { get; set; } = [];

        /// <summary>What a screen reader announces for the entry's row in the history list.</summary>
        public override string ToString() =>
            ScannedAt.ToString("g", System.Globalization.CultureInfo.CurrentCulture) + ", " + Target;
    }

    internal sealed class HistoricalDevice
    {
        public string Identity { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public string Hostname { get; set; } = string.Empty;
        public string MacAddress { get; set; } = string.Empty;
        public string Vendor { get; set; } = string.Empty;
        public string DeviceType { get; set; } = string.Empty;
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public DeviceIdentification? Identification { get; set; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public List<AccessEndpoint>? Access { get; set; }
        public List<int> OpenPorts { get; set; } = [];
    }

    internal sealed record ScanComparison(
        ScanHistoryEntry Current,
        ScanHistoryEntry? Previous,
        IReadOnlyList<HistoricalDevice> Added,
        IReadOnlyList<HistoricalDevice> Removed,
        IReadOnlyList<HistoricalDevice> Changed);

    internal static class ScanHistory
    {
        private static string FilePath => Locations.DataFile("history.json");
        private static HistoryArchive _archive = new(FilePath);
        internal static bool IsReadable => DemoData.Enabled || _archive.IsReadable;
        private static List<ScanHistoryEntry> _entries = [];
        private static HistoryRetention _demoRetention = new(HistoryRetentionMode.Count);

        public static IReadOnlyList<ScanHistoryEntry> Entries => _entries;

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
                ? _entries.Take(index).LastOrDefault(candidate =>
                    candidate.LocationId == entry.LocationId && string.Equals(candidate.Target, entry.Target, StringComparison.OrdinalIgnoreCase))
                : null;
            return Compare(entry, previous);
        }

        public static void Load()
        {
            // Demo mode never reads or writes the real history. It is a screenshot build running
            // on somebody's actual machine, so the fabricated network must not touch their file.
            if (DemoData.Enabled) { _entries = []; _demoRetention = new(HistoryRetentionMode.Count); return; }
            _archive = new(FilePath);
            _archive.LoadForLocation(DateTimeOffset.Now, Locations.Current.Id);
            if (_archive.Entries.Any(entry => entry.LocationId != Locations.Current.Id))
                throw new InvalidDataException("History belongs to a different location. Keep the file and restore the correct location data.");
            _entries = _archive.Entries;
        }

        /// <summary>Demo mode only: seeds fabricated history without going near the real file.</summary>
        internal static void SeedDemo(IEnumerable<ScanHistoryEntry> entries)
        {
            if (!DemoData.Enabled) return;
            _entries = [.. entries];
            foreach (var entry in _entries) { entry.LocationId = Locations.Current.Id; entry.LocationName = Locations.Current.Name; }
            _demoRetention = new(HistoryRetentionMode.SaveAll);
        }

        public static ScanComparison Record(string target, IEnumerable<NetworkDevice> devices)
        {
            var snapshot = devices.ToList();
            if (snapshot.Any(device => device.LocationId != null && device.LocationId != Locations.Current.Id))
                throw new InvalidOperationException("Scan results belong to a different location.");
            var current = new ScanHistoryEntry
            {
                ScannedAt = DateTimeOffset.Now,
                Target = target,
                LocationId = Locations.Current.Id,
                LocationName = Locations.Current.Name,
                Devices = [.. snapshot.Select(ToHistorical).OrderBy(device => device.IpAddress)]
            };
            var previous = _entries.LastOrDefault(entry =>
                entry.LocationId == current.LocationId && string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase));
            var comparison = Compare(current, previous);
            if (DemoData.Enabled) _entries = _demoRetention.Retained(_entries.Concat([current]), current.ScannedAt);
            else
            {
                _archive.Append(current, current.ScannedAt);
                _entries = _archive.Entries;
            }
            return comparison;
        }

        private static ScanComparison Compare(ScanHistoryEntry current, ScanHistoryEntry? previous)
        {
            if (previous == null || previous.LocationId != current.LocationId)
                return new ScanComparison(current, null, current.Devices, [], []);

            var before = previous.Devices.GroupBy(device => device.Identity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var after = current.Devices.GroupBy(device => device.Identity, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var added = after.Where(pair => !before.ContainsKey(pair.Key)).Select(pair => pair.Value).ToList();
            var removed = before.Where(pair => !after.ContainsKey(pair.Key)).Select(pair => pair.Value).ToList();
            var changed = after.Where(pair => before.TryGetValue(pair.Key, out var old) && HasChanged(old, pair.Value))
                .Select(pair => pair.Value).ToList();
            return new ScanComparison(current, previous, added, removed, changed);
        }

        private static bool HasChanged(HistoricalDevice before, HistoricalDevice after) =>
            !string.Equals(before.IpAddress, after.IpAddress, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.Hostname, after.Hostname, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.Vendor, after.Vendor, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(before.DeviceType, after.DeviceType, StringComparison.OrdinalIgnoreCase) ||
            !before.OpenPorts.OrderBy(port => port).SequenceEqual(after.OpenPorts.OrderBy(port => port));

        private static HistoricalDevice ToHistorical(NetworkDevice device) => new()
        {
            Identity = DeviceIdentity.For(device),
            IpAddress = device.IpAddress,
            Hostname = device.Hostname,
            MacAddress = device.MacAddress,
            Vendor = device.Vendor,
            DeviceType = device.DeviceType,
            Identification = device.Identification?.Snapshot(),
            Access = device.Access == null ? null : [.. device.Access],
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
