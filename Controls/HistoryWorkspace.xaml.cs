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
            ConfigureActions();
        }

        internal void ShowEntry(ScanHistoryEntry? entry)
        {
            _entry = entry;
            RefreshLocale();
        }

        private void HistoryChangesView_Click(object sender, RoutedEventArgs e) => SetView(false);

        internal FrameworkElement DetachToolbar()
        {
            ((Panel)HistoryToolbar.Parent).Children.Remove(HistoryToolbar);
            return HistoryToolbar;
        }

        internal FrameworkElement DetachSummary()
        {
            ((Panel)HistoryDetails.Parent).Children.Remove(HistoryDetails);
            return HistoryDetails;
        }

        internal void UseTableHeaderStyle(Style style)
        {
            HistoryChangesGrid.ColumnHeaderStyle = style;
            HistoryAllGrid.ColumnHeaderStyle = style;
        }

        private void HistoryAllView_Click(object sender, RoutedEventArgs e) => SetView(true);

        private void SetView(bool showAll)
        {
            if (_showAll == showAll) return;
            _showAll = showAll;
            if (!DemoData.Enabled) App.SetSetting("HistoryShowAll", showAll ? "1" : "0");
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
            string[] changes = ["Str_History_Change", "Str_Col_DeviceName", "Str_Col_Ip", "Str_Col_Type"];
            string[] devices = ["Str_Col_Ip", "Str_Col_DeviceName", "Str_Col_Mac", "Str_Col_Vendor", "Str_Col_Type", "Str_Col_Ports"];
            for (int i = 0; i < changes.Length; i++) HistoryChangesGrid.Columns[i].Header = Loc(changes[i]);
            for (int i = 0; i < devices.Length; i++) HistoryAllGrid.Columns[i].Header = Loc(devices[i]);
            // Match the sidebar's saved timestamp, including archives recorded in another zone.
            HistoryEntryContext.Text = _entry == null ? string.Empty : $"{_entry.Target} · {_entry.ScannedAt:g}";
            var comparison = _entry == null ? null : ScanHistory.Compare(_entry);
            HistoryComparisonContext.Text = comparison?.Previous == null ? string.Empty :
                string.Format(Loc("Str_History_ComparedWith"), comparison.Previous.ScannedAt.ToString("g"));
            bool comparing = !_showAll && comparison?.Previous != null;
            HistoryTarget.Text = _entry?.Target ?? Loc("Str_History_Empty");
            HistoryDateSeparator.Text = _entry == null ? string.Empty : " · ";
            HistoryPreviousTimestamp.Text = comparing ? comparison!.Previous!.ScannedAt.ToString("g") : string.Empty;
            HistoryComparisonSeparator.Text = comparing ? " / " : string.Empty;
            HistoryCurrentTimestamp.Text = _entry?.ScannedAt.ToString("g") ?? string.Empty;
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
            foreach (var device in comparison!.Added)
                _changes.Add(HistoryChangeRow.From(Loc("Str_History_Added"), device));
            foreach (var device in comparison.Removed)
                _changes.Add(HistoryChangeRow.From(Loc("Str_History_Removed"), device));
            foreach (var device in comparison.Changed)
                _changes.Add(HistoryChangeRow.From(Loc("Str_History_Changed"), device));
            HistorySummary.Text = comparison.Previous == null
                ? Loc("Str_History_FirstScan")
                : string.Format(Loc("Str_History_Summary"), comparison!.Added.Count,
                    comparison.Removed.Count, comparison.Changed.Count);
        }

        private sealed class HistoryChangeRow
        {
            public string Change { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public HistoricalDevice Device { get; init; } = new();
            public string IpAddress { get; init; } = string.Empty;
            public string DeviceType { get; init; } = string.Empty;

            public static HistoryChangeRow From(string change, HistoricalDevice device) => new()
            {
                Device = device,
                Change = change,
                Name = string.IsNullOrWhiteSpace(device.Hostname) ? device.MacAddress : device.Hostname,
                IpAddress = device.IpAddress,
                DeviceType = DeviceTypeConverter.Display(device.DeviceType)
            };
        }

        private sealed class HistoryDeviceRow
        {
            public HistoricalDevice Device { get; init; } = new();
            public string IpAddress { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public string MacAddress { get; init; } = string.Empty;
            public string Vendor { get; init; } = string.Empty;
            public string DeviceType { get; init; } = string.Empty;
            public string Ports { get; init; } = string.Empty;

            public static HistoryDeviceRow From(HistoricalDevice device) => new()
            {
                Device = device,
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
