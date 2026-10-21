using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KillerScan.Services;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private LocationData _location = LocationData.Shared();
        private readonly TrustMarkConverter _trustMark = new();

        /// <summary>The name of the saved scan last loaded here, which Save updates in place.</summary>
        private string? _loadedScanName;

        /// <summary>The Location this tab's names, approvals, history and saved scans belong to.</summary>
        internal LocationData Location => _location;

        /// <summary>Raised when this tab moves to another Location, so the shell can refresh its lists.</summary>
        public event EventHandler? LocationChanged;

        /// <summary>Raised by Manage Locations, which the shell answers with its Locations section.</summary>
        public event EventHandler? ManageLocationsRequested;

        // Runs before InitializeComponent so the grid's TrustMark resource resolves to this tab's
        // converter rather than the app-wide one.
        private void InitializeLocation()
        {
            _location = LocationService.Initial();
            _trustMark.State = MarkFor;
            Resources["TrustMark"] = _trustMark;
        }

        private TrustMark MarkFor(NetworkDevice device)
        {
            if (_location.Devices.IsTrusted(device)) return TrustMark.Approved;
            if (_active.NewIdentities.Contains(DeviceIdentity.For(device))) return TrustMark.New;
            return _location.Devices.HasBaseline && !_location.Devices.IsKnown(device) ? TrustMark.New : TrustMark.Known;
        }

        /// <summary>Devices in this snapshot that are new since the Location's baseline and not approved.</summary>
        internal IReadOnlyList<NetworkDevice> NewDevices =>
            _active.Devices.Where(device => MarkFor(device) == TrustMark.New).ToList();

        private void ApplyLocationHooks()
        {
            _active.Scanner.ManualTypes = mac => _location.Overrides.Get(mac);
            _active.Scanner.Completed = device => _location.Devices.Apply(device);
            UpdateLocationLabel();
            // Present from the start so Shift+F10 and the menu key open it too.
            LocationButton.ContextMenu = BuildLocationMenu();
        }

        /// <summary>
        /// Moves this tab to another Location. Refused while scanning, because the run captured its
        /// Location when it started. Picking a Location sends no traffic: it restores that
        /// Location's last targets and settings and leaves the current results on screen.
        /// </summary>
        internal bool SetLocation(LocationData data)
        {
            if (_disposed) return false;
            if (IsScanning)
            {
                StatusText.Text = Loc("Str_Loc_Busy");
                return false;
            }
            _location = data;
            _loadedScanName = null;
            _active.NewIdentities.Clear();
            if (data.Info is { } info && LocationService.Find(info.Id) is { } current && current.LastTargets.Length > 0)
            {
                Targets = current.LastTargets;
                _active.Options = current.LastOptions ?? new ScanOptions();
            }
            LocationService.Remember(data);
            ApplyLocationHooks();
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
            LocationChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        /// <summary>The Location's current display name, after any rename.</summary>
        internal string LocationName =>
            _location.Info is { } info ? LocationService.Find(info.Id)?.Name ?? info.Name
            : _location.IsShared ? Loc("Str_Loc_Unassigned") : Loc("Str_Loc_Temporary");

        private void UpdateLocationLabel()
        {
            if (LocationLabel == null) return;
            LocationLabel.Text = LocationName;
            System.Windows.Automation.AutomationProperties.SetName(LocationButton,
                Loc("Str_Loc_Title") + ": " + LocationName);
            // The notes are a glance away on the button, so the gateway or the person to call
            // is visible without opening anything.
            string notes = _location.Info is { } info ? (LocationService.Find(info.Id)?.Notes ?? info.Notes).Trim() : string.Empty;
            LocationButton.ToolTip = notes.Length == 0 ? Loc("Str_Loc_Title") + ": " + LocationName
                : Loc("Str_Loc_Title") + ": " + LocationName + "\n\n" + notes;
        }

        private void LocationButton_Click(object sender, RoutedEventArgs e)
        {
            if (!LocationButton.IsEnabled) return;
            var menu = BuildLocationMenu();
            LocationButton.ContextMenu = menu;
            menu.IsOpen = true;
        }

        private void LocationButton_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;
            LocationButton_Click(sender, new RoutedEventArgs());
        }

        private MenuItem MenuText(string key, Action click, bool enabled = true)
        {
            var item = new MenuItem { IsEnabled = enabled };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
            item.Click += (_, _) => click();
            return item;
        }

        private ContextMenu BuildLocationMenu()
        {
            var menu = new ContextMenu { PlacementTarget = LocationButton, Placement = PlacementMode.Bottom, VerticalOffset = 4 };
            foreach (var info in LocationService.Items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var captured = info;
                var item = new MenuItem
                {
                    Header = info.ToString(),
                    IsCheckable = true,
                    IsChecked = _location.Info?.Id == info.Id
                };
                item.Click += (_, _) => SetLocation(LocationService.Get(captured));
                menu.Items.Add(item);
            }
            if (LocationService.Loaded && !DemoMode)
            {
                var temporary = new MenuItem { IsCheckable = true, IsChecked = _location.IsTemporary };
                temporary.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Loc_Temporary");
                temporary.Click += (_, _) => SetLocation(LocationData.Temporary());
                menu.Items.Add(temporary);
            }

            var saved = _location.SavedScans.Items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (saved.Count > 0)
            {
                menu.Items.Add(new Separator());
                var heading = new MenuItem { IsEnabled = false };
                heading.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Loc_SavedScans");
                menu.Items.Add(heading);
                foreach (var scan in saved)
                {
                    var captured = scan;
                    var item = new MenuItem { Header = scan.Name, InputGestureText = scan.Target };
                    item.Click += (_, _) => LoadSavedScan(captured);
                    menu.Items.Add(item);
                }
            }

            menu.Items.Add(new Separator());
            bool editable = !IsScanning && (LocationService.Loaded || _location.IsShared);
            menu.Items.Add(MenuText("Str_Loc_SaveScan", SaveScan, editable && _loadedScanName != null));
            menu.Items.Add(MenuText("Str_Loc_SaveAs", SaveScanAs, editable));
            if (LocationService.Editable && !DemoMode)
            {
                bool own = _location.Info != null && !_location.IsShared;
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuText("Str_Loc_New", CreateLocation, !IsScanning));
                menu.Items.Add(MenuText("Str_Loc_Rename", RenameLocation, own));
                menu.Items.Add(MenuText("Str_Loc_Notes", EditNotes, own));
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuText("Str_Loc_Export", ExportLocation, own));
                menu.Items.Add(MenuText("Str_Loc_Import", ImportLocation, !IsScanning));
                menu.Items.Add(MenuText("Str_Loc_Manage", () => ManageLocationsRequested?.Invoke(this, EventArgs.Empty)));
            }
            return menu;
        }

        // ── Suggesting a Location ──────────────────────────────────────────────────────────
        // A completed scan records the gateway's MAC in its Location. When the machine is on a
        // network whose gateway MAC a Location has seen, a banner offers it. Nothing switches by
        // itself, and a dismissed gateway is not offered again until the app restarts.

        private static readonly HashSet<string> DismissedGateways = new(StringComparer.OrdinalIgnoreCase);
        private LocationInfo? _suggested;
        private string _suggestedGateway = string.Empty;

        private void InitializeLocationSuggestions()
        {
            Loaded += (_, _) => CheckLocationSuggestion();
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        }

        private void DisposeLocationSuggestions() =>
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;

        private void OnNetworkAddressChanged(object? sender, EventArgs e) =>
            Dispatcher.BeginInvoke(new Action(CheckLocationSuggestion));

        private void RecordGateway(LocationData location)
        {
            if (location.Info is not { } info || location.IsShared) return;
            string gateway = LocalNetwork.Detect()?.Gateway ?? string.Empty;
            if (gateway.Length == 0) return;
            var device = _active.Devices.FirstOrDefault(item => item.IpAddress == gateway && item.MacAddress.Length > 0);
            if (device != null) LocationService.AddGatewayMac(info, device.MacAddress);
        }

        private async void CheckLocationSuggestion()
        {
            if (_disposed || DemoMode || !LocationService.Editable) return;
            string gateway = LocalNetwork.Detect()?.Gateway ?? string.Empty;
            string mac = string.Empty;
            if (System.Net.IPAddress.TryParse(gateway, out var address))
            {
                try { mac = await System.Threading.Tasks.Task.Run(() => NetworkPlatform.Current.ResolveMac(address)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { mac = string.Empty; }
            }
            if (_disposed) return;
            ShowLocationSuggestion(mac);
        }

        internal void ShowLocationSuggestion(string gatewayMac)
        {
            var matches = LocationCatalog.Matching(LocationService.Items, gatewayMac)
                .Where(item => item.Id != _location.Info?.Id).ToList();
            bool show = matches.Count > 0 && !DismissedGateways.Contains(LocationInfo.NormalizeMac(gatewayMac))
                && !LocationCatalog.Matching(LocationService.Items, gatewayMac).Any(item => item.Id == _location.Info?.Id);
            _suggestedGateway = LocationInfo.NormalizeMac(gatewayMac);
            _suggested = show && matches.Count == 1 ? matches[0] : null;
            LocationSuggestion.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;
            LocationSuggestionText.Text = matches.Count == 1
                ? string.Format(Loc("Str_Loc_Suggest"), matches[0].Name)
                : string.Format(Loc("Str_Loc_SuggestMany"), string.Join(", ", matches.Select(item => item.Name)));
            // With more than one match, the picker is the way to choose; nothing is preselected.
            LocationSuggestionSwitch.Visibility = _suggested != null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void LocationSuggestionSwitch_Click(object sender, RoutedEventArgs e)
        {
            if (_suggested == null) return;
            if (SetLocation(LocationService.Get(_suggested))) LocationSuggestion.Visibility = Visibility.Collapsed;
        }

        private void LocationSuggestionDismiss_Click(object sender, RoutedEventArgs e)
        {
            if (_suggestedGateway.Length > 0) DismissedGateways.Add(_suggestedGateway);
            LocationSuggestion.Visibility = Visibility.Collapsed;
        }

        private void EditNotes()
        {
            if (_location.Info is not { } info) return;
            string current = LocationService.Find(info.Id)?.Notes ?? info.Notes;
            var dialog = new InputDialog(Loc("Str_Loc_Notes"), LocationName, Loc("Str_Loc_NotesLabel"), current,
                Loc("Str_Rename_Save"), Loc("Str_Btn_Cancel")) { Owner = Window.GetWindow(this) };
            dialog.UseMultipleLines();
            dialog.ShowDialog();
            if (!dialog.Confirmed) return;
            if (!LocationService.SetNotes(info, dialog.Value ?? string.Empty))
                PersistenceFeedback.Check(false, () => LocationService.SetNotes(info, dialog.Value ?? string.Empty), () => LocationService.SaveError);
            UpdateLocationLabel();
        }

        private void ExportLocation()
        {
            if (_location.Info is not { } info) return;
            var choice = new ConfirmDialog(Loc("Str_Loc_Export"), Loc("Str_Loc_ExportDetail"), Loc("Str_Loc_ExportButton"),
                Loc("Str_Btn_Cancel"), Loc("Str_Loc_ExportUsernames"), false) { Owner = Window.GetWindow(this) };
            choice.ShowDialog();
            if (!choice.Confirmed) return;
            bool usernames = ((CheckBox)choice.FindName("Check1")).IsChecked == true;
            var dialog = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Loc_FileFilter") + "|*" + LocationTransfer.Extension,
                FileName = SafeFileName(LocationName) + LocationTransfer.Extension,
                DefaultExt = LocationTransfer.Extension,
                AddExtension = true
            };
            if (ShowExportDialog(dialog) != true) return;
            try
            {
                var bytes = LocationService.Export(info, usernames) ?? throw new IOException(LocationService.LoadError ?? "Locations are unavailable.");
                ExportFile.Write(dialog.FileName, stream => stream.Write(bytes, 0, bytes.Length));
                StatusText.Text = string.Format(Loc("Str_St_Exported"), System.IO.Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                MessageBox.Show(Window.GetWindow(this), string.Format(Loc("Str_Err_Export"), ex.Message), AppInfo.DisplayName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportLocation()
        {
            var dialog = new FileDialog(FileDialogMode.Open) { Filter = Loc("Str_Loc_FileFilter") + "|*" + LocationTransfer.Extension };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            byte[] bytes;
            try { bytes = System.IO.File.ReadAllBytes(dialog.FileName); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowImportError(ex.Message); return; }
            if (!LocationService.Import(bytes, out var imported, out string? error) || imported == null)
            {
                ShowImportError(error ?? string.Empty);
                return;
            }
            SetLocation(LocationService.Get(imported));
            StatusText.Text = string.Format(Loc("Str_Loc_Imported"), imported.Name);
            // Same name as an existing Location is allowed, so offer a rename straight away.
            if (LocationService.Items.Count(item => string.Equals(item.Name, imported.Name, StringComparison.CurrentCultureIgnoreCase)) > 1)
                RenameLocation();
        }

        private void ShowImportError(string message) =>
            MessageBox.Show(Window.GetWindow(this), string.Format(Loc("Str_Loc_ImportFailed"), message), AppInfo.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);

        private static string SafeFileName(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            string clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return clean.Length == 0 ? "Location" : clean;
        }

        /// <summary>Loads a saved scan's targets and settings without starting it.</summary>
        internal void LoadSavedScan(ScanProfile scan)
        {
            if (_disposed || IsScanning) return;
            LoadProfile(scan);
            _loadedScanName = scan.Name;
            FocusTargets();
        }

        private string? AskName(string titleKey, string current)
        {
            var dialog = new InputDialog(Loc(titleKey), LocationName, Loc("Str_Rename_Label"), current,
                Loc("Str_Rename_Save"), Loc("Str_Btn_Cancel")) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
            return dialog.Confirmed && !string.IsNullOrWhiteSpace(dialog.Value) ? dialog.Value.Trim() : null;
        }

        private void SaveScan()
        {
            if (_loadedScanName == null) { SaveScanAs(); return; }
            var store = _location.SavedScans;
            PersistenceFeedback.Check(store.SaveEdited(_loadedScanName, Targets.Trim(), _active.Options), store.Retry, () => store.SaveError);
            LocationChanged?.Invoke(this, EventArgs.Empty);
        }

        private void SaveScanAs()
        {
            if (AskName("Str_Loc_SaveAs", _loadedScanName ?? string.Empty) is not { } name) return;
            var store = _location.SavedScans;
            bool exists = store.Items.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (exists && !string.Equals(name, _loadedScanName, StringComparison.OrdinalIgnoreCase))
            {
                var confirm = new ConfirmDialog(Loc("Str_Loc_SaveAs"), string.Format(Loc("Str_Loc_ReplaceScan"), name),
                    Loc("Str_Rename_Save"), Loc("Str_Btn_Cancel")) { Owner = Window.GetWindow(this) };
                confirm.ShowDialog();
                if (!confirm.Confirmed) return;
            }
            PersistenceFeedback.Check(store.SaveEdited(name, Targets.Trim(), _active.Options), store.Retry, () => store.SaveError);
            _loadedScanName = name;
            LocationChanged?.Invoke(this, EventArgs.Empty);
        }

        private void CreateLocation()
        {
            if (AskName("Str_Loc_NewTitle", string.Empty) is not { } name) return;
            if (LocationService.Create(name, out var created) && created != null)
                SetLocation(LocationService.Get(created));
            else
                PersistenceFeedback.Check(false, () => false, () => LocationService.SaveError ?? LocationService.LoadError);
        }

        private void RenameLocation()
        {
            if (_location.Info is not { } info || AskName("Str_Loc_Rename", LocationName) is not { } name) return;
            if (!LocationService.Rename(info, name))
                PersistenceFeedback.Check(false, () => LocationService.Rename(info, name), () => LocationService.SaveError);
            UpdateLocationLabel();
            LocationChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
