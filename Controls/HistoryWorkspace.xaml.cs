using System.Collections.ObjectModel;
using System.Windows.Controls;
using KillerScan.Services;

namespace KillerScan.Controls
{
    /// <summary>
    /// The changes recorded against one scan history entry. The list of entries lives in the
    /// window's history sidebar, which drives this pane through <see cref="ShowEntry"/>.
    /// </summary>
    public partial class HistoryWorkspace : UserControl
    {
        private readonly ObservableCollection<HistoryChangeRow> _changes = [];
        private ScanHistoryEntry? _entry;
        private string Loc(string key) => TryFindResource(key) as string ?? key;

        public HistoryWorkspace()
        {
            InitializeComponent();
            HistoryChangesGrid.ItemsSource = _changes;
        }

        internal void ShowEntry(ScanHistoryEntry? entry)
        {
            _entry = entry;
            RefreshLocale();
        }

        public void RefreshLocale()
        {
            _changes.Clear();
            if (_entry == null)
            {
                HistorySummary.Text = Loc("Str_History_Empty");
                return;
            }
            var comparison = ScanHistory.Compare(_entry);
            foreach (var device in comparison.Added)
                _changes.Add(HistoryChangeRow.From(Loc("Str_History_Added"), device));
            foreach (var device in comparison.Removed)
                _changes.Add(HistoryChangeRow.From(Loc("Str_History_Removed"), device));
            foreach (var device in comparison.Changed)
                _changes.Add(HistoryChangeRow.From(Loc("Str_History_Changed"), device));
            HistorySummary.Text = comparison.Previous == null
                ? Loc("Str_History_FirstScan")
                : string.Format(Loc("Str_History_Summary"), comparison.Added.Count,
                    comparison.Removed.Count, comparison.Changed.Count);
        }

        private sealed class HistoryChangeRow
        {
            public string Change { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string IpAddress { get; init; } = string.Empty;
            public string DeviceType { get; init; } = string.Empty;

            public static HistoryChangeRow From(string change, HistoricalDevice device) => new()
            {
                Change = change,
                Name = string.IsNullOrWhiteSpace(device.Hostname) ? device.MacAddress : device.Hostname,
                IpAddress = device.IpAddress,
                DeviceType = DeviceTypeConverter.Display(device.DeviceType)
            };
        }
    }
}
