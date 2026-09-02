using System;
using System.Threading;
using System.Windows;
using KillerScan.Models;
using KillerScan.Services;   // AppInfo, for the message box caption

namespace KillerScan.Shell
{
    // Scanner core: drives a scan from the UI and reflects engine progress back.
    // The scan itself runs in Services/NetworkScanner; this is only the glue. ScanSession owns
    // the scanner and scan state.
    public partial class MainWindow
    {
        // Prefix the device-count readout with the scanned target summary.
        private string CountLabel(string tail) =>
            string.IsNullOrEmpty(ActiveSubnet) ? tail : $"{ActiveSubnet}  ·  {tail}";

        /// <summary>Subscribe the session's scanner engine to the UI.</summary>
        private void WireSession(ScanSession s)
        {
            // Status messages are composed inside the scan engine (it knows the counts); hand it
            // a resource lookup so they come out in the active locale. Called from the scan's
            // background thread, so the lookup marshals to the UI thread.
            s.Scanner.Localizer = key => Application.Current.Dispatcher.Invoke(
                () => Application.Current.TryFindResource(key) as string);
            s.Scanner.StatusChanged += status =>
                Dispatcher.Invoke(() => { s.Status = status; if (s == _active) StatusText.Text = status; });
            s.Scanner.ProgressChanged += pct =>
                Dispatcher.Invoke(() => { s.Progress = pct; if (s == _active) ScanProgress.Value = pct; });
            s.Scanner.DeviceFound += device =>
                Dispatcher.Invoke(() =>
                {
                    s.Devices.Add(device);
                    if (s == _active)
                        RefreshDeviceCount();
                });
        }

        private async void ScanBtn_Click(object sender, RoutedEventArgs e)
        {
            if (DemoMode) { GenerateDemoScan(); return; }   // --demo: re-roll a fake network instead of scanning

            var s = _active;

            // Stop a scan already in progress.
            if (s.Cts != null)
            {
                s.Cts.Cancel(); s.Cts = null;
                if (s == _active)
                {
                    ScanBtn.Content = Loc("Str_Btn_Scan");
                    ScanProgress.Visibility = Visibility.Collapsed;
                    ExportButton.IsEnabled = s.Devices.Count > 0;
                    UpdateDeepScanButton();
                    if (s.Devices.Count > 0)
                    {
                        if (ResultsGrid.SelectedItem == null) ResultsGrid.SelectedIndex = 0;
                        ResultsGrid.Focus();
                    }
                }
                return;
            }

            // Validate before starting: one or many comma-separated targets, each a CIDR block,
            // a single host, or a range. A bad token names itself in the status bar rather than
            // throwing out of the scan (Services/ScanTargets.cs).
            var targets = Services.ScanTargets.Parse(SubnetInput.Text);
            if (!targets.Ok)
            {
                StatusText.Text = targets.Error switch
                {
                    Services.TargetError.Invalid  => string.Format(Loc("Str_St_BadTarget"), targets.Detail),
                    Services.TargetError.TooLarge => string.Format(Loc("Str_St_TooManyAddresses"),
                                                                   targets.Detail,
                                                                   Services.ScanTargets.MaxAddresses.ToString("N0")),
                    _                             => Loc("Str_St_NoTarget"),
                };
                SubnetInput.Focus();
                SubnetInput.SelectAll();
                return;
            }

            s.SubnetText = SubnetInput.Text;
            s.Devices.Clear();
            s.ScannedSubnet = targets.Summary;
            if (s == _active)
            {
                RefreshDeviceCount();
                UpdateDeepScanButton();
                ScanProgress.Value = 0;
                ScanProgress.Visibility = Visibility.Visible;
                ExportButton.IsEnabled = false;
                ScanBtn.Content = Loc("Str_Btn_Stop");
            }
            s.Cts = new CancellationTokenSource();
            try
            {
                await s.Scanner.ScanSubnetAsync(s.SubnetText, s.Cts.Token, fullScan: true);
            }
            catch (OperationCanceledException) { s.Status = Loc("Str_St_ScanCanceled"); if (s == _active) StatusText.Text = s.Status; }
            catch (Exception ex) { MessageBox.Show(string.Format(Loc("Str_Err_Scan"), ex.Message), AppInfo.DisplayName, MessageBoxButton.OK, MessageBoxImage.Error); }
            finally
            {
                s.Cts = null;
                // Re-enable export on ANY scan end (completed, canceled, errored) when we have results.
                if (s == _active)
                {
                    ScanBtn.Content = Loc("Str_Btn_Scan");
                    ScanProgress.Visibility = Visibility.Collapsed;
                    ExportButton.IsEnabled = s.Devices.Count > 0;
                    UpdateDeepScanButton();
                }
            }
        }
    }
}
