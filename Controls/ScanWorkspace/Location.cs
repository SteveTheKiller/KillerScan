using System;
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
            if (LocationService.Loaded && LocationService.LoadError == null && !DemoMode)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(MenuText("Str_Loc_New", CreateLocation, !IsScanning));
                menu.Items.Add(MenuText("Str_Loc_Rename", RenameLocation, _location.Info != null));
                menu.Items.Add(MenuText("Str_Loc_Manage", () => ManageLocationsRequested?.Invoke(this, EventArgs.Empty)));
            }
            return menu;
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
