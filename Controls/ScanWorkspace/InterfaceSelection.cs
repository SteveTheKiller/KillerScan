using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        internal string SelectedAdapterId { get; private set; } = "";
        internal string SelectedSourceAddress { get; private set; } = "";

        internal void SelectInterface(string adapterId, string sourceAddress, bool updateTarget = true)
        {
            if (IsScanning || _disposed) return;
            bool changed = SelectedAdapterId != adapterId || SelectedSourceAddress != sourceAddress;
            SelectedAdapterId = adapterId ?? "";
            SelectedSourceAddress = sourceAddress ?? "";
            if (changed)
            {
                _active.Devices.Clear(); _topologyPositions.Clear();
                _active.ScannedSubnet = ""; _scanCompleted = false;
                RefreshDeviceCount(); UpdateDeepScanButton();
            }
            if (DemoMode) return;
            if ((SelectedAdapterId.Length == 0) != (SelectedSourceAddress.Length == 0))
            { StatusText.Text = Loc("Str_Interface_Unavailable"); return; }
            var net = SelectedAdapterId.Length == 0 ? LocalNetwork.Detect() : LocalNetwork.Enumerate()
                .FirstOrDefault(n => n.AdapterId == SelectedAdapterId && n.LocalIp == SelectedSourceAddress);
            if (net == null)
            {
                StatusText.Text = Loc("Str_Interface_Unavailable");
                return; // Preserve the stale explicit choice; it must not silently become Automatic.
            }
            ShowNetworkInfo(net);
            if (updateTarget) Targets = net.Subnet;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void PrepareScanner()
        {
            if ((SelectedAdapterId.Length == 0) != (SelectedSourceAddress.Length == 0))
                throw new NetworkBindingException("An explicit interface requires both its ID and source IPv4 address.");
            var scanner = EngineHost.CreateScanner(SelectedAdapterId.Length == 0 ? null :
                ScanBinding.Resolve(SelectedAdapterId, SelectedSourceAddress));
            _active.Scanner = scanner;
            WireSession(_active);
        }
    }
}
