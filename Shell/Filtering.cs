using System;
using System.Windows.Controls;
using KillerScan.Models;

namespace KillerScan.Shell
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
                        // Manufacturer names are never translated, but "(Randomized)" is, so the
                        // same both-ways match the Type column needs applies to this one value.
                        || (Controls.VendorConverter.Display(d.Vendor)
                                .IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                        // BOTH the stored type and the label shown in the Type column. They are
                        // the same string in English and different in every other language, and
                        // Type is the only column where the two diverge - so searching the value
                        // alone meant a German user could type the word visibly in the cell
                        // (Drucker) and get nothing, while "Printer" worked. Matching the value
                        // too keeps saved filters and scripted habits working.
                        || d.DeviceType.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || Controls.DeviceTypeConverter.Display(d.DeviceType)
                               .IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
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
