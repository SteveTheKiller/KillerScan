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

        private void HistorySettingsMenu_Opening(object sender, ContextMenuEventArgs e)
        {
            HistoryAllLocationsItem.IsChecked = _historyAllLocations;
            HistoryAllLocationsItem.IsEnabled = LocationService.Loaded && !DemoData.Enabled && LocationService.Items.Count > 0;
            FlyoutPlacement.Attach(HistoryButton.ContextMenu, HistoryButton);
        }

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
            var dialog = new HistorySettingsDialog(ShellLocation.History.Retention, ApplyHistoryRetention) { Owner = this };
            dialog.ShowDialog();
        }

        private bool ApplyHistoryRetention(HistoryRetention policy)
        {
            bool applied = ShellLocation.History.ApplyRetention(policy, removed =>
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
                var entries = HistoryEntries();
                HistoryList.ItemsSource = entries;
                HistoryList.SelectedItem = HistoryRuns.Selection(entries, selected);
            }
            finally { _syncingHistory = false; }
        }

        /// <summary>Whether History lists every Location rather than the scan workspace's.</summary>
        private bool _historyAllLocations = App.GetSetting("HistoryAllLocations") == "1";

        /// <summary>
        /// Newest first. Each entry keeps the store it came from, so its baseline is picked from
        /// its own Location, and is labeled with that Location when every Location is listed.
        /// </summary>
        // Any run can be deleted from the list: right-click Delete, or the Delete key.
        private void InitHistoryDelete()
        {
            var delete = new MenuItem { InputGestureText = "Delete", Icon = MenuGlyph.Create(0xE74D) };
            delete.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Loc_DeleteButton");
            delete.Click += (_, _) => DeleteSelectedHistoryEntry();
            var rename = new MenuItem { InputGestureText = "F2", Icon = MenuGlyph.Create(0xE8AC) };
            rename.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_History_Rename");
            rename.Click += (_, _) => RenameSelectedHistoryEntry();
            HistoryList.ContextMenu = new ContextMenu { Items = { rename, delete } };
            HistoryList.PreviewMouseRightButtonDown += (_, e) =>
            {
                if (ItemsControl.ContainerFromElement(HistoryList, e.OriginalSource as DependencyObject) is ListBoxItem row)
                    row.IsSelected = true;
            };
            HistoryList.ContextMenuOpening += (_, e) => { if (HistoryList.SelectedItem is not ScanHistoryEntry) e.Handled = true; };
            HistoryList.PreviewKeyDown += (_, e) =>
            {
                if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
                if (e.Key == System.Windows.Input.Key.Delete) DeleteSelectedHistoryEntry();
                else if (e.Key == System.Windows.Input.Key.F2) RenameSelectedHistoryEntry();
                else return;
                e.Handled = true;
            };
        }

        // A scan is named so one Location can keep several networks apart; an empty name
        // goes back to showing the target.
        private void RenameSelectedHistoryEntry()
        {
            if (HistoryList.SelectedItem is ScanHistoryEntry entry) RenameHistoryEntry(entry);
        }

        private void RenameHistoryEntry(ScanHistoryEntry entry)
        {
            var dialog = new InputDialog(Loc("Str_History_Rename").TrimEnd('.'), entry.Target, Loc("Str_Rename_Label"), entry.Name,
                Loc("Str_Rename_Save"), Loc("Str_Btn_Cancel")) { Owner = this };
            dialog.ShowDialog();
            if (!dialog.Confirmed) return;
            HistoryRuns.Rename(entry.Owner ?? ShellLocation.History, entry, (dialog.Value ?? string.Empty).Trim());
            RefreshHistoryList();
            RefreshLocationsList();
            // The list now holds a fresh copy of the run; the pane is told, as it is after a delete.
            if (_workspaceView == "history") ShowHistoryEntry();
        }

        private void DeleteSelectedHistoryEntry()
        {
            if (HistoryList.SelectedItem is ScanHistoryEntry entry) DeleteHistoryEntry(entry);
        }

        private void DeleteHistoryEntry(ScanHistoryEntry entry)
        {
            HistoryRuns.Delete(entry.Owner ?? ShellLocation.History, entry);
            RefreshHistoryList();
            RefreshLocationsList();
            // The refresh selects another run without raising its change, so the pane is told here;
            // otherwise it would keep showing (and exporting) the run that was just deleted.
            ShowHistoryEntry();
        }

        private List<ScanHistoryEntry> HistoryEntries()
        {
            var sources = _historyAllLocations && LocationService.Loaded && !DemoData.Enabled && LocationService.Items.Count > 0
                ? LocationService.Items.Select(info => (Name: info.Name, Store: LocationService.Get(info).History)).ToList()
                : [(Name: string.Empty, Store: ShellLocation.History)];
            var entries = new List<ScanHistoryEntry>();
            foreach (var (name, store) in sources)
                foreach (var entry in store.Entries)
                {
                    entry.Owner = store;
                    entry.LocationName = name;
                    entries.Add(entry);
                }
            return [.. entries.OrderByDescending(entry => entry.ScannedAt)];
        }

        private void HistorySettingsMenu_LocationsClick(object sender, RoutedEventArgs e)
        {
            _historyAllLocations = sender is MenuItem { IsChecked: true };
            App.SetSetting("HistoryAllLocations", _historyAllLocations ? "1" : "0");
            RefreshHistoryList();
            if (_workspaceView == "history") ShowHistoryEntry();
        }

        /// <summary>
        /// Offered once after the first paint when the history file exists but cannot be read.
        /// The file is never overwritten while unreadable, so this only explains and retries.
        /// </summary>
        private void OfferHistoryRecovery()
        {
            var history = ShellLocation.History;
            while (history.LoadError is { } error)
            {
                string message = string.Format(Loc("Str_History_LoadFailed"), error, history.HistoryPath);
                if (MessageBox.Show(this, message, AppInfo.DisplayName, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                    != MessageBoxResult.Yes) break;
                history.Load();
            }
            RefreshHistoryList();
            _historyWorkspace?.RefreshLocale();
        }

        /// <summary>
        /// Offered once after the first paint when the Location catalog could not be read or the
        /// first-start move into Unassigned failed. The original files stay in use meanwhile.
        /// </summary>
        private void OfferLocationRecovery()
        {
            if (LocationService.LoadError is not { } error) return;
            MessageBox.Show(this, string.Format(Loc("Str_Loc_LoadFailed"), error), AppInfo.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        /// <summary>The Location the scan workspace is in, which the sidebar lists follow.</summary>
        private LocationData ShellLocation => _scanWorkspace?.Location ?? (_startLocation ??= LocationService.Initial());
        private LocationData? _startLocation;

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
                _historyWorkspace = new HistoryWorkspace
                {
                    LayoutTransform = new ScaleTransform(_appScale, _appScale),
                    HistorySource = () => ShellLocation.History
                };
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
