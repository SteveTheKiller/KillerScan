using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private readonly Button _locationButton = new() { Name = "LocationSelectionButton", Content = "\uE707", FontSize = 16, Margin = new Thickness(0, 0, 0, 10) };
        private string LocationName => Locations.Current.Id == LocationCatalog.LegacyId ? Loc("Str_Location_Legacy") : Locations.Current.Name;
        private bool CanChangeLocation => !Locations.IsBusy && _scanWorkspace?.IsScanning != true && !_terminalScanRunning &&
            _speedTestRun == null && _terminalControl?.HasRunningCommand != true && !(_terminalIsPing && !_terminalExited);

        private void InitializeLocations()
        {
            _locationButton.FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets");
            _locationButton.SetResourceReference(StyleProperty, "RailButton");
            _locationButton.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "Str_Location_Title");
            var menu = new ContextMenu();
            _locationButton.ContextMenu = menu;
            menu.Opened += (_, _) =>
            {
                menu.Items.Clear();
                if (!CanChangeLocation) menu.Items.Add(new MenuItem { Header = Loc("Str_Location_Busy"), IsEnabled = false });
                foreach (var location in Locations.Catalog.Items)
                {
                    var item = new MenuItem { Header = location.Id == LocationCatalog.LegacyId ? Loc("Str_Location_Legacy") : location.Name,
                        IsCheckable = true, IsChecked = location.Id == Locations.Current.Id, IsEnabled = CanChangeLocation };
                    item.Click += (_, _) => ChangeLocation(location.Id);
                    menu.Items.Add(item);
                }
                menu.Items.Add(new Separator());
                var create = new MenuItem { Header = Loc("Str_Location_New"), IsEnabled = CanChangeLocation };
                create.Click += (_, _) => EditLocation(create: true);
                menu.Items.Add(create);
                var rename = new MenuItem { Header = Loc("Str_Location_Rename"), IsEnabled = CanChangeLocation && Locations.Current.Id != LocationCatalog.LegacyId };
                rename.Click += (_, _) => EditLocation(create: false);
                menu.Items.Add(rename);
            };
            _locationButton.Click += (_, _) => { FlyoutPlacement.Attach(menu, _locationButton); menu.IsOpen = true; };
            RailButtons.Children.Insert(Math.Min(1, RailButtons.Children.Count), _locationButton);
            LocaleManager.LocaleChanged += RefreshLocationLabel;
            RefreshLocationLabel();
        }

        private void RefreshLocationLabel()
        {
            _locationButton.ToolTip = Loc("Str_Location_Title") + ": " + LocationName;
            SubtitleText.Text = Loc("Str_Location_Title") + ": " + LocationName;
            SubtitleText.ToolTip = SubtitleText.Text;
            SubtitleText.MaxWidth = 260;
            SubtitleText.TextTrimming = TextTrimming.CharacterEllipsis;
        }

        private void EditLocation(bool create)
        {
            if (!CanChangeLocation) return;
            var dialog = new InputDialog(Loc(create ? "Str_Location_New" : "Str_Location_Rename"),
                Loc("Str_Location_Explicit"), Loc("Str_Rename_Label"), create ? "" : Locations.Current.Name,
                Loc("Str_Rename_Save"), Loc("Str_Btn_Cancel")) { Owner = this };
            dialog.ShowDialog();
            if (!dialog.Confirmed) return;
            try
            {
                if (create) ChangeLocation(Locations.Catalog.Create(dialog.Value).Id);
                else { Locations.Catalog.Rename(Locations.Current.Id, dialog.Value); RefreshLocationLabel(); }
            }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or ArgumentException)
            { MessageBox.Show(ex.Message, Loc("Str_Location_Title"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private bool ChangeLocation(string id)
        {
            if (!CanChangeLocation) return false;
            if (id == Locations.Current.Id) return true;
            if (_scanWorkspace?.ScannedDevices.Count > 0 || _terminalControl != null || _watchWorkspace != null)
            {
                var confirm = new ConfirmDialog(Loc("Str_Location_Title"), Loc("Str_Location_Clear"),
                    Loc("Str_Location_Switch"), Loc("Str_Btn_Cancel")) { Owner = this };
                confirm.ShowDialog();
                if (!confirm.Confirmed || !CanChangeLocation) return false;
            }
            try { if (!Locations.TrySelect(id)) return false; }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException)
            { MessageBox.Show(ex.Message, Loc("Str_Location_Title"), MessageBoxButton.OK, MessageBoxImage.Error); return false; }
            // Old transient results must never be relabelled as belonging to the new location.
            if (_terminalControl != null)
            {
                _workspaceBody.Children.Remove(_terminalControl); _terminalControl.Dispose(); _terminalControl = null;
            }
            _terminalTitle = null; _terminalStatusKey = null; _terminalScanHasStatus = false;
            _terminalAdapterId = ""; _terminalSourceAddress = "";
            Environment.SetEnvironmentVariable("KS_LOCATION_ID", Locations.Current.Id);
            if (_viewToolbarTargets.TryGetValue("terminal", out var terminalTarget) && terminalTarget is TextBox input) input.Text = "";
            if (_watchWorkspace != null)
            {
                _watchWorkspace.Dispose(); _workspaceBody.Children.Remove(_watchWorkspace); _watchWorkspace = null;
                if (_viewToolbars.TryGetValue("watch", out var toolbar)) _workspaceToolbar.Children.Remove(toolbar);
                _viewToolbars.Remove("watch"); _viewToolbarTargets.Remove("watch");
            }
            _scanWorkspace?.ResetLocation(); _historyWorkspace?.ShowEntry(null);
            ShowScanView("devices"); RefreshHistoryList(); RefreshProfilesList(); RefreshLocationLabel();
            return true;
        }
    }
}
