using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private CancellationTokenSource? _rescanCts;

        private void UpdateDeepScanButton()
        {
            ScanOptionsButton.IsEnabled = !IsScanning;
            LocationButton.IsEnabled = !IsScanning;
            if (IsScanning && LocationButton.ContextMenu != null) LocationButton.ContextMenu.IsOpen = false;
            if (IsScanning && ScanOptionsButton.ContextMenu != null)
                ScanOptionsButton.ContextMenu.IsOpen = false;
            if (DeepScanAllButton == null) return;
            DeepScanAllButton.Visibility = !DemoMode && ActiveDevices.Count > 0 && _active.Cts == null
                ? Visibility.Visible
                : Visibility.Collapsed;
            DeepScanAllButton.Content = Loc(_rescanCts == null
                ? "Str_Btn_DeepScanAll"
                : "Str_Btn_Stop");
        }

        private void ResultsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            PrepareDeviceContextMenu();
        }

        private void PrepareDeviceContextMenu()
        {
            if (RescanMenuItem == null) return;
            int n = ResultsGrid.SelectedItems.Count;
            RescanMenuItem.Header = Loc(n > 1 ? "Str_Ctx_RescanMany" : "Str_Ctx_RescanOne");
            RescanMenuItem.IsEnabled = n > 0 && _rescanCts == null && _active.Cts == null;
            var selected = GetSelectedDevice();
            foreach (var item in ResultsGrid.ContextMenu.Items.OfType<MenuItem>())
                if (item != RescanMenuItem) item.IsEnabled = selected != null;
            ((System.Windows.Shapes.Ellipse)TrustDeviceMenuItem.Icon).Fill = (System.Windows.Media.Brush)_trustMark.Convert(
                selected!, typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.CurrentCulture);
            TrustDeviceMenuItem.IsEnabled = selected != null;
        }

        private async void RescanSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_disposed || _rescanCts != null || _active.Cts != null) return;

            var targets = ResultsGrid.SelectedItems
                .Cast<NetworkDevice>()
                .OrderBy(d => d.IpSortKey)
                .ToList();
            if (targets.Count == 0) return;
            var refreshTargets = ScanTargets.Parse(string.Join(",", targets.Select(d => d.IpAddress)));
            if (!refreshTargets.Ok || !AdapterAllowsScan(refreshTargets.Summary, refreshTargets.Addresses, out _)) return;

            var s = _active;
            var scanner = s.Scanner;
            _rescanCts = new CancellationTokenSource();
            var ct = _rescanCts.Token;
            UpdateDeepScanButton();
            StateChanged?.Invoke(this, EventArgs.Empty);

            ScanProgress.Value = 0;
            ScanProgress.Visibility = Visibility.Visible;
            int done = 0, refreshed = 0;
            bool canceled = false;
            var options = s.Options;
            if (options.ReverseDns) NetworkScanner.FlushLocalDnsCache();

            try
            {
                foreach (var old in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    StatusText.Text = string.Format(Loc("Str_St_Rescanning"), old.IpAddress);

                    NetworkDevice fresh;
                    try
                    {
                        fresh = await scanner.DeepProbeHostAsync(
                            old.IpAddress, ct, options, flushLocalDnsCache: false);
                        ct.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { done++; ScanProgress.Value = done * 100.0 / targets.Count; continue; }

                    int idx = s.Devices.IndexOf(old);
                    if (idx >= 0) { s.Devices[idx] = fresh; refreshed++; }

                    done++;
                    ScanProgress.Value = done * 100.0 / targets.Count;
                }
                StatusText.Text = RescanSummary(refreshed, targets.Count);
            }
            catch (OperationCanceledException) { canceled = true; if (!_disposed) StatusText.Text = Loc("Str_St_ScanCanceled"); }
            finally
            {
                NoteRefresh(s, refreshed, targets.Count - refreshed, canceled);
                _rescanCts.Dispose();
                _rescanCts = null;
                FinishRescanUi();
            }
        }

        private async void DeepScanAll_Click(object sender, RoutedEventArgs e)
            => await DeepScanAllAsync();

        // "N refreshed" only when every host answered; otherwise how many did and did not.
        private string RescanSummary(int refreshed, int total) => refreshed == total
            ? string.Format(Loc("Str_St_RescanDone"), refreshed)
            : string.Format(Loc("Str_St_RescanPartial"), refreshed, total, total - refreshed);

        // Manual refreshes keep the scan's own time and completion and are noted beside them.
        // Canceled hosts that were never reached count as not refreshed.
        private static void NoteRefresh(ScanSession session, int refreshed, int failed, bool canceled)
        {
            if (refreshed == 0 && failed == 0) return;
            var now = DateTimeOffset.Now;
            session.Refresh = session.Refresh?.Then(now, refreshed, failed, canceled)
                ?? new ScanRefresh(now, refreshed, failed, canceled);
        }

        private async Task DeepScanAllAsync(bool recordProfile = false)
        {
            if (_rescanCts != null)
            {
                _rescanCts.Cancel();
                return;
            }
            if (_disposed || _active.Cts != null || ActiveDevices.Count == 0) return;

            var s = _active;
            var scanner = s.Scanner;
            var targets = s.Devices.OrderBy(d => d.IpSortKey).ToList();
            // The automatic deep phase belongs to the run that already validated its adapter.
            if (!recordProfile)
            {
                var refreshTargets = ScanTargets.Parse(string.Join(",", targets.Select(d => d.IpAddress)));
                if (!refreshTargets.Ok || !AdapterAllowsScan(refreshTargets.Summary, refreshTargets.Addresses, out _)) return;
            }
            _rescanCts = new CancellationTokenSource();
            var ct = _rescanCts.Token;
            using var hostGate = new SemaphoreSlim(8);
            int done = 0;
            int refreshed = 0;
            var completion = ScanCompletion.Partial;
            var options = s.Options;

            ScanBtn.IsEnabled = false;
            ScanProgress.Value = 0;
            ScanProgress.Visibility = Visibility.Visible;
            UpdateDeepScanButton();
            StateChanged?.Invoke(this, EventArgs.Empty);
            try
            {
                if (options.ReverseDns) NetworkScanner.FlushLocalDnsCache();
                var tasks = targets.Select(async old =>
                {
                    await hostGate.WaitAsync(ct);
                    try
                    {
                        var fresh = await scanner.DeepProbeHostAsync(
                            old.IpAddress, ct, options, 128, flushLocalDnsCache: false);
                        ct.ThrowIfCancellationRequested();
                        int idx = s.Devices.IndexOf(old);
                        if (idx >= 0)
                        {
                            s.Devices[idx] = fresh;
                            refreshed++;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { }
                    finally
                    {
                        hostGate.Release();
                        done++;
                        if (!_disposed)
                        {
                            ScanProgress.Value = done * 100.0 / targets.Count;
                            StatusText.Text = string.Format(
                                Loc("Str_St_Rescanning"), $"{done}/{targets.Count}");
                        }
                    }
                });

                await Task.WhenAll(tasks);
                completion = refreshed == targets.Count ? ScanCompletion.Complete : ScanCompletion.Partial;
                if (!_disposed) StatusText.Text = RescanSummary(refreshed, targets.Count);
            }
            catch (OperationCanceledException)
            {
                completion = ScanCompletion.Canceled;
                if (!_disposed) StatusText.Text = Loc("Str_St_ScanCanceled");
            }
            catch (Exception ex)
            {
                if (!_disposed) StatusText.Text = string.Format(Loc("Str_Err_Scan"), ex.Message);
            }
            finally
            {
                try
                {
                    if (recordProfile)
                    {
                        _scanCompleted = completion == ScanCompletion.Complete;
                        LeaveDeletedLocation();
                        RecordCompletedRun(options, deep: true, completion);
                    }
                    // A manual Deep Scan All refreshes an existing snapshot; an automatic one is
                    // part of the run it just recorded.
                    else NoteRefresh(s, refreshed, targets.Count - refreshed, completion == ScanCompletion.Canceled);
                }
                finally
                {
                    _rescanCts.Dispose();
                    _rescanCts = null;
                    FinishRescanUi();
                }
            }
        }

        private void FinishRescanUi()
        {
            if (_disposed) return;
            ScanBtn.IsEnabled = true;
            ScanProgress.Visibility = Visibility.Collapsed;
            RefreshDeviceCount();
            UpdateDeepScanButton();
            _active.Status = StatusText.Text;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
