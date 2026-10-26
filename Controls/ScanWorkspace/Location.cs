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
            _inCatalog = _location.Info is { } initial && LocationService.Find(initial.Id) != null;
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
        /// Location's last targets and settings. The results on screen are cleared, because
        /// renaming or approving a row would otherwise write into the new Location. A completed
        /// run is in its own Location's history; a Temporary scan's results are nowhere else,
        /// so leaving them asks first. Picking the Location the tab is already in changes nothing.
        /// </summary>
        internal bool SetLocation(LocationData data) => SetLocation(data, keepResults: false);

        /// <summary>Asks whether to discard a Temporary scan's results. Tests answer it without a dialog.</summary>
        internal Func<string, bool>? ConfirmDiscard { get; set; }

        private bool IsCurrentLocation(LocationData data) =>
            ReferenceEquals(data, _location)
            || (data.Info != null && _location.Info != null && data.Info.Id == _location.Info.Id)
            || (data.IsTemporary && _location.IsTemporary)
            || (data.IsShared && _location.IsShared && data.Info == null && _location.Info == null);

        private bool SetLocation(LocationData data, bool keepResults)
        {
            if (_disposed) return false;
            if (IsScanning)
            {
                StatusText.Text = Loc("Str_Loc_Busy");
                return false;
            }
            if (IsCurrentLocation(data)) return true;
            bool clear = !keepResults && _active.Devices.Count > 0;
            if (clear && _location.IsTemporary)
            {
                string message = Loc("Str_Loc_DiscardTemporary");
                bool discard;
                if (ConfirmDiscard != null) discard = ConfirmDiscard(message);
                else
                {
                    var confirm = new ConfirmDialog(Loc("Str_Loc_DiscardTitle"), message, Loc("Str_Loc_DiscardConfirm"), Loc("Str_Btn_Cancel"))
                        { Owner = Window.GetWindow(this) };
                    confirm.ShowDialog();
                    discard = confirm.Confirmed;
                }
                if (!discard) return false;
            }
            if (clear) ClearResults();
            EnterLocation(data);
            return true;
        }

        private bool _inCatalog;

        private void EnterLocation(LocationData data)
        {
            _inCatalog = data.Info is { } entered && LocationService.Find(entered.Id) != null;
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
            RefreshLocationSuggestion();
            _filteredView?.Refresh();
            if (_showTopology) RefreshTopology();
            LocationChanged?.Invoke(this, EventArgs.Empty);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Moves the tab to a Temporary scan when its Location was deleted, by another window or
        /// another KillerScan, since the tab opened it. Without this, every later rename, approval
        /// or recorded scan would quietly recreate the deleted Location's folder, where nothing
        /// lists it. The results stay on screen, so they can still be saved to a Location.
        /// Returns true when the tab moved.
        /// </summary>
        internal bool LeaveDeletedLocation()
        {
            // Only a Location from this process's catalog can be judged against that catalog on disk.
            if (_disposed || !_inCatalog || _location.Info is not { } info || LocationService.StillExists(info.Id)) return false;
            EnterLocation(LocationData.Temporary());
            LocationService.Forget(info.Id);
            StatusText.Text = string.Format(Loc("Str_Loc_DeletedElsewhere"), info.Name);
            return true;
        }

        private void OnWindowActivated(object? sender, EventArgs e)
        {
            if (!IsScanning) LeaveDeletedLocation();
        }

        // Another tab in this process deleting the Location raises Changed; a scan in flight
        // checks again when it records.
        private void OnLocationsChanged(object? sender, EventArgs e)
        {
            if (_deletingHere) return;   // DeleteLocation moves this tab itself
            if (_location.Info is { } info && LocationService.Find(info.Id) == null && !IsScanning) LeaveDeletedLocation();
        }

        private bool _deletingHere;

        private Window? _activationSource;

        private void TrackDeletedLocations()
        {
            LocationService.Changed += OnLocationsChanged;
            Loaded += (_, _) =>
            {
                if (_activationSource != null) _activationSource.Activated -= OnWindowActivated;
                _activationSource = Window.GetWindow(this);
                if (_activationSource != null) _activationSource.Activated += OnWindowActivated;
            };
        }

        private void UntrackDeletedLocations()
        {
            LocationService.Changed -= OnLocationsChanged;
            if (_activationSource != null) _activationSource.Activated -= OnWindowActivated;
            _activationSource = null;
        }

        // Back to an empty tab, as if nothing had been scanned yet.
        private void ClearResults()
        {
            _active.Devices.Clear();
            _active.NewIdentities.Clear();
            _active.ScannedSubnet = string.Empty;
            _active.Completion = ScanCompletion.Partial;
            _active.Refresh = null;
            _active.Adapter = string.Empty;
            _topologyPositions.Clear();
            _scanCompleted = false;
            RefreshDeviceCount();
            UpdateDeepScanButton();
            UpdateExportButtonAvailability();
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
                    var item = new DetailMenuItem { Header = scan.Name, InputGestureText = scan.Target };
                    item.Click += (_, _) => LoadSavedScan(captured);
                    menu.Items.Add(item);
                }
            }

            if (_location.IsTemporary && _location.History.Entries.Count > 0 && LocationService.Items.Count > 0 && !DemoMode)
            {
                menu.Items.Add(new Separator());
                var saveTo = new MenuItem { IsEnabled = !IsScanning };
                saveTo.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Loc_SaveResults");
                foreach (var info in LocationService.Items.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var captured = info;
                    var item = new MenuItem { Header = info.ToString() };
                    item.Click += (_, _) => SaveResultsTo(LocationService.Get(captured));
                    saveTo.Items.Add(item);
                }
                menu.Items.Add(saveTo);
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
            TrackDeletedLocations();
        }

        private void DisposeLocationSuggestions()
        {
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            UntrackDeletedLocations();
        }

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

        private int _suggestionCheck;
        private string _lastGatewayMac = string.Empty;

        /// <summary>
        /// Looks up the gateway's MAC in the ARP cache Windows already has, off the UI thread.
        /// It never asks the network: a suggestion sends no traffic by itself. When several
        /// checks overlap, as on a burst of network changes, only the latest one is shown.
        /// </summary>
        private async void CheckLocationSuggestion()
        {
            if (_disposed || DemoMode || !LocationService.Editable) return;
            int check = ++_suggestionCheck;
            string mac;
            try
            {
                mac = await System.Threading.Tasks.Task.Run(() =>
                {
                    string gateway = LocalNetwork.Detect()?.Gateway ?? string.Empty;
                    return gateway.Length > 0 && NetworkPlatform.Current.ReadNeighborCache().TryGetValue(gateway, out var known) ? known : string.Empty;
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { mac = string.Empty; }
            if (_disposed || check != _suggestionCheck) return;
            ShowLocationSuggestion(mac);
        }

        internal void ShowLocationSuggestion(string gatewayMac)
        {
            _lastGatewayMac = gatewayMac;
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
            // The text is a live region, so a screen reader announces the suggestion when it appears.
            System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(LocationSuggestionText)?
                .RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }

        // After a Location change or a language change the banner may no longer apply, or
        // reads in the old language, so it is worked out again from the last gateway seen.
        private void RefreshLocationSuggestion()
        {
            if (_lastGatewayMac.Length > 0) ShowLocationSuggestion(_lastGatewayMac);
            else LocationSuggestion.Visibility = Visibility.Collapsed;
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

        private void EditNotes() { if (_location.Info is { } info) EditNotes(info); }

        internal void EditNotes(LocationInfo info)
        {
            var latest = LocationService.Find(info.Id) ?? info;
            string current = latest.Notes;
            var dialog = new InputDialog(Loc("Str_Loc_Notes"), latest.Name, Loc("Str_Loc_NotesLabel"), current,
                Loc("Str_Rename_Save"), Loc("Str_Btn_Cancel")) { Owner = Window.GetWindow(this) };
            dialog.UseMultipleLines();
            dialog.ShowDialog();
            if (!dialog.Confirmed) return;
            if (!LocationService.SetNotes(info, dialog.Value ?? string.Empty))
                PersistenceFeedback.Check(false, () => LocationService.SetNotes(info, dialog.Value ?? string.Empty), () => LocationService.SaveError);
            UpdateLocationLabel();
            LocationChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ExportLocation() { if (_location.Info is { } info) ExportLocation(info); }

        internal void ExportLocation(LocationInfo info)
        {
            string locationName = LocationService.Find(info.Id)?.Name ?? info.Name;
            var choice = new ConfirmDialog(Loc("Str_Loc_Export"), Loc("Str_Loc_ExportDetail"), Loc("Str_Loc_ExportButton"),
                Loc("Str_Btn_Cancel"), Loc("Str_Loc_ExportUsernames"), false) { Owner = Window.GetWindow(this) };
            choice.ShowDialog();
            if (!choice.Confirmed) return;
            bool usernames = ((CheckBox)choice.FindName("Check1")).IsChecked == true;
            var dialog = new FileDialog(FileDialogMode.Save)
            {
                Filter = Loc("Str_Loc_FileFilter") + "|*" + LocationTransfer.Extension,
                FileName = SafeFileName(locationName) + LocationTransfer.Extension,
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

        /// <summary>
        /// Deletes a Location after asking. Its files move to locations\.deleted rather than being
        /// erased. If this workspace is in it, the workspace becomes a Temporary scan.
        /// </summary>
        internal void DeleteLocation(LocationInfo info)
        {
            bool here = _location.Info?.Id == info.Id;
            if (here && IsScanning) { StatusText.Text = Loc("Str_Loc_Busy"); return; }
            string name = LocationService.Find(info.Id)?.Name ?? info.Name;
            var confirm = new ConfirmDialog(Loc("Str_Loc_DeleteTitle"), string.Format(Loc("Str_Loc_DeleteDetail"), name),
                Loc("Str_Loc_DeleteButton"), Loc("Str_Btn_Cancel")) { Owner = Window.GetWindow(this) };
            confirm.ShowDialog();
            if (!confirm.Confirmed) return;
            bool deleted;
            _deletingHere = true;
            try { deleted = LocationService.Delete(info, out _); }
            finally { _deletingHere = false; }
            if (!deleted)
            {
                PersistenceFeedback.Check(false, () => false, () => LocationService.SaveError ?? LocationService.LoadError);
                return;
            }
            if (here) SetLocation(LocationData.Temporary());
            else LocationChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Switches to a Location chosen in the Locations sidebar.</summary>
        internal bool OpenLocation(LocationInfo info) => SetLocation(LocationService.Get(info));

        internal void NewLocation() => CreateLocation();

        /// <summary>The largest .ksloc file Import reads. Tests lower it.</summary>
        internal static long MaxImportBytes { get; set; } = 256L * 1024 * 1024;
        private bool _importing;

        private async void ImportLocation()
        {
            var dialog = new FileDialog(FileDialogMode.Open) { Filter = Loc("Str_Loc_FileFilter") + "|*" + LocationTransfer.Extension };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            await ImportLocationFrom(dialog.FileName);
        }

        /// <summary>Imports a .ksloc file and moves the tab to it. Tests call it with their own file.</summary>
        internal async System.Threading.Tasks.Task ImportLocationFrom(string path)
        {
            if (_importing) return;
            LocationInfo? imported;
            string? error;
            _importing = true;
            try
            {
                // Read, checked and staged off the UI thread, so a large export does not freeze the
                // window. A file past the cap is refused before it is read into memory at all.
                long limit = MaxImportBytes;
                string tooLarge = string.Format(Loc("Str_Loc_ImportTooLarge"), Math.Max(1, limit / (1024 * 1024)));
                (imported, error) = await System.Threading.Tasks.Task.Run(() =>
                {
                    if (new FileInfo(path).Length > limit) return ((LocationInfo?)null, (string?)tooLarge);
                    byte[] bytes = File.ReadAllBytes(path);
                    bool ok = LocationService.Import(bytes, out var added, out string? why, notify: false);
                    return (ok ? added : null, why);
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { imported = null; error = ex.Message; }
            finally { _importing = false; }
            if (_disposed) return;
            if (imported == null)
            {
                ShowImportError(error ?? string.Empty);
                return;
            }
            LocationService.NotifyChanged();
            SetLocation(LocationService.Get(imported));
            StatusText.Text = string.Format(Loc("Str_Loc_Imported"), imported.Name);
            // Same name as an existing Location is allowed, so offer a rename straight away.
            if (LocationService.Items.Count(item => string.Equals(item.Name, imported.Name, StringComparison.CurrentCultureIgnoreCase)) > 1)
                RenameLocation();
        }

        /// <summary>Shows why an import failed. Tests capture the message without a dialog.</summary>
        internal Action<string>? ImportFailed { get; set; }

        private void ShowImportError(string message)
        {
            if (ImportFailed != null) { ImportFailed(message); return; }
            MessageBox.Show(Window.GetWindow(this), string.Format(Loc("Str_Loc_ImportFailed"), message), AppInfo.DisplayName,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

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
            AdapterChoice = scan.Adapter ?? string.Empty;
            FocusTargets();
        }

        /// <summary>
        /// The adapter this tab scans from, by its Windows name, or empty to let Windows choose.
        /// A saved scan restores its own choice; Scan settings changes it.
        /// </summary>
        internal string AdapterChoice
        {
            get => _adapterChoice;
            set
            {
                _adapterChoice = value ?? string.Empty;
                // Rescans use the same scanner, so they follow the choice too.
                _active.Scanner.Binding = _adapterChoice.Length == 0 ? null : FindAdapter(_adapterChoice);
            }
        }
        private string _adapterChoice = string.Empty;

        /// <summary>Finds a connected adapter by name. Tests supply their own adapters.</summary>
        internal Func<string, ScanBinding?> FindAdapter { get; set; } = ScanBinding.Find;

        /// <summary>The adapters Scan settings offers. Tests supply their own.</summary>
        internal Func<IReadOnlyList<ScanBinding>> AvailableAdapters { get; set; } = ScanBinding.Available;

        /// <summary>Asks whether to scan on another adapter. Tests answer it without a dialog.</summary>
        internal Func<string, bool>? ConfirmAdapter { get; set; }

        /// <summary>The adapter Windows would route these targets through, by its name, or empty.</summary>
        private static string RouteAdapter(string targets)
        {
            var parsed = ScanTargets.Parse(targets);
            if (!parsed.Ok || parsed.Addresses.Count == 0) return string.Empty;
            try { return NetworkPlatform.Current.BestRoute(parsed.Addresses[0])?.Interface ?? string.Empty; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return string.Empty; }
        }

        /// <summary>
        /// Resolves the tab's adapter choice for one run. The choice is looked up again every
        /// time, because adapters come and go. A chosen adapter that is not connected asks before
        /// falling back to the route Windows picks; a target that is neither on the chosen
        /// adapter's network nor routed through it by Windows is refused.
        /// <paramref name="adapter"/> is the adapter the run will actually use, for history.
        /// </summary>
        private bool AdapterAllowsScan(string targets, IReadOnlyList<System.Net.IPAddress> addresses, out string adapter)
        {
            adapter = RouteAdapter(targets);
            if (_adapterChoice.Length == 0) { _active.Scanner.Binding = null; return true; }
            var binding = FindAdapter(_adapterChoice);
            if (binding == null)
            {
                string message = string.Format(Loc("Str_Loc_AdapterMissing"), _adapterChoice);
                bool fallBack;
                if (ConfirmAdapter != null) fallBack = ConfirmAdapter(message);
                else
                {
                    var confirm = new ConfirmDialog(Loc("Str_Loc_AdapterTitle"), message, Loc("Str_Btn_Scan"), Loc("Str_Btn_Cancel"))
                        { Owner = Window.GetWindow(this) };
                    confirm.ShowDialog();
                    fallBack = confirm.Confirmed;
                }
                _active.Scanner.Binding = null;
                return fallBack;
            }
            var outside = addresses.FirstOrDefault(address => !binding.Reaches(address));
            if (outside != null)
            {
                StatusText.Text = string.Format(Loc("Str_St_AdapterOffLink"), outside, binding.InterfaceName, binding.Network);
                FocusTargets();
                return false;
            }
            _active.Scanner.Binding = binding;
            adapter = binding.InterfaceName;
            return true;
        }

        /// <summary>
        /// Moves a Temporary scan's results into a Location: its history entries and any names
        /// typed while it was temporary. Nothing is approved and the baseline is not touched,
        /// then the tab moves to that Location.
        /// </summary>
        internal bool SaveResultsTo(LocationData target)
        {
            if (_disposed || IsScanning || !_location.IsTemporary || target.IsTemporary || ReferenceEquals(target, _location)) return false;
            var source = _location;
            bool saved = true;
            foreach (var entry in source.History.Entries) saved &= target.History.Adopt(entry);
            foreach (var device in _active.Devices)
                if (source.Devices.GetName(device) is { } name) saved &= target.Devices.SetName(device, name);
            PersistenceFeedback.Check(saved, () => target.History.Retry() && target.Devices.Retry(),
                () => target.History.SaveError ?? target.Devices.SaveError);
            // The results now belong to the target, so they stay on screen there.
            SetLocation(target, keepResults: true);
            HistoryRecorded?.Invoke(this, EventArgs.Empty);
            return true;
        }

        private string? AskName(string titleKey, string current, string? detail = null)
        {
            var dialog = new InputDialog(Loc(titleKey), detail ?? LocationName, Loc("Str_Rename_Label"), current,
                Loc("Str_Rename_Save"), Loc("Str_Btn_Cancel")) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
            return dialog.Confirmed && !string.IsNullOrWhiteSpace(dialog.Value) ? dialog.Value.Trim() : null;
        }

        private void SaveScan()
        {
            if (_loadedScanName == null) { SaveScanAs(); return; }
            var store = _location.SavedScans;
            PersistenceFeedback.Check(store.SaveEdited(_loadedScanName, Targets.Trim(), _active.Options, _adapterChoice), store.Retry, () => store.SaveError);
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
            SaveScanAs(name);
        }

        /// <summary>
        /// Saves the current targets, settings and adapter choice under a name in this Location.
        /// </summary>
        internal bool SaveScanAs(string name)
        {
            var store = _location.SavedScans;
            bool saved = PersistenceFeedback.Check(store.SaveEdited(name, Targets.Trim(), _active.Options, _adapterChoice), store.Retry, () => store.SaveError);
            _loadedScanName = name;
            LocationChanged?.Invoke(this, EventArgs.Empty);
            return saved;
        }

        private void CreateLocation()
        {
            if (AskName("Str_Loc_NewTitle", string.Empty) is not { } name) return;
            if (LocationService.Create(name, out var created) && created != null)
                SetLocation(LocationService.Get(created));
            else
                PersistenceFeedback.Check(false, () => false, () => LocationService.SaveError ?? LocationService.LoadError);
        }

        private void RenameLocation() { if (_location.Info is { } info) RenameLocation(info); }

        /// <summary>Renames any Location, from this menu or the Locations sidebar. The ID and data stay put.</summary>
        internal void RenameLocation(LocationInfo info)
        {
            string current = LocationService.Find(info.Id)?.Name ?? info.Name;
            if (AskName("Str_Loc_Rename", current, current) is not { } name) return;
            if (!LocationService.Rename(info, name))
                PersistenceFeedback.Check(false, () => LocationService.Rename(info, name), () => LocationService.SaveError);
            UpdateLocationLabel();
            LocationChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
