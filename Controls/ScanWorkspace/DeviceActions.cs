using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerScan.Controls;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private NetworkDevice? GetSelectedDevice() => ResultsGrid.SelectedItem as NetworkDevice;

        private void ResultsGrid_RightClick(object sender, MouseButtonEventArgs e)
        {
            var row = ItemsControl.ContainerFromElement(ResultsGrid, e.OriginalSource as DependencyObject) as DataGridRow;
            if (row?.Item is not NetworkDevice device) { ResultsGrid.UnselectAll(); return; }
            if (!ResultsGrid.SelectedItems.Contains(device))
            {
                ResultsGrid.SelectedItems.Clear();
                ResultsGrid.SelectedItem = device;
            }
            ResultsGrid.CurrentItem = device;
            ResultsGrid.Focus();
        }

        public void WatchSelected() => Watch_Click(this, new RoutedEventArgs());

        private void CopyIp_Click(object sender, RoutedEventArgs e)
        { if (_showServices) { CopyServiceText("CopyIp"); return; } var d = GetSelectedDevice(); if (d != null) CopyDeviceText(d.IpAddress); }

        private void CopyMac_Click(object sender, RoutedEventArgs e)
        { if (_showServices) return; var d = GetSelectedDevice(); if (d != null) CopyDeviceText(d.MacAddress); }

        private void CopyHostname_Click(object sender, RoutedEventArgs e)
        { if (_showServices) { CopyServiceText("CopyHost"); return; } var d = GetSelectedDevice(); if (d != null) CopyDeviceText(d.Hostname); }

        private async void CopyDeviceText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Clipboard.SetText(text); return; }
                catch (System.Runtime.InteropServices.COMException)
                {
                    if (attempt == 4) { StatusText.Text = Loc("Str_Clipboard_Failed"); return; }
                    await System.Threading.Tasks.Task.Delay(50);
                }
            }
        }

        private void PingDevice_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Ping");
        private void OpenBrowser_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Browser");
        private void RdpDevice_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Rdp");
        private void SshDevice_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Ssh");
        private void SshAsDevice_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("SshAs");
        private void Diagnose_Click(object sender, RoutedEventArgs e) => RaiseDeviceAction("Diagnose");
        /// <summary>
        /// Keep Alive takes the whole selection, not just the focused row: watching a handful
        /// of devices together is the normal case, and adding them one at a time is busywork.
        /// The shell adds each as a target, so the order on screen is the order here.
        /// </summary>
        private void Watch_Click(object sender, RoutedEventArgs e)
        {
            var selected = ResultsGrid.SelectedItems.OfType<NetworkDevice>().ToList();
            if (selected.Count == 0) { RaiseDeviceAction("Watch"); return; }
            foreach (var device in selected)
                DeviceAction?.Invoke(this, new ScanDeviceActionEventArgs(device, "Watch", false));
        }

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
                PersistenceFeedback.Check(_location.Overrides.Set(d.MacAddress, type), _location.Overrides.Save, () => _location.Overrides.SaveError);
                // The automatic result stays on record next to the manual type.
                d.Recognition = NetworkScanner.AssessDevice(d, mac => _location.Overrides.Get(mac));
                d.DeviceType = type;
                _filteredView?.Refresh();
                if (_showTopology) RefreshTopology();
                StatusText.Text = string.Format(Loc("Str_St_OverrideSet"), d.MacAddress, type);
            }
        }

        private void ClearOverride_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null || string.IsNullOrEmpty(d.MacAddress)) return;
            PersistenceFeedback.Check(_location.Overrides.Set(d.MacAddress, null), _location.Overrides.Save, () => _location.Overrides.SaveError);
            d.Recognition = NetworkScanner.AssessDevice(d, mac => _location.Overrides.Get(mac));
            d.DeviceType = d.Recognition.Type;
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
            StatusText.Text = string.Format(Loc("Str_St_OverrideCleared"), d.MacAddress);
        }

        private void TypeCell_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (sender is not DataGridCell { DataContext: NetworkDevice device } cell) { e.Handled = true; return; }
            cell.ToolTip = RecognitionText.Tooltip(device.Recognition);
        }

        /// <summary>
        /// Shows why the selected device has its type. It only reads what the scan already
        /// recorded: opening it sends nothing to the device.
        /// </summary>
        internal void WhyType_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            var show = ShowRecognition ?? ((heading, detail) =>
                new ConfirmDialog(heading, detail, Loc("Str_Sys_Close"), string.Empty) { Owner = Window.GetWindow(this) }.ShowDialog());
            show(RecognitionText.Heading(d), RecognitionText.Detail(d));
        }

        /// <summary>
        /// Lists the selected device's endpoints from what the scan recorded. Opening the menu
        /// sends nothing; clicking an endpoint opens it with the existing browser, SSH or Remote
        /// Desktop action, or copies the address when no app handles the protocol.
        /// </summary>
        private void AccessMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, AccessMenuItem)) return;
            AccessMenuItem.Items.Clear();
            var d = GetSelectedDevice();
            var endpoints = d == null ? [] : DeviceAccess.ForDevice(d);
            if (d == null || endpoints.Count == 0)
            {
                AccessMenuItem.Items.Add(new MenuItem { Header = Loc("Str_Access_None"), IsEnabled = false, Icon = MenuGlyph.Create(0xE946) });
                return;
            }
            foreach (var endpoint in endpoints)
            {
                string? action = AccessAction(endpoint);
                var item = new MenuItem { Header = AccessWording.Item(endpoint), ToolTip = AccessWording.Tooltip(endpoint, action != null), Icon = MenuGlyph.Create(AccessGlyph(endpoint)) };
                item.Click += (_, _) => OpenAccess(d, endpoint);
                AccessMenuItem.Items.Add(item);
            }
            var copy = new MenuItem { Header = Loc("Str_Access_CopyMenu"), Icon = MenuGlyph.Create(0xE8C8) };
            foreach (var endpoint in endpoints)
            {
                string? address = DeviceAccess.Address(d.IpAddress, endpoint);
                if (address == null) continue;
                var item = new MenuItem { Header = address, Icon = MenuGlyph.Create(0xE8C8) };
                item.Click += (_, _) => CopyDeviceText(address);
                copy.Items.Add(item);
            }
            AccessMenuItem.Items.Add(copy);
        }

        // The same glyphs the device menu uses for Open in Browser, SSH and Remote Desktop.
        private static int AccessGlyph(AccessEndpoint endpoint) => endpoint.Protocol switch
        {
            "http" or "https" => 0xE8A7,
            "ssh" => 0xE756,
            "rdp" => 0xE7F4,
            _ => 0xE8C8,
        };

        private static string? AccessAction(AccessEndpoint endpoint) => endpoint.Protocol switch
        {
            "http" or "https" => "Browser",
            "ssh" => "Ssh",
            "rdp" => "Rdp",
            _ => null,
        };

        internal void OpenAccess(NetworkDevice device, AccessEndpoint endpoint)
        {
            if (AccessAction(endpoint) is { } action)
                DeviceAction?.Invoke(this, new ScanDeviceActionEventArgs(device, action, false, endpoint.Port));
            else if (DeviceAccess.Address(device.IpAddress, endpoint) is { } address)
                CopyDeviceText(address);
        }

        /// <summary>Test hook: replaces the window that shows a device's evidence.</summary>
        internal Action<string, string>? ShowRecognition { get; set; }

        private void RenameDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            var dlg = new InputDialog(
                Loc("Str_Rename_Title"),
                d.IpAddress,
                Loc("Str_Rename_Label"),
                _location.Devices.GetName(d) ?? d.Hostname,
                Loc("Str_Rename_Save"),
                Loc("Str_Btn_Cancel")) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;
            PersistenceFeedback.Check(_location.Devices.SetName(d, dlg.Value), _location.Devices.Retry, () => _location.Devices.SaveError);
            // A blank name clears the custom one, so the row shows the discovered name again.
            if (string.IsNullOrWhiteSpace(dlg.Value)) DevicePreferenceStore.Restore(d); else DevicePreferenceStore.Rename(d, dlg.Value);
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
        }

        private void ClearDeviceName_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            PersistenceFeedback.Check(_location.Devices.SetName(d, null), _location.Devices.Retry, () => _location.Devices.SaveError);
            DevicePreferenceStore.Restore(d);
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
        }

        private void TrustDevice_Click(object sender, RoutedEventArgs e)
        {
            var d = GetSelectedDevice(); if (d == null) return;
            PersistenceFeedback.Check(_location.Devices.SetTrusted(d, !_location.Devices.IsTrusted(d)), _location.Devices.Retry, () => _location.Devices.SaveError);
            ((System.Windows.Shapes.Ellipse)TrustDeviceMenuItem.Icon).Fill = (System.Windows.Media.Brush)_trustMark.Convert(
                d, typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.CurrentCulture);
            // Trust is not a property of the device, so nothing notifies the row that its gutter
            // marker has changed color. Re-reading the items is what re-runs the converter; the
            // selection is restored afterwards because refreshing the view drops it.
            ResultsGrid.Items.Refresh();
            ResultsGrid.SelectedItem = d;
        }
    }
}
