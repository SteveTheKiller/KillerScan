using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private HistoryWorkspace? _historyWorkspace;

        /// <summary>
        /// F6 and the rail button open the history sidebar and show the selected snapshot.
        /// Pressing it again with the sidebar already open closes it, so the one control both
        /// reveals and dismisses the panel.
        /// </summary>
        private void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            ShortcutsOverlay.Visibility = Visibility.Collapsed;
            AboutOverlay.Visibility = Visibility.Collapsed;
            bool wasOpen = !_sidebarCollapsed && _sidebarSection == "history";
            ShowSidebarSection("history");
            if (!wasOpen) ShowHistoryEntry();
        }

        /// <summary>
        /// Set while the list is being repopulated. Swapping the workspace from inside the
        /// selection event that a repopulate raises re-enters the list's container generator
        /// mid-measure, which throws. The caller drives the pane once the list has settled.
        /// </summary>
        private bool _syncingHistory;

        private void RefreshHistoryList()
        {
            _syncingHistory = true;
            try
            {
                var selected = HistoryList.SelectedItem as ScanHistoryEntry;
                var entries = ScanHistory.Entries.Reverse().ToList();
                HistoryList.ItemsSource = entries;
                HistoryList.SelectedItem = selected != null && entries.Contains(selected)
                    ? selected : entries.FirstOrDefault();
            }
            finally { _syncingHistory = false; }
        }

        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingHistory) return;
            // Deferred for the same reason: let the list finish its own layout pass before the
            // workspace underneath it is replaced.
            Dispatcher.BeginInvoke(new Action(ShowHistoryEntry),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ShowHistoryEntry()
        {
            _historyWorkspace ??= new HistoryWorkspace { LayoutTransform = new ScaleTransform(_appScale, _appScale) };
            _historyWorkspace.ShowEntry(HistoryList.SelectedItem as ScanHistoryEntry);
            ShowWorkspaceContent(_historyWorkspace, "history");
        }
    }
}
