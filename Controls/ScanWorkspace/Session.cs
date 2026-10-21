using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using KillerScan.Models;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private ObservableCollection<NetworkDevice> ActiveDevices => _active.Devices;

        private string ActiveSubnet
        {
            get => _active.ScannedSubnet;
            set => _active.ScannedSubnet = value;
        }

        // Topology and Services are rebuilt from the whole device list, so redrawing them for each
        // device a scan reports cost N(N+1)/2 node builds over a scan. Changes are coalesced into
        // at most one rebuild per interval; a pending rebuild always runs, so the last device
        // shows up without waiting for anything else to change.
        private static readonly TimeSpan ViewRefreshInterval = TimeSpan.FromMilliseconds(300);
        private System.Windows.Threading.DispatcherTimer? _viewRefreshTimer;
        private DateTime _lastViewRefresh = DateTime.MinValue;

        private void ScheduleViewRefresh()
        {
            if (_disposed || (!_showTopology && !_showServices) || _viewRefreshTimer?.IsEnabled == true) return;
            var wait = _lastViewRefresh + ViewRefreshInterval - DateTime.UtcNow;
            _viewRefreshTimer ??= new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, Dispatcher);
            _viewRefreshTimer.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
            _viewRefreshTimer.Tick -= ViewRefreshTick;
            _viewRefreshTimer.Tick += ViewRefreshTick;
            _viewRefreshTimer.Start();
        }

        private void ViewRefreshTick(object? sender, EventArgs e)
        {
            _viewRefreshTimer?.Stop();
            if (_disposed) return;
            if (_showTopology) RefreshTopology();
            if (_showServices) RefreshServices();
            // Measured from the end: a large diagram can take longer than the interval to build.
            _lastViewRefresh = DateTime.UtcNow;
        }

        private void ActivateSession()
        {
            ResultsGrid.ItemsSource = _active.Devices;
            _active.Devices.CollectionChanged += (_, _) => ScheduleViewRefresh();
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
            UpdateExportButtonAvailability();
            UpdateDeepScanButton();
            FilterInput_TextChanged(FilterInput, null!);
        }
    }
}
