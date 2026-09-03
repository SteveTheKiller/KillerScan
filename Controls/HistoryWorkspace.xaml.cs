using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class HistoryWorkspace : UserControl
    {
        private readonly ObservableCollection<HistoryChangeRow> _changes = [];
        private string Loc(string key) => TryFindResource(key) as string ?? key;

        public HistoryWorkspace()
        {
            InitializeComponent();
            HistoryChangesGrid.ItemsSource = _changes;
            Loaded += (_, _) => RefreshHistory();
        }

        public void RefreshHistory()
        {
            var selected = HistoryList.SelectedItem as ScanHistoryEntry;
            var entries = ScanHistory.Entries.Reverse().ToList();
            HistoryList.ItemsSource = entries;
            HistoryList.SelectedItem = selected != null && entries.Contains(selected) ? selected : entries.FirstOrDefault();
            RefreshLocale();
        }

        public void RefreshLocale()
        {
            _changes.Clear();
            if (HistoryList.SelectedItem is not ScanHistoryEntry entry)
            {
                HistorySummary.Text = Loc("Str_History_Empty");
                return;
            }
            var comparison = ScanHistory.Compare(entry);
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

        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshLocale();

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
