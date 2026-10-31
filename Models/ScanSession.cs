using System.Collections.ObjectModel;
using System.Threading;
using KillerScan.Services;

namespace KillerScan.Models
{
    // Each scan tab owns its targets, results, scanner, and cancellation lifetime.
    internal sealed class ScanSession(string subnet, NetworkScanner? scanner = null)
    {
        public ObservableCollection<NetworkDevice> Devices { get; } = [];
        public NetworkScanner Scanner { get; } = scanner ?? new();
        public CancellationTokenSource? Cts { get; set; }

        public string SubnetText { get; set; } = subnet;
        public ScanOptions Options { get; set; } = new();
        public string Status { get; set; } = "Ready";
        public double Progress { get; set; }

        public bool IsScanning => Cts != null;

        public string ScannedSubnet { get; set; } = "";

        /// <summary>The target text the results came from. <see cref="SubnetText"/> follows the box, which stays editable during a run.</summary>
        public string ScannedTargets { get; set; } = "";
        public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.Now;
        public ScanCompletion Completion { get; set; } = ScanCompletion.Partial;

        /// <summary>The adapter the scan's traffic routed through when it started.</summary>
        public string Adapter { get; set; } = string.Empty;

        /// <summary>The evidence for which network the last run was on.</summary>
        public ScanNetwork Network { get; set; } = ScanNetwork.Unknown;

        /// <summary>
        /// Manual rescans after the scan itself. They change some devices but not the scan's
        /// own time or completion, so reports state them separately. Null until a refresh.
        /// </summary>
        public ScanRefresh? Refresh { get; set; }

        /// <summary>Devices this snapshot found outside its Location's Known list, by identity.</summary>
        public HashSet<string> NewIdentities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What manual rescans of this snapshot did, summed across every refresh.</summary>
    internal sealed record ScanRefresh(DateTimeOffset At, int Refreshed, int Failed, bool Canceled)
    {
        public bool Partial => Failed > 0 || Canceled;
        public ScanRefresh Then(DateTimeOffset at, int refreshed, int failed, bool canceled) =>
            new(at, Refreshed + refreshed, Failed + failed, Canceled || canceled);
    }
}
