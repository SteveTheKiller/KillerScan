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

        /// <summary>
        /// The Locations section: every Location in one flat list, for managing them. Switching
        /// and saved scans live in the scan bar's Location picker (ScanWorkspace/Location.cs).
        /// </summary>
        private void RefreshLocationsList()
        {
            string? current = ShellLocation.Info?.Id;
            string? selected = (LocationsList.SelectedItem as LocationRow)?.Info.Id;
            var rows = LocationService.Items
                .Where(item => item.Id != LocationCatalog.UnassignedId)   // not a place (Location.cs)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new LocationRow
                {
                    Info = item,
                    Name = item.Name,
                    IsCurrent = item.Id == current,
                    // The customer label, or else the first line of the notes, as a reminder.
                    Detail = item.Customer.Length > 0 ? item.Customer
                        : item.Notes.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty,
                })
                .ToList();
            LocationsList.ItemsSource = rows;
            LocationsList.SelectedItem = rows.FirstOrDefault(row => row.Info.Id == selected);
            NewLocationButton.IsEnabled = LocationService.Editable && _scanWorkspace != null;
        }

        private static LocationRow? LocationFor(object sender) => (sender as FrameworkElement)?.DataContext as LocationRow;

        // A click only selects, as in any list; a double-click or Enter switches to the Location.
        private void LocationList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left && LocationFor(e.OriginalSource) is { } row) OpenLocation(row);
        }

        private void LocationList_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers != ModifierKeys.None || LocationsList.SelectedItem is not LocationRow row) return;
            if (e.Key == Key.Enter) { OpenLocation(row); e.Handled = true; }
            else if (e.Key == Key.F2) { _scanWorkspace?.RenameLocation(row.Info); e.Handled = true; }
            else if (e.Key == Key.Delete) { _scanWorkspace?.DeleteLocation(row.Info); e.Handled = true; }
        }

        /// <summary>Once the migrated "Saved scans" Location is empty it goes; a tab in it moves to
        /// Unassigned first, so nothing is left pointing at a deleted Location.</summary>
        private void RetireHoldingLocation()
        {
            if (_scanWorkspace is { InImportedScans: true } inside
                && LocationService.Items.FirstOrDefault(LocationService.IsImportedScans) is { } held
                && LocationService.Get(held).SavedScans.Items.Count == 0
                && LocationService.Find(LocationCatalog.UnassignedId) is { } unassigned)
                inside.SetLocation(LocationService.Get(unassigned));
            LocationService.RetireImportedScansIfEmpty();
        }

        private void OpenLocation(LocationRow row)
        {
            if (NewScan().OpenLocation(row.Info)) RefreshLocationsList();
        }

        /// <summary>
        /// The Location card, built as it opens for the row it belongs to: the name and detail over Open,
        /// Edit and Export, with Delete in the header. A tile closes the card before it acts, so the
        /// dialog Edit, Export or Delete opens never sits under it.
        /// </summary>
        private void LocationRow_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not FrameworkElement { ContextMenu: { } menu, DataContext: LocationRow row }) return;
            PrepareLocationRowCard(menu, row);
        }

        internal void PrepareLocationRowCard(ContextMenu menu, LocationRow row)
        {
            var card = ScanCards.Prepare(menu);
            card.Items.Add(ScanCards.Header(card, row.Name, row.Detail,
                Loc("Str_Loc_Delete"), "Delete", () => _scanWorkspace?.DeleteLocation(row.Info)));
            card.Items.Add(ScanCards.Grid(card, new[]
            {
                new KillerFamily.Controls.CardTile("", Loc("Str_Loc_Open"), "Enter", () => OpenLocation(row)) { Id = "Open", Shown = Loc("Str_Card_Open") },
                new KillerFamily.Controls.CardTile("", Loc("Str_Loc_Edit"), "F2", () => _scanWorkspace?.RenameLocation(row.Info)) { Id = "Edit", Shown = Loc("Str_Card_Edit") },
                new KillerFamily.Controls.CardTile("", Loc("Str_Loc_Export"), null, () => _scanWorkspace?.ExportLocation(row.Info)) { Id = "Export", Shown = Loc("Str_Card_Export") },
            }));
        }
        private void NewLocation_Click(object sender, RoutedEventArgs e) => _scanWorkspace?.NewLocation();
        private void ImportLocation_Click(object sender, RoutedEventArgs e) => _scanWorkspace?.ImportLocationFromSidebar();
    }

    /// <summary>A Location in the Locations list: the Location and what its row shows.</summary>
    internal sealed class LocationRow
    {
        public LocationInfo Info { get; init; } = new();
        public string Name { get; init; } = string.Empty;
        public string Detail { get; init; } = string.Empty;
        public bool IsCurrent { get; init; }
        /// <summary>The Location's icon glyph; the pin when it has none.</summary>
        public string Glyph => LocationIcons.Glyph(Info.Icon);
        public string Tip => Detail.Length == 0 ? Name : Name + "\n" + Detail;

        /// <summary>What a screen reader announces for the row, including which one is current.</summary>
        public override string ToString()
        {
            string name = IsCurrent && Application.Current?.TryFindResource("Str_Loc_Current") is string current
                ? string.Format(current, Name) : Name;
            return Detail.Length == 0 ? name : name + ", " + Detail;
        }
    }
}
