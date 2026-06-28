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
            int shown = _filteredView.Cast<object>().Count();
            DeviceCount.Text = CountLabel(string.IsNullOrEmpty(filter)
                ? $"{_devices.Count} device{(_devices.Count == 1 ? "" : "s")} found"
                : $"{shown} of {_devices.Count} shown");
        }
    }
}
