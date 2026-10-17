using System.Collections.ObjectModel;
using System.Threading;
using KillerScan.Services;

namespace KillerScan.Models
{
    // Each scan tab owns its targets, results, scanner, and cancellation lifetime.
    internal sealed class ScanSession(string subnet)
    {
        public ObservableCollection<NetworkDevice> Devices { get; } = [];
        public NetworkScanner Scanner { get; set; } = new();
        public ScanOptions Options { get; set; } = new();
        public string LocationId { get; set; } = Locations.Current.Id;
        public CancellationTokenSource? Cts { get; set; }

        public string SubnetText { get; set; } = subnet;
        public string Status { get; set; } = "Ready";
        public double Progress { get; set; }

        public bool IsScanning => Cts != null;

        public string ScannedSubnet { get; set; } = "";
    }
}
