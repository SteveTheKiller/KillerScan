using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Shell
{
    // Scanner core: right-click actions on a discovered device (copy, ping, open,
    // remote in, and device-type overrides).
    public partial class MainWindow
    {
        private NetworkDevice? GetSelectedDevice() => ResultsGrid.SelectedItem as NetworkDevice;

        private void CopyIp_Click(object sender, RoutedEventArgs e)
        { var d = GetSelectedDevice(); if (d != null) Clipboard.SetText(d.IpAddress); }

        private void CopyMac_Click(object sender, RoutedEventArgs e)
        { var d = GetSelectedDevice(); if (d != null && !string.IsNullOrEmpty(d.MacAddress)) Clipboard.SetText(d.MacAddress); }

        private void CopyHostname_Click(object sender, RoutedEventArgs e)
        { var d = GetSelectedDevice(); if (d != null && !string.IsNullOrEmpty(d.Hostname)) Clipboard.SetText(d.Hostname); }

        private void PingDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            Process.Start(new ProcessStartInfo("cmd", $"/c ping -n 4 {d.IpAddress} & pause") { UseShellExecute = true });
        }

        private void OpenBrowser_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            Process.Start(new ProcessStartInfo($"http://{d.IpAddress}") { UseShellExecute = true });
        }

        private void RdpDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            Process.Start(new ProcessStartInfo("mstsc", $"/v:{d.IpAddress}") { UseShellExecute = true });
        }

        // ============================================================
        // SSH
        //
        // The username is NOT assumed. `ssh <ip>` with no user makes the Windows client fall back
        // to the logged-on account name, which is wrong on most of what a scan turns up - a UniFi
        // box, a switch and an appliance each want their own vendor's account, and none of them is
        // the name you sign in to Windows with. So the first connection to a device asks, the
        // answer is remembered against that device's MAC (Services/DeviceLogins.cs), and every
        // connection after that goes straight through.
        //
        // Leaving the box empty is a real answer, kept as such: it clears any remembered name and
        // launches plain `ssh <ip>`, which is the pre-1.6.0 behavior for anyone who wants it.
        // ============================================================

        /// <summary>Right-click SSH: connect with the remembered username, asking once if this
        /// device has never been connected to.</summary>
        private void SshDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;

            // null means never asked, which is not the same as asked-and-left-blank: a stored
            // empty string is a deliberate "no username", and must not re-prompt every time.
            string? remembered = DeviceLogins.Get(d.MacAddress, d.IpAddress);
            if (remembered is not null) { LaunchSsh(d.IpAddress, remembered); return; }

            if (!AskSshUser(d, string.Empty, out string user)) return;
            DeviceLogins.Set(d.MacAddress, d.IpAddress, user);
            LaunchSsh(d.IpAddress, user);
        }

        /// <summary>Right-click SSH as...: always asks, pre-filled with whatever is remembered, so
        /// a device can be reached as a different account or its stored name corrected.</summary>
        private void SshAsDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;

            var remembered = DeviceLogins.Get(d.MacAddress, d.IpAddress) ?? string.Empty;
            if (!AskSshUser(d, remembered, out string user)) return;
            DeviceLogins.Set(d.MacAddress, d.IpAddress, user);
            LaunchSsh(d.IpAddress, user);
        }

        /// <summary>Prompts for the username. Returns false when the dialog was canceled, which
        /// must not launch anything and must not overwrite what is already remembered.</summary>
        private bool AskSshUser(NetworkDevice d, string initial, out string user)
        {
            // The hostname is friendlier than the IP when there is one, and the heading is the only
            // place the dialog says which device is being connected to.
            string who = string.IsNullOrEmpty(d.Hostname) ? d.IpAddress : $"{d.Hostname} ({d.IpAddress})";

            var dlg = new InputDialog(
                string.Format(Loc("Str_Ssh_Head"), who),
                Loc("Str_Ssh_Detail"),
                Loc("Str_Ssh_User"),
                initial,
                Loc("Str_Ssh_Connect"),
                Loc("Str_Btn_Cancel")) { Owner = this };
            dlg.ShowDialog();

            user = dlg.Value;
            return dlg.Confirmed;
        }

        /// <summary>Hands off to the system ssh client. A blank username means no user@ prefix, so
        /// the client applies its own config (~/.ssh/config, and its default) exactly as before.</summary>
        private static void LaunchSsh(string ip, string user)
        {
            string target = string.IsNullOrWhiteSpace(user) ? ip : $"{user}@{ip}";
            Process.Start(new ProcessStartInfo("cmd", $"/c ssh {target}") { UseShellExecute = true });
        }

        /// <summary>
        /// Tick the type the selected device currently has. Recomputed each time the submenu opens
        /// rather than tracked, because the type can change underneath it - a rescan reclassifies,
        /// and Clear Override reverts to the auto-detected type. This reflects the device's
        /// EFFECTIVE type, whether that came from the classifier or from a manual override.
        /// </summary>
        private void SetTypeMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem parent) return;
            var d = GetSelectedDevice();
            foreach (object o in parent.Items)
            {
                if (o is not MenuItem mi || mi.Tag is not string type) continue;
                mi.IsChecked = d != null &&
                               string.Equals(type, d.DeviceType, StringComparison.OrdinalIgnoreCase);
            }
        }

        private void SetType_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null || string.IsNullOrEmpty(d.MacAddress)) return;
            if (sender is MenuItem mi && mi.Tag is string type)
            {
                DeviceOverrides.Set(d.MacAddress, type);
                d.DeviceType = type;
                _filteredView?.Refresh();
                if (_showTopology) RefreshTopology();
                StatusText.Text = string.Format(Loc("Str_St_OverrideSet"), d.MacAddress, type);
            }
        }

        private void ClearOverride_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null || string.IsNullOrEmpty(d.MacAddress)) return;
            DeviceOverrides.Set(d.MacAddress, null);
            // Reclassify without override
            d.DeviceType = NetworkScanner.ClassifyDevice(d);
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
            StatusText.Text = string.Format(Loc("Str_St_OverrideCleared"), d.MacAddress);
        }

        private void RenameDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            var dlg = new InputDialog(
                Loc("Str_Rename_Title"),
                d.IpAddress,
                Loc("Str_Rename_Label"),
                DevicePreferences.GetName(d) ?? d.Hostname,
                Loc("Str_Rename_Save"),
                Loc("Str_Btn_Cancel")) { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;
            DevicePreferences.SetName(d, dlg.Value);
            if (!string.IsNullOrWhiteSpace(dlg.Value)) d.Hostname = dlg.Value.Trim();
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
        }

        private void ClearDeviceName_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            DevicePreferences.SetName(d, null);
            d.Hostname = string.IsNullOrWhiteSpace(d.NetbiosName) ? string.Empty : d.NetbiosName;
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
        }

        private void TrustDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            DevicePreferences.SetTrusted(d, !DevicePreferences.IsTrusted(d));
            TrustDeviceMenuItem.IsChecked = DevicePreferences.IsTrusted(d);
        }
    }
}
