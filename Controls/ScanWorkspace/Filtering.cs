using System;
using System.Windows.Controls;
using KillerScan.Models;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private void FilterInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_filteredView == null) return;
            string filter = FilterInput.Text.Trim().ToLowerInvariant();
            FilterToggleButton.SetResourceReference(Control.ForegroundProperty,
                filter.Length > 0 ? "PrimaryBrush" : "TextBrush");
            FilterToggleButton.FontWeight = filter.Length > 0
                ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal;
            if (filter.Length > 0) FilterBox.Visibility = System.Windows.Visibility.Visible;
            if (string.IsNullOrEmpty(filter))
                _filteredView.Filter = null;
            else
                _filteredView.Filter = obj =>
                {
                    if (obj is not NetworkDevice d) return false;
                    return d.IpAddress.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || d.Hostname.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || d.MacAddress.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || d.Vendor.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || (Controls.VendorConverter.Display(d.Vendor)
                                .IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        || d.DeviceType.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || Controls.DeviceTypeConverter.Display(d.DeviceType)
                               .IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                };
            RefreshDeviceCount();
            if (_showTopology) RefreshTopology();
            if (_showServices) RefreshServices();
        }

        private void RefreshDeviceCount()
        {
            if (DeviceCount == null) return;
            int total = ActiveDevices?.Count ?? 0;
            // "0 devices found" before anything has been scanned is not a result, it is the
            // absence of one, and it reads as a failed scan sitting next to Ready. The cell earns
            // its place once there is a count to report; a completed scan that genuinely found
            // nothing still says so, because there the zero is the finding.
            if (total == 0 && !_scanCompleted)
            {
                DeviceCount.Text = string.Empty;
                return;
            }
            string flt = FilterInput?.Text.Trim() ?? "";
            string tail;
            if (!string.IsNullOrEmpty(flt) && _filteredView != null)
            {
                int shown = _filteredView.Cast<object>().Count();
                tail = string.Format(Loc("Str_Count_Shown"), shown, total);
            }
            else
            {
                tail = string.Format(Loc("Str_Count_Found"), total);
            }
            DeviceCount.Text = tail;
        }
    }
}
