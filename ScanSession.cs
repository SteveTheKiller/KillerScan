using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan
{
    // One scan tab: its own device list, scanner engine, subnet and scan state. Tabs run
    // independently, so a background tab keeps scanning while you look at another.
    internal sealed class ScanSession(string subnet) : INotifyPropertyChanged
    {
        public ObservableCollection<NetworkDevice> Devices { get; } = [];
        public NetworkScanner Scanner { get; } = new();
        public CancellationTokenSource? Cts { get; set; }

        public string SubnetText { get; set; } = subnet;
        public string Status { get; set; } = "Ready";
        public double Progress { get; set; }

        public bool IsScanning => Cts != null;

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnChanged(nameof(IsActive)); }
        }

        private string _scannedSubnet = "";
        public string ScannedSubnet
        {
            get => _scannedSubnet;
            set { _scannedSubnet = value; OnChanged(nameof(ScannedSubnet)); OnChanged(nameof(Title)); }
        }

        // Tab caption: the scanned subnet once a scan has run, otherwise a placeholder.
        public string Title => string.IsNullOrEmpty(ScannedSubnet) ? "New scan" : ScannedSubnet;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
