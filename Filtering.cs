using System;
using System.Windows.Controls;
using KillerScan.Models;

namespace KillerScan
{
    // Scanner core: live text filter over the discovered-devices grid.
    public partial class MainWindow
    {
        private void FilterInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_filteredView == null) return;
            string filter = FilterInput.Text.Trim().ToLowerInvariant();
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
                        || d.DeviceType.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                };
            RefreshDeviceCount();
        }

        /// <summary>Rebuilds the device-count readout from current state, in the active language.</summary>
        private void RefreshDeviceCount()
        {
            if (DeviceCount == null) return;
            int total = ActiveDevices?.Count ?? 0;
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
            DeviceCount.Text = CountLabel(tail);
        }
    }
}
