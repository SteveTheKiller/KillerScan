using System.IO;
using System.Text.Json;
using KillerScan.Models;

namespace KillerScan.Services
{
    internal sealed class ScanHistoryEntry
    {
        public DateTimeOffset ScannedAt { get; set; }
        public string Target { get; set; } = string.Empty;
        public List<HistoricalDevice> Devices { get; set; } = [];
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
        IReadOnlyList<HistoricalDevice> Changed);

    internal static class ScanHistory
    {
        private const int MaximumEntries = 50;
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "history.json");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private static List<ScanHistoryEntry> _entries = [];

        public static IReadOnlyList<ScanHistoryEntry> Entries => _entries;

        public static ScanComparison Compare(ScanHistoryEntry entry)
        {
            int index = _entries.IndexOf(entry);
            ScanHistoryEntry? previous = index > 0
                ? _entries.Take(index).LastOrDefault(candidate =>
                    string.Equals(candidate.Target, entry.Target, StringComparison.OrdinalIgnoreCase))
                : null;
            return Compare(entry, previous);
        }

        public static void Load()
        {
            // Demo mode never reads or writes the real history. It is a screenshot build running
            // on somebody's actual machine, so the fabricated network must not touch their file.
            if (DemoData.Enabled) { _entries = []; return; }
            try
            {
                if (!File.Exists(FilePath)) return;
                _entries = JsonSerializer.Deserialize<List<ScanHistoryEntry>>(
                    File.ReadAllText(FilePath)) ?? [];
            }
            catch
            {
                _entries = [];
            }
        }

        /// <summary>Demo mode only: seeds fabricated history without going near the real file.</summary>
        internal static void SeedDemo(IEnumerable<ScanHistoryEntry> entries)
        {
            if (!DemoData.Enabled) return;
            _entries = [.. entries];
        }

        public static ScanComparison Record(string target, IEnumerable<NetworkDevice> devices)
        {
            var current = new ScanHistoryEntry
            {
                ScannedAt = DateTimeOffset.Now,
                Target = target,
                Devices = [.. devices.Select(ToHistorical).OrderBy(device => device.IpAddress)]
            };
            var previous = _entries.LastOrDefault(entry =>
                string.Equals(entry.Target, target, StringComparison.OrdinalIgnoreCase));
            var comparison = Compare(current, previous);
            _entries.Add(current);
            if (_entries.Count > MaximumEntries)
                _entries.RemoveRange(0, _entries.Count - MaximumEntries);
            Save();
            return comparison;
        }

        private static ScanComparison Compare(ScanHistoryEntry current, ScanHistoryEntry? previous)
        {
            if (previous == null)
                return new ScanComparison(current, null, current.Devices, [], []);

            var before = previous.Devices.ToDictionary(device => device.Identity, StringComparer.OrdinalIgnoreCase);
            var after = current.Devices.ToDictionary(device => device.Identity, StringComparer.OrdinalIgnoreCase);
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
            OpenPorts = [.. device.OpenPorts.OrderBy(port => port)]
        };

        private static void Save()
        {
            if (DemoData.Enabled) return;
            try
            {
                string directory = Path.GetDirectoryName(FilePath)!;
                Directory.CreateDirectory(directory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries, JsonOptions));
            }
            catch { }
        }
    }

    internal static class DeviceIdentity
    {
        public static string For(NetworkDevice device) =>
            !string.IsNullOrWhiteSpace(device.MacAddress)
                ? "mac:" + device.MacAddress.Replace("-", ":").ToUpperInvariant()
                : "ip:" + device.IpAddress;
    }
}
