using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Models;

namespace KillerScan
{
    // Scanner core: right-click "Rescan" of one or more selected devices. Runs the deep
    // per-host probe (exhaustive ports + refreshed fingerprints) and swaps each result
    // back into the grid in place. Independent of the tab's subnet scan; disabled while
    // that tab is mid-scan so the two don't fight over the scanner's discovery state.
    public partial class MainWindow
    {
        private CancellationTokenSource? _rescanCts;

        // Set the singular/plural label and enable state just before the menu opens.
        private void ResultsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
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

            ScanProgress.Value = 0;
            ScanProgress.Visibility = Visibility.Visible;
            int done = 0;

            try
            {
                foreach (var old in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    StatusText.Text = string.Format(Loc("Str_St_Rescanning"), old.IpAddress);

                    NetworkDevice fresh;
                    try { fresh = await scanner.DeepProbeHostAsync(old.IpAddress, ct); }
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
            catch (OperationCanceledException) { StatusText.Text = Loc("Str_St_ScanCancelled"); }
            finally
            {
                _rescanCts = null;
                ScanProgress.Visibility = Visibility.Collapsed;
                RefreshDeviceCount();
            }
        }
    }
}
