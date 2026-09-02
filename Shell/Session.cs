using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using KillerScan.Models;

namespace KillerScan.Shell
{
    // The app has one scan surface. ActiveDevices and ActiveSubnet keep the scanner partials
    // independent of the state container without pretending that multiple tabs exist.
    public partial class MainWindow
    {
        private ObservableCollection<NetworkDevice> ActiveDevices => _active.Devices;

        private string ActiveSubnet
        {
            get => _active.ScannedSubnet;
            set => _active.ScannedSubnet = value;
        }

        private void ActivateSession()
        {
            ResultsGrid.ItemsSource = _active.Devices;
            _active.Devices.CollectionChanged += (_, _) =>
            {
                if (_showTopology) RefreshTopology();
            };
            _filteredView = CollectionViewSource.GetDefaultView(_active.Devices);
            if (_filteredView is ListCollectionView lcv)
            {
                lcv.IsLiveSorting = true;
                lcv.LiveSortingProperties.Clear();
                lcv.LiveSortingProperties.Add(nameof(NetworkDevice.IpSortKey));
                lcv.SortDescriptions.Clear();
                lcv.SortDescriptions.Add(new SortDescription(nameof(NetworkDevice.IpSortKey), ListSortDirection.Ascending));
            }

            SubnetInput.Text = _active.SubnetText;
            StatusText.Text = _active.Status;
            ScanProgress.Value = _active.Progress;
            ScanProgress.Visibility = _active.IsScanning ? Visibility.Visible : Visibility.Collapsed;
            ScanBtn.Content = Loc(_active.IsScanning ? "Str_Btn_Stop" : "Str_Btn_Scan");
            ExportButton.IsEnabled = _active.Devices.Count > 0;
            FilterInput_TextChanged(FilterInput, null!);
        }
    }
}
