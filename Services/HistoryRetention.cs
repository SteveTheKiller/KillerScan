namespace KillerScan.Services
{
    internal enum HistoryRetentionMode { Count, Time, SaveAll }

    internal sealed record HistoryRetention(HistoryRetentionMode Mode, int Count = 100, int Days = 30)
    {
        internal bool IsValid => Enum.IsDefined(typeof(HistoryRetentionMode), Mode) &&
            Count > 0 && Days is >= 1 and <= 36500;

        internal List<ScanHistoryEntry> Retained(IEnumerable<ScanHistoryEntry> entries, DateTimeOffset now)
        {
            if (!IsValid) throw new ArgumentOutOfRangeException(nameof(HistoryRetention));
            var source = entries.ToList();
            if (Mode == HistoryRetentionMode.SaveAll) return source;
            if (Mode == HistoryRetentionMode.Time)
            {
                long cutoff = Math.Max(0, now.UtcDateTime.Ticks - (long)Days * TimeSpan.TicksPerDay);
                return [.. source.Where(entry => entry.ScannedAt.UtcDateTime.Ticks >= cutoff)];
            }
            var latest = new HashSet<ScanHistoryEntry>(source.OrderByDescending(entry => entry.ScannedAt).Take(Count));
            return [.. source.Where(latest.Contains)];
        }

        /// <summary>
        /// The entries with these runs added, or null when this policy would drop any of them at
        /// once. A run already among the entries is not added a second time.
        /// </summary>
        internal List<ScanHistoryEntry>? Adopting(IReadOnlyList<ScanHistoryEntry> entries, IReadOnlyList<ScanHistoryEntry> runs, DateTimeOffset now)
        {
            var kept = Retained(entries.Concat(runs.Where(run => !entries.Any(run.IsSameRun))), now);
            return runs.All(run => kept.Any(run.IsSameRun)) ? kept : null;
        }
    }
}
