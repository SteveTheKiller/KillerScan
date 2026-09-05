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
            session.Scanner.Localizer = key => Dispatcher.Invoke(() => Loc(key));
            session.Scanner.StatusChanged += status => Dispatcher.Invoke(() =>
            {
                if (_disposed) return;
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
            _active.SubnetText = Targets;
            _active.ScannedSubnet = targets.Summary;
            _active.Devices.Clear();
            _topologyPositions.Clear();
            var cts = new CancellationTokenSource();
            _active.Cts = cts;
            ScanProgress.Value = 0;
            ScanProgress.Visibility = Visibility.Visible;
            ExportButton.IsEnabled = false;
            ScanBtn.Content = Loc("Str_Btn_Stop");
            // A scan in flight is no longer a complete picture, so the light drops out of its
            // finished state here rather than when this one ends.
            _scanCompleted = false;
            UpdateDeepScanButton();
            RefreshDeviceCount();
            StateChanged?.Invoke(this, EventArgs.Empty);
            bool completed = false;
            try
            {
                await _active.Scanner.ScanSubnetAsync(_active.SubnetText, cts.Token, fullScan: true);
                completed = !cts.IsCancellationRequested && !_disposed;
                if (completed && _active.Devices.Count > 0)
                {
                    ScanHistory.Record(_active.ScannedSubnet, _active.Devices);
                    // Tell the shell so the history sidebar picks up the new entry rather than
                    // showing a stale list until it is next reopened.
                    HistoryRecorded?.Invoke(this, EventArgs.Empty);
                    if (!DevicePreferences.HasTrustedDevices)
                    {
                        DevicePreferences.TrustAll(_active.Devices);
                        StatusText.Text = string.Format(Loc("Str_St_TrustedBaseline"), _active.Devices.Count);
                    }
                    else
                    {
                        int unknown = _active.Devices.Count(device => !DevicePreferences.IsTrusted(device));
                        if (unknown > 0) StatusText.Text = string.Format(Loc("Str_St_UnknownDevices"), unknown);
                    }
                }
            }
            catch (OperationCanceledException) { if (!_disposed) StatusText.Text = Loc("Str_St_ScanCanceled"); }
            catch (Exception ex) { if (!_disposed) StatusText.Text = string.Format(Loc("Str_Err_Scan"), ex.Message); }
            finally
            {
                _active.Cts = null;
                cts.Dispose();
                bool deep = completed && _runDeepAfterScan && !_disposed;
                _runDeepAfterScan = false;
                _scanCompleted = completed;
                if (!_disposed)
                {
                    _active.Status = StatusText.Text;
                    ScanBtn.Content = Loc("Str_Btn_Scan");
                    ScanProgress.Visibility = Visibility.Collapsed;
                    ExportButton.IsEnabled = _active.Devices.Count > 0;
                    UpdateDeepScanButton();
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
                if (deep) DeepScan();
            }
        }
    }
}
