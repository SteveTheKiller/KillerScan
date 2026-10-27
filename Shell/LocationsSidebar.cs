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
            // Save works from an unsaved scan (Save to a Location) and inside a Location.
            SaveToLocationButton.IsEnabled = _scanWorkspace is { IsScanning: false } scanning
                && (!scanning.IsUnsaved || LocationService.Items.Any(item => item.Id != LocationCatalog.UnassignedId));
            string? current = ShellLocation.Info?.Id;
            string? selectedLocation = (_selectedLocationNode as LocationRow)?.Info.Id;
            var selectedScan = (_selectedLocationNode as ScanNode)?.Profile;
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
                // Its saved scans: the named networks this Location keeps, each a target and its
                // settings (LAN, Guest Wi-Fi, Cameras). Results are never listed here; every run
                // is in Scan history.
                var store = LocationService.Get(item).SavedScans;
                foreach (var profile in store.Items.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var node = new ScanNode(this) { Profile = profile, Location = row };
                    if (selectedScan != null && string.Equals(profile.Name, selectedScan.Name, StringComparison.OrdinalIgnoreCase))
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
                else if (e.Key == Key.F2) { RenameSavedScan(scan); e.Handled = true; }
                else if (e.Key == Key.Delete) { DeleteSavedScan(scan); e.Handled = true; }
            }
        }

        /// <summary>Opens a saved scan from the tree: into its Location if it is another one,
        /// then its target, settings and adapter go into the scan bar, ready to scan.</summary>
        private void OpenScan(ScanNode scan)
        {
            if (ShellLocation.Info?.Id != scan.Location.Info.Id && !NewScan().OpenLocation(scan.Location.Info)) return;
            if (_scanWorkspace is not { } workspace) return;
            workspace.LoadProfile(scan.Profile);
            workspace.AdapterChoice = scan.Profile.Adapter ?? string.Empty;
            RefreshLocationsList();
        }

        /// <summary>Renames in place: the label turns into a text box (ScanEditor_* below).</summary>
        private void RenameSavedScan(ScanNode scan)
        {
            scan.EditText = scan.HasNickname ? scan.Profile.Name : string.Empty;
            scan.IsEditing = true;
        }

        private void ScanEditor_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TextBox box && box.IsVisible)
                Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ScanEditor_KeyDown(object sender, KeyEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ScanNode scan) return;
            if (e.Key == Key.Enter) { CommitScanName(scan); e.Handled = true; }
            else if (e.Key == Key.Escape) { scan.IsEditing = false; e.Handled = true; LocationsList.Focus(); }
        }

        private void ScanEditor_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ScanNode { IsEditing: true } scan) CommitScanName(scan);
        }

        /// <summary>A blank name goes back to the subnet, which is what an un-nicknamed scan shows.</summary>
        private void CommitScanName(ScanNode scan)
        {
            scan.IsEditing = false;
            string name = scan.EditText.Trim();
            if (name.Length == 0) name = scan.Profile.Target;
            if (name == scan.Profile.Name) return;
            var store = LocationService.Get(scan.Location.Info).SavedScans;
            bool saved = store.SaveEdited(name, scan.Profile.Target, scan.Profile.Options, scan.Profile.Adapter ?? string.Empty)
                && store.Delete(scan.Profile);
            PersistenceFeedback.Check(saved, store.Retry, () => store.SaveError);
            RefreshLocationsList();
        }

        // ---- Dragging a saved scan to another Location ----
        private ScanNode? _dragScan;
        private Point _dragStart;

        private void InitLocationsDrag()
        {
            LocationsList.AllowDrop = true;
            LocationsList.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _dragScan = NodeUnder(e.OriginalSource as DependencyObject) as ScanNode;
                _dragStart = e.GetPosition(LocationsList);
            };
            LocationsList.PreviewMouseMove += (_, e) =>
            {
                if (_dragScan == null || e.LeftButton != MouseButtonState.Pressed) return;
                var now = e.GetPosition(LocationsList);
                if (Math.Abs(now.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
                    && Math.Abs(now.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                var scan = _dragScan;
                _dragScan = null;
                DragDrop.DoDragDrop(LocationsList, new DataObject(typeof(ScanNode), scan), DragDropEffects.Move);
            };
            LocationsList.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetData(typeof(ScanNode)) is ScanNode scan && TargetRow(e) is { } row
                    && row.Info.Id != scan.Location.Info.Id ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            LocationsList.Drop += (_, e) =>
            {
                if (e.Data.GetData(typeof(ScanNode)) is ScanNode scan && TargetRow(e) is { } row
                    && row.Info.Id != scan.Location.Info.Id)
                    MoveSavedScan(scan, row);
                e.Handled = true;
            };
        }

        private static object? NodeUnder(DependencyObject? source)
        {
            for (var node = source; node != null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
                if (node is TreeViewItem item) return item.DataContext;
            return null;
        }

        /// <summary>The Location a drop lands in: the row itself, or the Location of a scan dropped on.</summary>
        private LocationRow? TargetRow(DragEventArgs e) => NodeUnder(e.OriginalSource as DependencyObject) switch
        {
            LocationRow row => row,
            ScanNode node => node.Location,
            _ => null
        };

        /// <summary>Moves a saved scan into another Location: saved there first, then removed here,
        /// so a failed save loses nothing.</summary>
        private void MoveSavedScan(ScanNode scan, LocationRow row)
        {
            var from = LocationService.Get(scan.Location.Info).SavedScans;
            var to = LocationService.Get(row.Info).SavedScans;
            bool saved = to.SaveEdited(scan.Profile.Name, scan.Profile.Target, scan.Profile.Options, scan.Profile.Adapter ?? string.Empty);
            if (!PersistenceFeedback.Check(saved, to.Retry, () => to.SaveError)) return;
            PersistenceFeedback.Check(from.Delete(scan.Profile), from.Retry, () => from.SaveError);
            _expandedLocations.Add(row.Info.Id);
            RefreshLocationsList();
        }

        private void DeleteSavedScan(ScanNode scan)
        {
            var store = LocationService.Get(scan.Location.Info).SavedScans;
            PersistenceFeedback.Check(store.Delete(scan.Profile), store.Retry, () => store.SaveError);
            RefreshLocationsList();
        }

        private void ScanOpen_Click(object sender, RoutedEventArgs e) { if (ScanFor(sender) is { } scan) OpenScan(scan); }
        private void ScanRename_Click(object sender, RoutedEventArgs e) { if (ScanFor(sender) is { } scan) RenameSavedScan(scan); }
        private void ScanDelete_Click(object sender, RoutedEventArgs e) { if (ScanFor(sender) is { } scan) DeleteSavedScan(scan); }

        private void OpenLocation(LocationRow row)
        {
            if (NewScan().OpenLocation(row.Info)) RefreshLocationsList();
        }

        private void LocationOpen_Click(object sender, RoutedEventArgs e) { if (LocationFor(sender) is { } row) OpenLocation(row); }
        private void LocationRename_Click(object sender, RoutedEventArgs e) { if (LocationFor(sender) is { } row) _scanWorkspace?.RenameLocation(row.Info); }
        private void LocationExport_Click(object sender, RoutedEventArgs e) { if (LocationFor(sender) is { } row) _scanWorkspace?.ExportLocation(row.Info); }
        private void LocationDelete_Click(object sender, RoutedEventArgs e) { if (LocationFor(sender) is { } row) _scanWorkspace?.DeleteLocation(row.Info); }
        private void NewLocation_Click(object sender, RoutedEventArgs e) => _scanWorkspace?.NewLocation();
        private void ImportLocation_Click(object sender, RoutedEventArgs e) => _scanWorkspace?.ImportLocationFromSidebar();

        // Save to: the unsaved scan on screen goes into a Location picked from a short menu.
        private void SaveToLocation_Click(object sender, RoutedEventArgs e)
        {
            if (_scanWorkspace is not { } scan || sender is not Button button) return;
            if (!scan.IsUnsaved)
            {
                // Already in a Location: save the target and settings as one of its networks.
                var name = new InputDialog(Loc("Str_Loc_SaveScan"), scan.Targets.Trim(), Loc("Str_Rename_Label"),
                    string.Empty, Loc("Str_Rename_Save"), Loc("Str_Btn_Cancel")) { Owner = this };
                name.ShowDialog();
                if (name.Confirmed && scan.SaveCurrentScan(name.Value)) RefreshLocationsList();
                return;
            }
            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var info in LocationService.Items.Where(item => item.Id != LocationCatalog.UnassignedId)
                         .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var captured = info;
                var item = new MenuItem { Header = info.ToString() };
                item.Click += (_, _) =>
                {
                    // Optional name for the run; blank keeps the target as its label.
                    var dialog = new InputDialog(Loc("Str_Loc_SaveScan"), captured.ToString(), Loc("Str_Rename_Label"),
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
        /// <summary>The Location's icon glyph; the pin when it has none.</summary>
        public string Glyph => LocationIcons.Glyph(Info.Icon);
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
    /// <summary>A saved scan under its Location: a named target with its settings.</summary>
    /// <summary>A saved scan under its Location: a named target with its settings.</summary>
    internal sealed class ScanNode(MainWindow owner) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected, _isEditing;
        private string _editText = string.Empty;
        public ScanProfile Profile { get; init; } = new();
        public LocationRow Location { get; init; } = null!;

        /// <summary>A name other than the subnet itself (a blank save stores the subnet as the name).</summary>
        public bool HasNickname => Profile.Name.Trim().Length > 0
            && !string.Equals(Profile.Name.Trim(), Profile.Target.Trim(), StringComparison.OrdinalIgnoreCase);
        /// <summary>The nickname, or the subnet when there is none.</summary>
        public string Label => HasNickname ? Profile.Name.Trim() : Profile.Target;
        public string Tip => Profile.Target;

        public bool IsEditing
        {
            get => _isEditing;
            set { if (_isEditing == value) return; _isEditing = value; PropertyChanged?.Invoke(this, new(nameof(IsEditing))); }
        }

        public string EditText
        {
            get => _editText;
            set { _editText = value ?? string.Empty; PropertyChanged?.Invoke(this, new(nameof(EditText))); }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected == value) return; _isSelected = value; owner.LocationNodeSelected(this, value); PropertyChanged?.Invoke(this, new(nameof(IsSelected))); }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public override string ToString() => Label + ", " + Profile.Target;
    }
}
