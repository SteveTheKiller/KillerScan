using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        /// <summary>One row of the Locations sidebar: the Location plus what the row shows.</summary>
        internal sealed class LocationRow
        {
            public LocationInfo Info { get; init; } = new();
            public string Name { get; init; } = string.Empty;
            public string Detail { get; init; } = string.Empty;

            /// <summary>What a screen reader announces for the row.</summary>
            public override string ToString() => Detail.Length == 0 ? Name : Name + ", " + Detail;
        }

        private void ShowLocationsSection()
        {
            ShortcutsOverlay.Visibility = Visibility.Collapsed;
            AboutOverlay.Visibility = Visibility.Collapsed;
            // Always shows the list rather than closing it: Manage Locations is a request to see it.
            if (!_sidebarCollapsed && _sidebarSection == "locations") { RefreshLocationsList(); return; }
            ShowSidebarSection("locations");
        }

        private void RefreshLocationsList()
        {
            string? current = ShellLocation.Info?.Id;
            var rows = LocationService.Items
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new LocationRow
                {
                    Info = item,
                    Name = item.Id == current ? string.Format(Loc("Str_Loc_Current"), item.Name) : item.Name,
                    // The customer label, or else the first line of the notes, as a reminder.
                    Detail = item.Customer.Length > 0 ? item.Customer
                        : item.Notes.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0) ?? string.Empty
                })
                .ToList();
            string? selected = (LocationsList.SelectedItem as LocationRow)?.Info.Id;
            LocationsList.ItemsSource = rows;
            LocationsList.SelectedItem = rows.FirstOrDefault(row => row.Info.Id == selected);
            NewLocationButton.IsEnabled = LocationService.Editable && _scanWorkspace != null;
        }

        private static LocationRow? LocationFor(object sender) => (sender as FrameworkElement)?.DataContext as LocationRow;

        private void LocationList_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(LocationsList, e.OriginalSource as DependencyObject) is not ListBoxItem item)
            { LocationsList.SelectedItem = null; return; }
            LocationsList.SelectedItem = item.DataContext;
            item.Focus();
        }

        private void LocationList_Activate(object sender, MouseButtonEventArgs e)
        {
            if (ItemsControl.ContainerFromElement(LocationsList, e.OriginalSource as DependencyObject) is not ListBoxItem { DataContext: LocationRow row })
                return;
            OpenLocation(row);
        }

        private void LocationList_KeyDown(object sender, KeyEventArgs e)
        {
            if (LocationsList.SelectedItem is not LocationRow row || Keyboard.Modifiers != ModifierKeys.None) return;
            if (e.Key == Key.Enter) { OpenLocation(row); e.Handled = true; }
            else if (e.Key == Key.F2) { _scanWorkspace?.RenameLocation(row.Info); e.Handled = true; }
            else if (e.Key == Key.Delete) { _scanWorkspace?.DeleteLocation(row.Info); e.Handled = true; }
        }

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
    }
}
