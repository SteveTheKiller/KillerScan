using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        internal string LocationId => _active.LocationId;
        internal void ResetLocation()
        {
            if (IsScanning) throw new InvalidOperationException("Stop the scan before changing location.");
            _active.LocationId = Locations.Current.Id;
            _active.Devices.Clear(); _topologyPositions.Clear(); _active.ScannedSubnet = "";
            _scanCompleted = false; _active.Options = new ScanOptions();
            SelectInterface("", "", updateTarget: false); Targets = "";
            StatusText.Text = Loc("Str_St_Ready");
            _active.Scanner = EngineHost.CreateScanner(); WireSession(_active);
            RefreshDeviceCount(); UpdateDeepScanButton(); UpdateExportButtonAvailability();
            if (_showServices) RefreshServices();
            if (_showTopology) RefreshTopology();
        }
    }
}
