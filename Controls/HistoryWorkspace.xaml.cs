using System.Collections.ObjectModel;
using System.Windows;
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
        private readonly ObservableCollection<HistoryDeviceRow> _devices = [];
        private ScanHistoryEntry? _entry;
        private string Loc(string key) => TryFindResource(key) as string ?? key;

        /// <summary>
        /// Which reading of the selected entry is in front. Remembered between runs, because a
        /// comparison and a snapshot answer different questions and people stay with one of them.
        /// </summary>
        private bool _showAll = App.GetSetting("HistoryShowAll") == "1";

        public HistoryWorkspace()
        {
            InitializeComponent();
            HistoryChangesGrid.ItemsSource = _changes;
            HistoryAllGrid.ItemsSource = _devices;
            ApplyView();
        }

        internal void ShowEntry(ScanHistoryEntry? entry)
        {
            _entry = entry;
            RefreshLocale();
        }

        private void HistoryChangesView_Click(object sender, RoutedEventArgs e) => SetView(false);

        private void HistoryAllView_Click(object sender, RoutedEventArgs e) => SetView(true);

        private void SetView(bool showAll)
        {
            if (_showAll == showAll) return;
            _showAll = showAll;
            App.SetSetting("HistoryShowAll", showAll ? "1" : "0");
            ApplyView();
            RefreshLocale();
        }

        private void ApplyView()
        {
            HistoryChangesGrid.Visibility = _showAll ? Visibility.Collapsed : Visibility.Visible;
            HistoryAllGrid.Visibility = _showAll ? Visibility.Visible : Visibility.Collapsed;
            // The active side is marked by foreground alone, as in the shortcuts overlay.
            HistoryChangesViewButton.IsEnabled = _showAll;
            HistoryAllViewButton.IsEnabled = !_showAll;
        }

        public void RefreshLocale()
        {
            _changes.Clear();
            _devices.Clear();
            if (_entry == null)
            {
                HistorySummary.Text = Loc("Str_History_Empty");
                return;
            }
            if (_showAll)
            {
                foreach (var device in _entry.Devices)
                    _devices.Add(HistoryDeviceRow.From(device));
                HistorySummary.Text = string.Format(Loc("Str_History_AllSummary"), _entry.Devices.Count);
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

        private sealed class HistoryDeviceRow
        {
            public string IpAddress { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string MacAddress { get; init; } = string.Empty;
            public string Vendor { get; init; } = string.Empty;
            public string DeviceType { get; init; } = string.Empty;
            public string Ports { get; init; } = string.Empty;

            public static HistoryDeviceRow From(HistoricalDevice device) => new()
            {
                IpAddress = device.IpAddress,
                Name = device.Hostname,
                MacAddress = device.MacAddress,
                Vendor = device.Vendor,
                DeviceType = DeviceTypeConverter.Display(device.DeviceType),
                // A dash rather than an empty cell: nothing open is a finding, not missing data.
                Ports = device.OpenPorts.Count == 0 ? "-" : string.Join(", ", device.OpenPorts)
            };
        }
    }
}
