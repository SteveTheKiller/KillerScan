using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private void LocationsButton_Click(object sender, RoutedEventArgs e) => ShowLocationsSection();

        private void ShowLocationsSection()
        {
            ShortcutsOverlay.Visibility = Visibility.Collapsed;
            AboutOverlay.Visibility = Visibility.Collapsed;
            // Always shows the list rather than closing it: Manage Locations is a request to see it.
            if (!_sidebarCollapsed && _sidebarSection == "locations") { RefreshLocationsList(); return; }
            ShowSidebarSection("locations");
        }

        // Locations expanded in the tree, and the ones seen before (so the current Location opens
        // by default the first time, and a node the user collapsed stays collapsed).
        private readonly HashSet<string> _expandedLocations = [];
        private readonly HashSet<string> _seenLocations = [];
        private object? _selectedLocationNode;

        private void RefreshLocationsList()
        {
            SaveToLocationButton.IsEnabled = _scanWorkspace is { IsUnsaved: true, IsScanning: false }
                && LocationService.Items.Any(item => item.Id != LocationCatalog.UnassignedId);
            string? current = ShellLocation.Info?.Id;
            string? selectedLocation = (_selectedLocationNode as LocationRow)?.Info.Id;
            var selectedScan = (_selectedLocationNode as ScanNode)?.Entry;
            _selectedLocationNode = null;
            var rows = new List<LocationRow>();
            foreach (var item in LocationService.Items
                .Where(item => item.Id != LocationCatalog.UnassignedId)   // not a place (Location.cs)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (_seenLocations.Add(item.Id) && item.Id == current) _expandedLocations.Add(item.Id);
                var row = new LocationRow(this)
                {
                    Info = item,
                    Name = item.Id == current ? string.Format(Loc("Str_Loc_Current"), item.Name) : item.Name,
                    // The customer label, or else the first line of the notes, as a reminder.
                    Detail = item.Customer.Length > 0 ? item.Customer
                        : item.Notes.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty,
                };
                // Its scans, newest first, under it.
                var store = LocationService.Get(item).History;
                foreach (var entry in store.Entries.OrderByDescending(entry => entry.ScannedAt))
                {
                    entry.Owner = store;
                    var node = new ScanNode(this) { Entry = entry, Location = row };
                    if (selectedScan != null && entry.ScannedAt == selectedScan.ScannedAt && entry.Target == selectedScan.Target)
                        node.IsSelected = true;
                    row.Children.Add(node);
                }
                row.InitExpanded(_expandedLocations.Contains(item.Id));
                if (item.Id == selectedLocation && selectedScan == null) row.IsSelected = true;
                rows.Add(row);
            }
            LocationsList.ItemsSource = rows;
            NewLocationButton.IsEnabled = LocationService.Editable && _scanWorkspace != null;
        }

        internal void LocationNodeSelected(object node, bool selected)
        {
            if (selected) _selectedLocationNode = node;
            else if (ReferenceEquals(_selectedLocationNode, node)) _selectedLocationNode = null;
        }

        internal void LocationNodeExpanded(LocationRow row, bool expanded)
        {
            if (expanded) _expandedLocations.Add(row.Info.Id); else _expandedLocations.Remove(row.Info.Id);
        }

        private static LocationRow? LocationFor(object sender) => (sender as FrameworkElement)?.DataContext as LocationRow;
        private static ScanNode? ScanFor(object sender) => (sender as FrameworkElement)?.DataContext as ScanNode;

        private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
        {
            while (node != null && node is not T)
                node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
            return node as T;
        }

        private void LocationList_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is not { } item) return;
            item.IsSelected = true;
            item.Focus();
        }

        // A click opens: a Location switches to it, a scan opens in Scan history. The chevron
        // only expands, so a press on it is left to the tree.
        private void LocationList_Activate(object sender, MouseButtonEventArgs e)
        {
            if (FindAncestor<System.Windows.Controls.Primitives.ToggleButton>(e.OriginalSource as DependencyObject) != null) return;
            switch (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject)?.DataContext)
            {
                case LocationRow row: OpenLocation(row); break;
                case ScanNode scan: OpenScan(scan); break;
            }
        }

        private void LocationList_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None) return;
            var node = _selectedLocationNode ?? LocationsList.SelectedItem;
            if (node is LocationRow row)
            {
                if (e.Key == Key.Enter) { OpenLocation(row); e.Handled = true; }
                else if (e.Key == Key.F2) { _scanWorkspace?.RenameLocation(row.Info); e.Handled = true; }
                else if (e.Key == Key.Delete) { _scanWorkspace?.DeleteLocation(row.Info); e.Handled = true; }
            }
            else if (node is ScanNode scan)
            {
                if (e.Key == Key.Enter) { OpenScan(scan); e.Handled = true; }
                else if (e.Key == Key.F2) { RenameHistoryEntry(scan.Entry); e.Handled = true; }
                else if (e.Key == Key.Delete) { DeleteHistoryEntry(scan.Entry); e.Handled = true; }
            }
        }

        /// <summary>Opens a scan from the tree: into its Location if it is another one, then in
        /// Scan history, selected there.</summary>
        private void OpenScan(ScanNode scan)
        {
            if (ShellLocation.Info?.Id != scan.Location.Info.Id && !NewScan().OpenLocation(scan.Location.Info)) return;
            RefreshHistoryList();
            var match = HistoryList.Items.OfType<ScanHistoryEntry>()
                .FirstOrDefault(entry => entry.ScannedAt == scan.Entry.ScannedAt && entry.Target == scan.Entry.Target);
            if (match == null) return;
            _syncingHistory = true;
            try { HistoryList.SelectedItem = match; }
            finally { _syncingHistory = false; }
            ShowHistoryEntry();
            RefreshLocationsList();
        }

        private void ScanOpen_Click(object sender, RoutedEventArgs e) { if (ScanFor(sender) is { } scan) OpenScan(scan); }
        private void ScanRename_Click(object sender, RoutedEventArgs e) { if (ScanFor(sender) is { } scan) RenameHistoryEntry(scan.Entry); }
        private void ScanDelete_Click(object sender, RoutedEventArgs e) { if (ScanFor(sender) is { } scan) DeleteHistoryEntry(scan.Entry); }

        private void OpenLocation(LocationRow row)
        {
            if (NewScan().OpenLocation(row.Info)) RefreshLocationsList();
        }

        private void LocationOpen_Click(object sender, RoutedEventArgs e) { if (LocationFor(sender) is { } row) OpenLocation(row); }
        private void LocationRename_Click(object sender, RoutedEventArgs e) { if (LocationFor(sender) is { } row) _scanWorkspace?.RenameLocation(row.Info); }
        private void LocationNotes_Click(object sender, RoutedEventArgs e) { if (LocationFor(sender) is { } row) _scanWorkspace?.EditNotes(row.Info); }
        private void LocationExport_Click(object sender, RoutedEventArgs e) { if (LocationFor(sender) is { } row) _scanWorkspace?.ExportLocation(row.Info); }
        private void LocationDelete_Click(object sender, RoutedEventArgs e) { if (LocationFor(sender) is { } row) _scanWorkspace?.DeleteLocation(row.Info); }
        private void NewLocation_Click(object sender, RoutedEventArgs e) => _scanWorkspace?.NewLocation();
        private void ImportLocation_Click(object sender, RoutedEventArgs e) => _scanWorkspace?.ImportLocationFromSidebar();

        // Save to: the unsaved scan on screen goes into a Location picked from a short menu.
        private void SaveToLocation_Click(object sender, RoutedEventArgs e)
        {
            if (_scanWorkspace is not { } scan || sender is not Button button) return;
            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var info in LocationService.Items.Where(item => item.Id != LocationCatalog.UnassignedId)
                         .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var captured = info;
                var item = new MenuItem { Header = info.ToString() };
                item.Click += (_, _) =>
                {
                    // Optional name for the run; blank keeps the target as its label.
                    var dialog = new InputDialog(Loc("Str_History_Rename").TrimEnd('.'), captured.ToString(), Loc("Str_Rename_Label"),
                        string.Empty, Loc("Str_Rename_Save"), Loc("Str_Btn_Cancel")) { Owner = this };
                    dialog.ShowDialog();
                    if (!dialog.Confirmed) return;
                    if (scan.SaveResultsTo(LocationService.Get(captured), dialog.Value)) RefreshLocationsList();
                };
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) return;
            menu.IsOpen = true;
        }
    }

    /// <summary>A Location in the Locations tree: the Location, what its row shows, its scans.</summary>
    internal sealed class LocationRow(MainWindow owner) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected, _isExpanded;
        public LocationInfo Info { get; init; } = new();
        public string Name { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public List<ScanNode> Children { get; } = [];

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; owner.LocationNodeSelected(this, value); Changed(nameof(IsSelected)); }
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set { if (_isExpanded == value) return; _isExpanded = value; owner.LocationNodeExpanded(this, value); Changed(nameof(IsExpanded)); }
        }

        internal void InitExpanded(bool expanded) => _isExpanded = expanded;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));

        /// <summary>What a screen reader announces for the row.</summary>
        public override string ToString() => Detail.Length == 0 ? Name : Name + ", " + Detail;
    }

    /// <summary>A scan under its Location in the Locations tree.</summary>
    internal sealed class ScanNode(MainWindow owner) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;
        public ScanHistoryEntry Entry { get; init; } = new();
        public LocationRow Location { get; init; } = null!;
        public string Title => Entry.DisplayName;
        public string When => Entry.ScannedAt.ToString("g", System.Globalization.CultureInfo.CurrentCulture);

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; owner.LocationNodeSelected(this, value); PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Title + ", " + When;
    }
}
