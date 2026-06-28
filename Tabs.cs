using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using KillerScan.Models;

namespace KillerScan
{
    // Tabbed scan sessions. Each tab is a ScanSession (own device list + scanner); this partial
    // proxies the active session to the rest of the scanner core and handles new/switch/close.
    public partial class MainWindow
    {
        // ---- Active-session proxies (the scanner core reads/writes the current tab through these) ----
        private ObservableCollection<NetworkDevice> ActiveDevices => _active.Devices;

        private string ActiveSubnet
        {
            get => _active.ScannedSubnet;
            set => _active.ScannedSubnet = value;
        }

        /// <summary>Bind the grid + filter view to a session and mirror its scan state in the chrome.</summary>
        private void ActivateSession(ScanSession s)
        {
            // Persist the outgoing tab's subnet edit before switching away.
            if (_active != null && _active != s) _active.SubnetText = SubnetInput.Text;

            _active = s;
            foreach (var x in _sessions) x.IsActive = (x == s);

            ResultsGrid.ItemsSource = s.Devices;
            _filteredView = CollectionViewSource.GetDefaultView(s.Devices);
            if (_filteredView is ListCollectionView lcv)
            {
                lcv.IsLiveSorting = true;
                lcv.LiveSortingProperties.Clear();
                lcv.LiveSortingProperties.Add(nameof(NetworkDevice.IpSortKey));
                lcv.SortDescriptions.Clear();
                lcv.SortDescriptions.Add(new SortDescription(nameof(NetworkDevice.IpSortKey), ListSortDirection.Ascending));
            }

            SubnetInput.Text = s.SubnetText;
            StatusText.Text = s.Status;
            ScanProgress.Value = s.Progress;
            ScanProgress.Visibility = s.IsScanning ? Visibility.Visible : Visibility.Collapsed;
            ScanBtn.Content = Loc(s.IsScanning ? "Str_Btn_Stop" : "Str_Btn_Scan");
            ExportButton.IsEnabled = s.Devices.Count > 0;

            // Re-apply the current filter to the new collection (also refreshes the device count).
            FilterInput_TextChanged(FilterInput, null!);
        }

        private void NewTab_Click(object sender, RoutedEventArgs e)
        {
            var s = new ScanSession(SubnetInput.Text);
            WireSession(s);                 // Scanning.cs
            _sessions.Add(s);
            ActivateSession(s);
        }

        private void Tab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ScanSession s && s != _active)
                ActivateSession(s);
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;                       // don't also trigger Tab_Click on the row
            if (sender is not FrameworkElement fe || fe.DataContext is not ScanSession s) return;
            if (_sessions.Count == 1) return;       // always keep at least one tab

            s.Cts?.Cancel();
            int idx = _sessions.IndexOf(s);
            _sessions.Remove(s);
            if (_active == s)
                ActivateSession(_sessions[Math.Min(idx, _sessions.Count - 1)]);
        }
    }
}
