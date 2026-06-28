using System;
using System.Threading;
using System.Windows;

namespace KillerScan
{
    // Scanner core: drives a scan from the UI and reflects engine progress back.
    // The scan itself runs in Services/NetworkScanner; this is only the glue. Each tab
    // (ScanSession) owns its own scanner, so background tabs keep scanning; UI writes are
    // gated to the active tab.
    public partial class MainWindow
    {
        // Prefix the device-count readout with the active tab's scanned subnet.
        private string CountLabel(string tail) =>
            string.IsNullOrEmpty(ActiveSubnet) ? tail : $"{ActiveSubnet}  ·  {tail}";

        /// <summary>Subscribe a session's scanner engine to the UI (only writes when that tab is active).</summary>
        private void WireSession(ScanSession s)
        {
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
            var s = _active;

            // Stop a scan already running on this tab.
            if (s.Cts != null)
            {
                s.Cts.Cancel(); s.Cts = null;
                if (s == _active)
                {
                    ScanBtn.Content = Loc("Str_Btn_Scan");
                    ScanProgress.Visibility = Visibility.Collapsed;
                    ExportButton.IsEnabled = s.Devices.Count > 0;
                }
                return;
            }

            s.SubnetText = SubnetInput.Text;
            s.Devices.Clear();
            s.ScannedSubnet = SubnetInput.Text.Trim();           // updates the tab caption
            if (s == _active)
            {
                RefreshDeviceCount();
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
            catch (OperationCanceledException) { s.Status = "Scan cancelled"; if (s == _active) StatusText.Text = "Scan cancelled"; }
            catch (Exception ex) { MessageBox.Show($"Scan error: {ex.Message}", "KillerScan", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally
            {
                s.Cts = null;
                // Re-enable export on ANY scan end (completed, cancelled, errored) when we have results.
                if (s == _active)
                {
                    ScanBtn.Content = Loc("Str_Btn_Scan");
                    ScanProgress.Visibility = Visibility.Collapsed;
                    ExportButton.IsEnabled = s.Devices.Count > 0;
                }
            }
        }
    }
}
