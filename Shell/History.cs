using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private HistoryWorkspace? _historyWorkspace;

        private void HistorySettingsMenu_Opening(object sender, ContextMenuEventArgs e) =>
            FlyoutPlacement.Attach(HistoryButton.ContextMenu, HistoryButton);

        private bool HandleHistorySettingsShortcut(Key key, ModifierKeys modifiers)
        {
            if (!IsHistorySettingsShortcut(key, modifiers)) return false;
            HistorySettings_Click(this, new RoutedEventArgs());
            return true;
        }

        private static bool IsHistorySettingsShortcut(Key key, ModifierKeys modifiers) =>
            key == Key.R && modifiers == (ModifierKeys.Control | ModifierKeys.Alt);

        private void HistorySettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new HistorySettingsDialog(ScanHistory.Retention, ApplyHistoryRetention) { Owner = this };
            dialog.ShowDialog();
        }

        private bool ApplyHistoryRetention(HistoryRetention policy)
        {
            bool applied = ScanHistory.ApplyRetention(policy, removed =>
            {
                var confirmation = CreateHistoryRemovalConfirmation(removed,
                    OwnedWindows.OfType<HistorySettingsDialog>().FirstOrDefault() ?? (Window)this);
                confirmation.ShowDialog();
                return confirmation.Confirmed;
            });
            if (applied)
            {
                RefreshHistoryList();
                if (_workspaceView == "history") ShowHistoryEntry();
            }
            return applied;
        }

        private ConfirmDialog CreateHistoryRemovalConfirmation(int removed, Window? owner = null)
        {
            var dialog = new ConfirmDialog(Loc("Str_History_Settings"),
                string.Format(Loc("Str_History_RemoveConfirm"), removed),
                Loc("Str_History_Delete"), Loc("Str_Btn_Cancel"));
            if (owner != null) dialog.Owner = owner;
            var button = (Button)dialog.FindName("OkButton");
            button.Width = double.NaN;
            button.MinWidth = 80;
            return dialog;
        }

        /// <summary>
        /// Ctrl+H and the rail button open the history sidebar and show the selected snapshot.
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
            if (_historyWorkspace == null)
            {
                _historyWorkspace = new HistoryWorkspace { LayoutTransform = new ScaleTransform(_appScale, _appScale) };
                _historyWorkspace.UseTableHeaderStyle(((DataGrid)_scanWorkspace!.FindName("ResultsGrid")).ColumnHeaderStyle);
                RegisterViewToolbar("history", _historyWorkspace.DetachToolbar());
                _workspaceSummary.Children.Add(_historyWorkspace.DetachSummary());
                _workspaceSummary.LayoutTransform = new ScaleTransform(_appScale, _appScale);
                _historyWorkspace.CurrentScanRequested += () => ShowScanView("devices");
                _historyWorkspace.SidebarRequested += () => HistoryButton_Click(this, new RoutedEventArgs());
            }
            _historyWorkspace.ShowEntry(HistoryList.SelectedItem as ScanHistoryEntry);
            ShowWorkspaceContent(_historyWorkspace, "history");
        }
    }
}
