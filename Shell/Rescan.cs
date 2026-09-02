using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Shell
{
    // Scanner core: right-click "Rescan" of one or more selected devices. Runs the deep
    // per-host probe (exhaustive ports + refreshed fingerprints) and swaps each result
    // back into the grid in place. Disabled during a subnet scan so the two operations do not
    // fight over the scanner's discovery state.
    public partial class MainWindow
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

        // Set the singular/plural label and enable state just before the menu opens.
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
        }

        private async void RescanSelected_Click(object sender, RoutedEventArgs e)
        {
            if (_rescanCts != null || _active.Cts != null) return;

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

            ScanProgress.Value = 0;
            ScanProgress.Visibility = Visibility.Visible;
            int done = 0;
            NetworkScanner.FlushLocalDnsCache();

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
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { done++; ScanProgress.Value = done * 100.0 / targets.Count; continue; }

                    // Swap the refreshed record into the collection in place. Same IP -> same
                    // sort slot, so the row updates without duplicating or reordering. (Only
                    // safe because rescan is blocked while a subnet scan is mutating the list.)
                    int idx = s.Devices.IndexOf(old);
                    if (idx >= 0) s.Devices[idx] = fresh;

                    done++;
                    ScanProgress.Value = done * 100.0 / targets.Count;
                }
                StatusText.Text = string.Format(Loc("Str_St_RescanDone"), targets.Count);
            }
            catch (OperationCanceledException) { StatusText.Text = Loc("Str_St_ScanCanceled"); }
            finally
            {
                _rescanCts = null;
                ScanProgress.Visibility = Visibility.Collapsed;
                RefreshDeviceCount();
                UpdateDeepScanButton();
            }
        }

        private async void DeepScanAll_Click(object sender, RoutedEventArgs e)
        {
            if (_rescanCts != null)
            {
                _rescanCts.Cancel();
                return;
            }
            if (_active.Cts != null || ActiveDevices.Count == 0) return;

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
            NetworkScanner.FlushLocalDnsCache();

            try
            {
                var tasks = targets.Select(async old =>
                {
                    await hostGate.WaitAsync(ct);
                    try
                    {
                        var fresh = await scanner.DeepProbeHostAsync(
                            old.IpAddress, ct, 128, flushLocalDnsCache: false);
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
                        ScanProgress.Value = done * 100.0 / targets.Count;
                        StatusText.Text = string.Format(
                            Loc("Str_St_Rescanning"), $"{done}/{targets.Count}");
                    }
                });

                await Task.WhenAll(tasks);
                StatusText.Text = string.Format(Loc("Str_St_RescanDone"), refreshed);
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = Loc("Str_St_ScanCanceled");
            }
            finally
            {
                _rescanCts.Dispose();
                _rescanCts = null;
                ScanBtn.IsEnabled = true;
                ScanProgress.Visibility = Visibility.Collapsed;
                RefreshDeviceCount();
                UpdateDeepScanButton();
            }
        }
    }
}
