using System.Threading;
using System.Windows;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private string CountLabel(string tail) =>
            string.IsNullOrEmpty(ActiveSubnet) ? tail : $"{ActiveSubnet}  ·  {tail}";

        private void WireSession(ScanSession session)
        {
            session.Scanner.StatusChanged += scanStatus => Dispatcher.Invoke(() =>
            {
                if (_disposed) return;
                string status = ScanStatusText.Format(scanStatus, Loc);
                session.Status = status;
                StatusText.Text = status;
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
            session.Scanner.ProgressChanged += progress => Dispatcher.Invoke(() =>
            {
                if (_disposed) return;
                session.Progress = progress;
                ScanProgress.Value = progress;
            });
            session.Scanner.DeviceFound += device => Dispatcher.Invoke(() =>
            {
                if (_disposed) return;
                session.Devices.Add(device);
                RefreshDeviceCount();
            });
        }

        private async void ScanBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_disposed) return;
            if (_active.Cts != null) { Stop(); return; }
            if (_rescanCts != null) return;
            if (DemoMode) { GenerateDemoScan(); return; }
            var targets = ScanTargets.Parse(Targets);
            if (!targets.Ok)
            {
                StatusText.Text = targets.Error switch
                {
                    TargetError.Invalid => string.Format(Loc("Str_St_BadTarget"), targets.Detail),
                    TargetError.TooLarge => string.Format(Loc("Str_St_TooManyAddresses"),
                        targets.Detail, ScanTargets.MaxAddresses.ToString("N0")),
                    _ => Loc("Str_St_NoTarget")
                };
                FocusTargets();
                return;
            }
            if (!AdapterAllowsScan(Targets, targets.Addresses, out string adapter)) return;
            _active.Adapter = adapter;
            _active.Network = ScanNetwork.Unknown;
            _active.SubnetText = Targets;
            _active.ScannedSubnet = targets.Summary;
            _active.ScannedTargets = Targets;
            _active.ScannedAt = DateTimeOffset.Now;
            _active.Completion = ScanCompletion.Partial;
            _active.Refresh = null;
            _active.Devices.Clear();
            _topologyPositions.Clear();
            var cts = new CancellationTokenSource();
            _active.Cts = cts;
            ScanProgress.Value = 0;
            ScanProgress.Visibility = Visibility.Visible;
            UpdateExportButtonAvailability();
            ScanBtn.Content = Loc("Str_Btn_Stop");
            // A scan in flight is no longer a complete picture, so the light drops out of its
            // finished state here rather than when this one ends.
            _scanCompleted = false;
            UpdateDeepScanButton();
            RefreshDeviceCount();
            StateChanged?.Invoke(this, EventArgs.Empty);
            bool completed = false;
            var options = _active.Options;
            bool runDeep = _runDeepAfterScan;
            // The run belongs to the Location it started in; switching is locked until it ends.
            var location = _location;
            _active.NewIdentities.Clear();
            if (location.Info != null) LocationService.SetLastUsed(location.Info, _active.SubnetText, options, _adapterChoice);
            try
            {
                await _active.Scanner.ScanSubnetAsync(_active.SubnetText, cts.Token, options, fullScan: true);
                completed = !cts.IsCancellationRequested && !_disposed;
                if (completed)
                {
                    // Deleted elsewhere while scanning: the results stay, as a Temporary scan.
                    // A demo's fabricated network says nothing about the adapter of the PC running it.
                    if (!DemoData.Enabled) _active.Network = await IdentifyNetwork();
                    // Checked after that wait, right before the write it protects.
                    bool left = LeaveDeletedLocation();
                    if (left) location = _location;
                    if (!runDeep || _active.Devices.Count == 0) RecordCompletedRun(options, deep: false);
                    NoteKnownDevices(location);
                    if (!left) NoteUnsaved(location);
                }
            }
            catch (OperationCanceledException)
            {
                _active.Completion = ScanCompletion.Canceled;
                if (!_disposed) StatusText.Text = Loc("Str_St_ScanCanceled");
            }
            catch (Exception ex) { if (!_disposed) StatusText.Text = string.Format(Loc("Str_Err_Scan"), ex.Message); }
            finally
            {
                _active.Cts = null;
                cts.Dispose();
                bool deep = completed && runDeep && _active.Devices.Count > 0 && !_disposed;
                _scanCompleted = completed;
                // A Location deleted elsewhere while this scan ran is left now, however it ended;
                // the completed path above already did, the canceled and failed ones did not.
                if (!_disposed && !completed) LeaveDeletedLocation();
                if (!_disposed)
                {
                    _active.Status = StatusText.Text;
                    ScanBtn.Content = Loc("Str_Btn_Scan");
                    ScanProgress.Visibility = Visibility.Collapsed;
                    UpdateExportButtonAvailability();
                    UpdateDeepScanButton();
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
                if (deep) await DeepScanAllAsync(recordProfile: true);
            }
        }

        /// <summary>
        /// Records a completed scan as Known in its Location. The first one sets the baseline and
        /// approves nothing; later ones count only devices new since then. A Temporary scan has
        /// no baseline to compare against, so it reports nothing either way.
        /// </summary>
        private void NoteKnownDevices(LocationData location)
        {
            if (location.IsTemporary || _active.Devices.Count == 0) return;
            // A Location learns a network only from what was saved into it: a run that finishes on
            // one of its saved scans' targets records the router; any other run does not.
            if (location.SavedScans.Items.Any(saved => string.Equals(saved.Target.Trim(), _active.ScannedTargets.Trim(), StringComparison.OrdinalIgnoreCase)))
                RecordGateway(location, _active.ScannedTargets);
            var devices = location.Devices;
            var seen = devices.Observe(_active.Devices.ToList());
            PersistenceFeedback.Check(seen.Saved, devices.Retry, () => devices.SaveError);
            _active.NewIdentities = new(seen.NewDevices.Select(DeviceIdentity.For), StringComparer.OrdinalIgnoreCase);
            if (seen.BaselineCreated)
                StatusText.Text = string.Format(Loc(ScanStatusText.Counted("Str_St_KnownBaseline", _active.Devices.Count)), _active.Devices.Count);
            else if (seen.NewDevices.Count > 0)
                StatusText.Text = string.Format(Loc(ScanStatusText.Counted("Str_St_UnknownDevices", seen.NewDevices.Count)), seen.NewDevices.Count);
            _filteredView?.Refresh();
        }

        // Only a Temporary scan can be lost by closing the app, so it says so once there is
        // something to lose, and only where Save scan is on offer to keep it.
        private void NoteUnsaved(LocationData location)
        {
            if (location.IsTemporary && _active.Devices.Count > 0 && LocationService.Editable)
                StatusText.Text = Loc("Str_Loc_TemporaryNotice");
        }

        // Off the UI thread: the ARP read is an arp.exe run. A run with no evidence just compares less.
        private async Task<ScanNetwork> IdentifyNetwork()
        {
            var macs = ScanNetwork.Macs(_active.Devices);
            string targets = _active.ScannedTargets, adapter = _active.Adapter;
            try { return await Task.Run(() => ScanNetwork.Identify(targets, adapter, macs, FindAdapter, AdapterGateway, AdapterIdentity)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return ScanNetwork.Unknown; }
        }

        private void RecordCompletedRun(ScanOptions options, bool deep, ScanCompletion completion = ScanCompletion.Complete)
        {
            var history = _location.History;
            var recorded = history.Record(_active.ScannedSubnet, _active.ScannedTargets, _active.Devices, completion, options, deep, _active.Adapter, _active.Network);
            _active.ScannedAt = recorded.Current.ScannedAt;
            _active.Completion = completion;
            if (_disposed) return;
            if (PersistenceFeedback.Check(recorded.Saved, history.Retry, () => history.SaveError))
                HistoryRecorded?.Invoke(this, EventArgs.Empty);
        }
    }
}
