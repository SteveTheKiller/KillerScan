using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private readonly ObservableCollection<HistoryChangeRow> _historyChanges = [];

        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryOverlay.Visibility == Visibility.Visible)
            {
                HideHistory();
                return;
            }
            if (ShortcutsOverlay.Visibility == Visibility.Visible) ShortcutsOverlay.Visibility = Visibility.Collapsed;
            if (AboutOverlay.Visibility == Visibility.Visible) AboutOverlay.Visibility = Visibility.Collapsed;
            HistoryChangesGrid.ItemsSource = _historyChanges;
            HistoryList.ItemsSource = ScanHistory.Entries.Reverse().ToList();
            HistoryList.SelectedIndex = HistoryList.Items.Count > 0 ? 0 : -1;
            if (HistoryList.Items.Count == 0)
            {
                _historyChanges.Clear();
                HistorySummary.Text = Loc("Str_History_Empty");
            }
            FadeOverlayIn(HistoryOverlay);
        }

        private void HideHistory() => FadeOverlayOut(HistoryOverlay);
        private void HistoryOverlay_Click(object sender, MouseButtonEventArgs e) => HideHistory();
        private void HistoryCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void HistoryClose_Click(object sender, RoutedEventArgs e) => HideHistory();

        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (HistoryList.SelectedItem is not ScanHistoryEntry entry) return;
            var comparison = ScanHistory.Compare(entry);
            _historyChanges.Clear();
            foreach (var device in comparison.Added)
                _historyChanges.Add(HistoryChangeRow.From(Loc("Str_History_Added"), device));
            foreach (var device in comparison.Removed)
                _historyChanges.Add(HistoryChangeRow.From(Loc("Str_History_Removed"), device));
            foreach (var device in comparison.Changed)
                _historyChanges.Add(HistoryChangeRow.From(Loc("Str_History_Changed"), device));
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
                DeviceType = Controls.DeviceTypeConverter.Display(device.DeviceType)
            };
        }
    }
}
