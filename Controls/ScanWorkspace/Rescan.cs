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
            TrustDeviceMenuItem.IsChecked = selected != null && DevicePreferences.IsTrusted(selected);
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

            var s = _active;
            var scanner = s.Scanner;
            _rescanCts = new CancellationTokenSource();
            var ct = _rescanCts.Token;
            UpdateDeepScanButton();
            StateChanged?.Invoke(this, EventArgs.Empty);

            ScanProgress.Value = 0;
            ScanProgress.Visibility = Visibility.Visible;
            int done = 0;
            KillerScan.Services.NetworkScanner.FlushLocalDnsCache();

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
                            old.IpAddress, ct, flushLocalDnsCache: false);
                        ct.ThrowIfCancellationRequested();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { done++; ScanProgress.Value = done * 100.0 / targets.Count; continue; }

                    int idx = s.Devices.IndexOf(old);
                    if (idx >= 0) s.Devices[idx] = fresh;

                    done++;
                    ScanProgress.Value = done * 100.0 / targets.Count;
                }
                StatusText.Text = string.Format(Loc("Str_St_RescanDone"), targets.Count);
            }
            catch (OperationCanceledException) { if (!_disposed) StatusText.Text = Loc("Str_St_ScanCanceled"); }
            finally
            {
                _rescanCts.Dispose();
                _rescanCts = null;
                FinishRescanUi();
            }
        }

        private async void DeepScanAll_Click(object sender, RoutedEventArgs e)
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
            _rescanCts = new CancellationTokenSource();
            var ct = _rescanCts.Token;
            using var hostGate = new SemaphoreSlim(8);
            int done = 0;
            int refreshed = 0;

            ScanBtn.IsEnabled = false;
            ScanProgress.Value = 0;
            ScanProgress.Visibility = Visibility.Visible;
            UpdateDeepScanButton();
            StateChanged?.Invoke(this, EventArgs.Empty);
            KillerScan.Services.NetworkScanner.FlushLocalDnsCache();

            try
            {
                var tasks = targets.Select(async old =>
                {
                    await hostGate.WaitAsync(ct);
                    try
                    {
                        var fresh = await scanner.DeepProbeHostAsync(
                            old.IpAddress, ct, 128, flushLocalDnsCache: false);
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
                if (!_disposed) StatusText.Text = string.Format(Loc("Str_St_RescanDone"), refreshed);
            }
            catch (OperationCanceledException)
            {
                if (!_disposed) StatusText.Text = Loc("Str_St_ScanCanceled");
            }
            finally
            {
                _rescanCts.Dispose();
                _rescanCts = null;
                FinishRescanUi();
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
