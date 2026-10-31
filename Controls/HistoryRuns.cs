using KillerScan.Services;

namespace KillerScan.Controls
{
    /// <summary>
    /// History's right-click Rename and Delete. Each offers to repeat that same change if the write
    /// fails; the store's own Retry only saves runs that are waiting to be recorded.
    /// </summary>
    internal static class HistoryRuns
    {
        // A run another window deleted has no name left to save, so that alone asks nothing.
        internal static bool Rename(HistoryStore store, ScanHistoryEntry entry, string name) =>
            PersistenceFeedback.Attempt(() => store.Rename(entry, name) || store.SaveError == null, () => store.SaveError);

        internal static bool Delete(HistoryStore store, ScanHistoryEntry entry) =>
            PersistenceFeedback.Attempt(() => store.Remove(entry), () => store.SaveError);

        /// <summary>
        /// The row to keep selected after the list is rebuilt: the same run, matched by what it
        /// is, because a rename reads the entries afresh from the file. Delete acts on the
        /// selection without asking, so it must never slide to another run.
        /// </summary>
        internal static ScanHistoryEntry? Selection(List<ScanHistoryEntry> entries, ScanHistoryEntry? selected) =>
            (selected == null ? null : entries.FirstOrDefault(entry => ReferenceEquals(entry.Owner, selected.Owner) && entry.IsSameRun(selected)))
            ?? entries.FirstOrDefault();
    }
}
