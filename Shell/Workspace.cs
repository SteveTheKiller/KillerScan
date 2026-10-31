using System.Diagnostics;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerScan.Controls;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private readonly Grid _workspaceBody = new();
        private readonly Grid _workspaceToolbar = new();
        private readonly Grid _toolbarSurface = new() { UseLayoutRounding = true, Background = Brushes.Transparent };
        private readonly Grid _workspaceSummary = new() { Visibility = Visibility.Collapsed, UseLayoutRounding = true };
        private ScanWorkspace? _scanWorkspace;
        private FrameworkElement? _selectedWorkspace;
        private string _workspaceView = "scan";
        private string? _announcedStatus;
        private ScanWorkspace? ActiveScan => _selectedWorkspace == _scanWorkspace ? _scanWorkspace : null;

        private void InitializeWorkspace()
        {
            // Toolbar, summary, top tab slot, the pane, bottom tab slot (Shell/WorkspaceTabs.cs).
            WorkspaceHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WorkspaceHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WorkspaceHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WorkspaceHost.RowDefinitions.Add(new RowDefinition());
            WorkspaceHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            // Supplemental view information sits above the existing table frame.
            foreach (UIElement child in WorkspaceHost.Children)
                if (Grid.GetRow(child) == 1) Grid.SetRow(child, 3);
            Grid.SetRow(_workspaceSummary, 1);
            WorkspaceHost.Children.Add(_workspaceSummary);
            BuildWorkspaceNavigation();
            _workspaceToolbar.ColumnDefinitions.Add(new ColumnDefinition());
            // Transparent, not a colour of its own. The window paints the background across its
            // full width, so letting it through keeps one continuous gradient; painting the brush
            // here restarted the ramp inside the content column, and painting a flat tone instead
            // just moved the seam to where the bar met the margins beside the pane.
            var toolbarSurface = _toolbarSurface;
            // No grain of its own either. The chrome strip behind this whole row already carries
            // one, and it shows through now that the bar is transparent; adding a second here
            // doubled the texture over the toolbar. The strip sits below the controls, so the
            // subnet box still reads as a field rather than as textured background.
            toolbarSurface.Children.Add(_workspaceToolbar);
            TitleToolbarHost.Child = toolbarSurface;
            TerminalLayout.Children.Add(_workspaceBody);
            TerminalLayout.SizeChanged += (_, _) => ClipWorkspaceSurface();
            NewScan(_startupScanTarget ?? string.Empty);
        }

        /// <summary>
        /// The window has one input bar. Each view registers the controls it needs there, and
        /// only the active view's are shown, so a tool never carries a second bar of its own.
        /// </summary>
        private readonly Dictionary<string, FrameworkElement> _viewToolbars = [];

        private void RegisterViewToolbar(string view, FrameworkElement toolbar)
        {
            toolbar.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(toolbar, 0);
            _viewToolbars[view] = toolbar;
            FitToTitleBar(toolbar);
            _workspaceToolbar.Children.Add(toolbar);
            UpdateViewToolbar();
        }

        /// <summary>
        /// The bars were laid out for their own 34px row and now ride in the 32px title bar (22px
        /// on 98SE), where their 2px margins and 3px padding clipped the address box's text top
        /// and bottom. Vertical margins go and vertical padding drops to 1px, so every control
        /// fits whatever height the caption has.
        /// </summary>
        private static void FitToTitleBar(FrameworkElement element)
        {
            element.Margin = new Thickness(element.Margin.Left, 0, element.Margin.Right, 0);
            if (element is Control c)
                c.Padding = new Thickness(c.Padding.Left, Math.Min(c.Padding.Top, 1), c.Padding.Right, Math.Min(c.Padding.Bottom, 1));
            if (element is Panel panel)
                foreach (var child in panel.Children.OfType<FrameworkElement>()) FitToTitleBar(child);
            else if (element is Decorator { Child: FrameworkElement inner } && element is not Control) FitToTitleBar(inner);
        }

        private void UpdateViewToolbar()
        {
            // Only scan-backed views share scan controls. Other views use their own toolbar.
            string key = ActiveScan != null ? "scan" : _workspaceView;
            foreach (var pair in _viewToolbars)
                pair.Value.Visibility = pair.Key == key ? Visibility.Visible : Visibility.Collapsed;
            // A view with no bar of its own (the terminal) gets the row back.
            _toolbarSurface.Visibility = _viewToolbars.ContainsKey(key) ? Visibility.Visible : Visibility.Collapsed;
            TitleTools.Visibility = key == "scan" ? Visibility.Visible : Visibility.Collapsed;
            // The bars are different widths, so what fits beside them changes with the view.
            FitToolbarViews();
        }

        private void ShowWorkspaceContent(FrameworkElement content, string view)
        {
            if (!_workspaceBody.Children.Contains(content)) _workspaceBody.Children.Add(content);
            _selectedWorkspace = content;
            _workspaceView = view;
            _workspaceSummary.Visibility = view == "history" ? Visibility.Visible : Visibility.Collapsed;
            // The export menu lives on the rail whatever is in front, so it is told which view it
            // is acting for. Anything built on the scan workspace resolves itself.
            _scanWorkspace?.ExportContext = view is "watch" or "terminal" or "history" ? view : "scan";
            foreach (FrameworkElement child in _workspaceBody.Children)
                child.Visibility = child == content ? Visibility.Visible : Visibility.Collapsed;
            UpdateViewToolbar();
            UpdateWorkspaceNavigation();
            UpdateWorkspaceStatus();
            UpdateWorkspaceRail();
        }

        private ScanWorkspace NewScan(string target = "")
        {
            if (_scanWorkspace == null)
            {
                _scanWorkspace = new ScanWorkspace(target, DemoMode);
                var scanToolbar = _scanWorkspace.DetachToolbar();
                scanToolbar.Margin = new Thickness(8, 0, 0, 0);
                _viewToolbarTargets["scan"] = (FrameworkElement)_scanWorkspace.FindName("SubnetInput");
                RegisterViewToolbar("scan", scanToolbar);
                // The filter lives in the title bar's right end, like KillerShell's search.
                foreach (string name in new[] { "FilterToggleButton", "FilterBox" })
                {
                    var part = (FrameworkElement)_scanWorkspace.FindName(name);
                    ((Panel)part.Parent).Children.Remove(part);
                    TitleTools.Children.Add(part);
                }
                var networkDetails = new StackPanel();
                foreach (string name in new[] { "LocalIpLabel", "GatewayLabel", "DnsLabel", "InterfaceLabel" })
                {
                    var label = (TextBlock)_scanWorkspace.FindName(name);
                    var group = (FrameworkElement)label.Parent;
                    ((Panel)group.Parent).Children.Remove(group);
                    networkDetails.Children.Add(group);
                }
                NetworkFooter.ToolTip = networkDetails;
                // Address, adapter and link speed, with the wired or wireless glyph beside them.
                // Both follow the workspace's own labels, so a network change updates the footer
                // without the shell having to detect anything itself.
                NetworkFooter.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text")
                    { Source = _scanWorkspace.FindName("FooterNetLabel") });
                NetworkFooterAdapter.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text")
                    { Source = _scanWorkspace.FindName("FooterNetAdapter") });
                NetworkFooterSpeed.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text")
                    { Source = _scanWorkspace.FindName("FooterNetSpeed") });
                NetworkFooterIcon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text")
                    { Source = _scanWorkspace.FindName("InterfaceIcon") });
                // The badge appears and disappears with portable mode, and the address changes
                // width with the network, so the fit is re-run on both rather than once.
                UpdateScanLight();
                FooterBar.SizeChanged += (_, _) => { FitFooterNetwork(); FitFooterStatus(); };
                PortableBadge.IsVisibleChanged += (_, _) => { FitFooterNetwork(); FitFooterStatus(); };
                ScanProgress.IsVisibleChanged += (_, _) => FitFooterNetwork();
                NetworkFooter.SizeChanged += (_, _) => FitFooterNetwork();
                FitFooterNetwork();
                FitFooterStatus();
                var count = (TextBlock)_scanWorkspace.FindName("DeviceCount");
                ((Panel)count.Parent).Children.Remove(count);
                count.Margin = new Thickness(8, 0, 0, 0);
                // The footer's own type, so the count reads as part of the status line.
                count.FontSize = 16;
                count.SetResourceReference(TextBlock.FontFamilyProperty, "BrandSansFont");
                TextOptions.SetTextFormattingMode(count, TextFormattingMode.Ideal);
                DeviceCountFooter.Children.Add(count);
                // Export lives on the rail with the other app actions rather than on the view bar.
                // It is one step at the end of a scan, and the bar belongs to the views and to the
                // target box. Same button, same flyout, so the export code is untouched.
                var export = (Button)_scanWorkspace.FindName("ExportButton");
                ((Panel)export.Parent).Children.Remove(export);
                export.ClearValue(Control.TemplateProperty);
                export.ClearValue(Control.BackgroundProperty);
                export.ClearValue(FrameworkElement.WidthProperty);
                export.ClearValue(FrameworkElement.HeightProperty);
                export.ClearValue(FrameworkElement.VerticalAlignmentProperty);
                export.Margin = new Thickness(0, 0, 0, 10);
                export.FontSize = 15;
                export.Content = "\uE896";
                export.SetResourceReference(StyleProperty, "RailButton");
                export.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "Str_TT_Export");
                RailButtons.Children.Insert(0, export);
                _scanWorkspace.DeviceAction += (_, e) => WorkspaceDeviceAction(e.Device, e.Action, e.ServicePort);
                _scanWorkspace.ShellExportRequested += ShellExport;
                // The sidebar lists belong to the workspace's Location, so they follow it.
                _scanWorkspace.LocationChanged += (_, _) =>
                {
                    RefreshHistoryList();
                    RefreshLocationsList();
                    if (_workspaceView == "history") ShowHistoryEntry();
                    UpdateWorkspaceStatus();
                };
                _scanWorkspace.ManageLocationsRequested += (_, _) => ShowLocationsSection();
                _scanWorkspace.SavedScansEmptied += (_, _) => RetireHoldingLocation();
                // Everything bound with DynamicResource follows a language change on its own. The
                // status line and the device count do not: they are composed in code and stored,
                // so they are re-stated here from the state the workspace is in.
                LocaleManager.LocaleChanged += OnShellLocaleChanged;
                _scanWorkspace.HistoryRecorded += (_, _) =>
                {
                    if (!_sidebarCollapsed) RefreshHistoryList();
                    if (!_sidebarCollapsed && _sidebarSection == "locations") RefreshLocationsList();
                    // A re-rolled demo network invalidates whatever Keep Alive was watching, so it
                    // follows the new addresses rather than sitting on a network that is gone.
                    if (DemoData.Enabled) _watchWorkspace?.RestartWith(DemoWatchTargets());
                };
                _scanWorkspace.StateChanged += (_, _) =>
                {
                    if (ActiveScan != null)
                    {
                        _workspaceView = ActiveScan.View == "topology" ? "topology"
                            : ActiveScan.View == "services" ? "services" : "scan";
                        UpdateWorkspaceNavigation();
                        UpdateWorkspaceStatus();
                        UpdateWorkspaceRail();
                    }
                    // Outside the guard: a scan carries on while you sit in Keep Alive or a
                    // terminal, and the light is the one thing on screen still reporting it.
                    UpdateScanLight();
                };
                _scanWorkspace.ApplyScale(_appScale);
            }
            else if (!string.IsNullOrWhiteSpace(target) && !_scanWorkspace.IsScanning)
                _scanWorkspace.Targets = target;
            ShowScanView("devices");
            return _scanWorkspace;
        }

        /// <summary>
        /// Whether the footer is showing the adapter and link speed. Clicking the cell toggles it,
        /// and the choice is remembered; a bar too narrow to hold it folds it away regardless.
        /// </summary>
        private bool _footerNetExpanded = App.GetSetting("FooterNetworkDetail") != "0";

        /// <summary>
        /// The footer light. Amber is the resting state and also what an interrupted scan leaves
        /// behind, on the grounds that a half-finished scan and no scan at all tell you the same
        /// thing about the list on screen. A view with no scan behind it, Keep Alive or a terminal,
        /// leaves the light on whatever the scan workspace last reported.
        /// </summary>
        private void UpdateScanLight()
        {
            if (ScanLight == null) return;
            var state = _scanWorkspace?.Indicator ?? Controls.ScanIndicator.Idle;
            (string fill, string key) = state switch
            {
                Controls.ScanIndicator.Scanning => ("#D0453A", "Str_Light_Scanning"),
                Controls.ScanIndicator.Deep     => ("#2F7FD0", "Str_Light_Deep"),
                Controls.ScanIndicator.Complete => ("#3FA95F", "Str_Light_Done"),
                _                               => ("#E0A317", "Str_Light_Idle"),
            };
            ScanLight.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill));
            ScanLight.SetResourceReference(ToolTipProperty, key);
        }

        private void NetworkFooter_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _footerNetExpanded = !_footerNetExpanded;
            App.SetSetting("FooterNetworkDetail", _footerNetExpanded ? "1" : "0");
            FitFooterNetwork();
        }

        /// <summary>
        /// Keeps the network cell clear of the portable badge. The badge is centered on the whole
        /// footer rather than on the space left over, so a wide right cell runs underneath it. The
        /// adapter and speed are the part that can go: the address stays whatever happens.
        /// </summary>
        private void FitFooterNetwork()
        {
            if (NetworkFooterAdapter == null || NetworkFooterSpeed == null) return;

            if (!_footerNetExpanded)
            {
                NetworkFooterAdapter.Visibility = Visibility.Collapsed;
                NetworkFooterSpeed.Visibility = Visibility.Collapsed;
                return;
            }

            double footer = FooterBar.ActualWidth;
            if (footer <= 0) return;

            // Half the bar, plus half the badge and a little air, is where the right cell has to
            // stop. The whole cell is measured rather than added up from its parts, so the
            // copyright, the margins and the scan progress bar (shown only while a scan runs)
            // all count.
            double badge = PortableBadge.Visibility == Visibility.Visible ? PortableBadge.ActualWidth : 0;
            double limit = footer / 2 + badge / 2 + FooterBadgeGap;
            double cellRight = VersionCell.TranslatePoint(new Point(VersionCell.ActualWidth, 0), FooterBar).X
                             + VersionCell.Margin.Right;
            double room = cellRight - limit;

            // The speed is the first to go: it is the least useful of the three and the shortest,
            // so dropping it usually buys enough room to keep the adapter name intact. Trimming
            // either one to an ellipsis looks like damage rather than a fit.
            bool showAdapter = !string.IsNullOrEmpty(NetworkFooterAdapter.Text);
            bool showSpeed = showAdapter && !string.IsNullOrEmpty(NetworkFooterSpeed.Text);
            if (showSpeed && FooterCellWidth(true, true) > room) showSpeed = false;
            if (showAdapter && !showSpeed && FooterCellWidth(true, false) > room) showAdapter = false;

            NetworkFooterAdapter.Visibility = showAdapter ? Visibility.Visible : Visibility.Collapsed;
            NetworkFooterSpeed.Visibility = showSpeed ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>The right cell's full width, margins included, with the given parts shown.</summary>
        private double FooterCellWidth(bool adapter, bool speed)
        {
            NetworkFooterAdapter.Visibility = adapter ? Visibility.Visible : Visibility.Collapsed;
            NetworkFooterSpeed.Visibility = speed ? Visibility.Visible : Visibility.Collapsed;
            VersionCell.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return VersionCell.DesiredSize.Width;
        }

        /// <summary>Breathing room between the right cell and the portable badge.</summary>
        private const double FooterBadgeGap = 8;

        /// <summary>
        /// The same problem on the left of the bar. The status line had a fixed 230px cap so it
        /// could not slide under the centered badge, which meant it was clipped to an ellipsis on
        /// a wide window that had room to spare. The cap is measured instead: everything up to the
        /// badge, or up to the version cell when no badge is showing, less whatever the device
        /// count needs. A message still too long for that is trimmed, but only then, and the whole
        /// of it is in the tooltip.
        /// </summary>
        private void FitFooterStatus()
        {
            if (StatusText == null || FooterBar == null) return;
            double footer = FooterBar.ActualWidth;
            if (footer <= 0) return;

            double badge = PortableBadge.Visibility == Visibility.Visible ? PortableBadge.ActualWidth : 0;
            double right = badge > 0 ? footer / 2 - badge / 2 : footer - VersionCell.ActualWidth;

            double count = 0;
            if (DeviceCountFooter.Visibility == Visibility.Visible)
            {
                DeviceCountFooter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                count = DeviceCountFooter.DesiredSize.Width;
            }
            double budget = right - StatusCellInset - count - FooterStatusGap;
            StatusText.MaxWidth = budget > 60 ? budget : 60;
        }

        /// <summary>The status cell's own left inset, which the measurement starts after.</summary>
        private const double StatusCellInset = 18;
        /// <summary>Breathing room so the text never touches the badge.</summary>
        private const double FooterStatusGap = 16;

        private void ShowScanView(string view)
        {
            if (_scanWorkspace == null) return;
            _scanWorkspace.SetView(view);
            ShowWorkspaceContent(_scanWorkspace, view == "topology" ? "topology"
                : view == "services" ? "services" : "scan");
        }

        private void OnShellLocaleChanged()
        {
            _scanWorkspace?.RefreshLocalizedText();
            _historyWorkspace?.RefreshLocale();
            UpdateWorkspaceStatus();
            UpdateScanLight();
        }

        private void UpdateWorkspaceStatus()
        {
            // Str_St_Ready rather than nothing: before the first scan the cell would otherwise be
            // empty in every language except English, where the XAML's design-time "Ready" showed.
            string status = ActiveScan?.Status ?? (_workspaceView == "watch" ? Loc("Str_View_KeepAlive")
                : _workspaceView == "history" ? Loc("Str_History_Title")
                : _workspaceView == "services" ? Loc("Str_Services_Title") : Loc("Str_St_Ready"));
            StatusText.Text = status;
            // The text is a live region, so a screen reader hears where things stand. Not while a
            // scan runs, when its progress lines would talk over each other. A run's final status
            // is written before its bar goes, so this compares with what was last announced.
            if (ActiveScan?.IsProgressVisible != true && status != _announcedStatus)
            {
                _announcedStatus = status;
                System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(StatusText)?
                    .RaiseAutomationEvent(System.Windows.Automation.Peers.AutomationEvents.LiveRegionChanged);
            }
            ScanProgress.Value = ActiveScan?.Progress ?? 0;
            ScanProgress.Visibility = ActiveScan?.IsProgressVisible == true ? Visibility.Visible : Visibility.Collapsed;
            if (_workspaceView == "terminal") UpdateTerminalPanelStatus();
            // Empty as well as duplicated: before the first scan there is no count to show, and
            // the cell would otherwise contribute its margin to a bar that has nothing in it.
            if (_scanWorkspace?.FindName("DeviceCount") is TextBlock count)
            {
                bool hide = _workspaceView is "terminal" or "history" || string.IsNullOrEmpty(count.Text) || StatusText.Text.Contains(count.Text);
                count.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
                DeviceCountFooter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
            }
            FitFooterStatus();
        }

        /// <summary>
        /// The status cell says what happened in one line, which is all it has room for. The
        /// tooltip is where the rest goes, and what it says depends on the view in front of you:
        /// an alert about unknown devices names them, a finished scan breaks down what it found,
        /// and a Keep Alive run keeps reporting while you are somewhere else. Built at hover
        /// rather than stored, so it is never a description of a state that has since moved on.
        /// </summary>
        private void StatusText_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            string tip = BuildStatusTooltip();
            // No tooltip at all rather than an empty box: there is nothing useful to add before
            // the first scan, and a blank popup following the cursor is worse than none.
            if (string.IsNullOrEmpty(tip)) { e.Handled = true; return; }
            StatusText.ToolTip = tip;
        }

        // Measured on a copy: a trimmed TextBlock reports the width of what it drew, not of its text.
        private bool StatusIsTrimmed()
        {
            var whole = new TextBlock
            {
                Text = StatusText.Text, FontFamily = StatusText.FontFamily, FontSize = StatusText.FontSize,
                FontWeight = StatusText.FontWeight, FontStyle = StatusText.FontStyle
            };
            whole.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return whole.DesiredSize.Width > StatusText.MaxWidth;
        }

        private string BuildStatusTooltip()
        {
            if (_workspaceView == "terminal") return StatusText.Text;
            if (_workspaceView == "watch" && _watchWorkspace != null)
            {
                var (total, replying) = _watchWorkspace.WatchState;
                if (total == 0) return string.Empty;
                return string.Format(Loc("Str_Tip_Watch"), total, total - replying);
            }
            if (_workspaceView == "history")
                return HistoryList.SelectedItem is ScanHistoryEntry entry
                    ? string.Format(Loc("Str_Tip_History"), entry.Target,
                        entry.ScannedAt.ToLocalTime().ToString("g"), entry.Devices.Count)
                    : string.Empty;

            // A status the footer trims is read here in full, ahead of the counts.
            string whole = StatusIsTrimmed() ? StatusText.Text : string.Empty;
            var devices = _scanWorkspace?.ScannedDevices;
            if (devices == null || devices.Count == 0) return whole;
            if (whole.Length > 0) whole += "\n\n";

            // The alert the status bar raises is a count. This is the part that answers "which?".
            var unknown = _scanWorkspace!.NewDevices;
            if (unknown.Count > 0)
            {
                const int Listed = 8;
                var lines = unknown.Take(Listed).Select(device =>
                    string.IsNullOrWhiteSpace(device.Hostname)
                        ? $"  {device.IpAddress}  {device.MacAddress}"
                        : $"  {device.IpAddress}  {device.Hostname}");
                string body = string.Join("\n", lines);
                if (unknown.Count > Listed)
                    body += "\n" + string.Format(Loc("Str_Tip_AndMore"), unknown.Count - Listed);
                return whole + Loc("Str_Tip_UnknownHead") + "\n" + body + "\n\n" + Loc("Str_Tip_TrustHint");
            }
            int withPorts = devices.Count(device => device.OpenPorts.Count > 0);
            return whole + string.Format(Loc("Str_Tip_Scan"), devices.Count, withPorts);
        }

        private void DisposeWorkspace()
        {
            DisposeTerminalPanel();
            foreach (var content in _workspaceBody.Children.OfType<FrameworkElement>())
                if (content != _terminalControl) (content as IDisposable)?.Dispose();
        }

        // AppScale.cs hook: the scan bar rides in the title bar, so the workspace parts scale
        // one by one instead of through ScaleHost.
        partial void ApplyAppScaleTo(double scale, ref bool handled)
        {
            ApplyWorkspaceScale(scale);
            handled = true;
        }

        private void ApplyWorkspaceScale(double scale)
        {
            _workspaceToolbar.LayoutTransform = new ScaleTransform(scale, scale);
            _workspaceSummary.LayoutTransform = new ScaleTransform(scale, scale);
            _viewTabBand.LayoutTransform = new ScaleTransform(scale, scale);
            foreach (var content in _workspaceBody.Children.OfType<FrameworkElement>())
                if (content is ScanWorkspace scan) scan.ApplyScale(scale);
                else if (content is NetworkToolsWindow tools) tools.ApplyScale(scale);
                else content.LayoutTransform = new ScaleTransform(scale, scale);
        }

        private void RefreshWorkspaceLocale()
        {
            RefreshTerminalPanelTheme();
            ClipWorkspaceSurface();
            _scanWorkspace?.RefreshLocale();
            _historyWorkspace?.RefreshLocale();
            FitToolbarViews();
            UpdateWorkspaceNavigation();
            UpdateWorkspaceStatus();
        }

        private void WorkspaceDeviceAction(NetworkDevice device, string action, int? servicePort = null)
        {
            try { RunWorkspaceDeviceAction(device, action, servicePort); }
            catch (System.ComponentModel.Win32Exception ex) { StatusText.Text = ex.Message; }
            catch (InvalidOperationException ex) { StatusText.Text = ex.Message; }
        }

        private void RunWorkspaceDeviceAction(NetworkDevice device, string action, int? servicePort = null)
        {
            if (!IPAddress.TryParse(device.IpAddress, out var address)) return;
            string ip = address.ToString();
            if (action == "Watch" || action == "Diagnose") { OpenNetworkTool(device, action == "Diagnose"); return; }
            if (action == "Browser")
            {
                string? url = ScanWorkspace.BrowserAddress(device, servicePort);
                if (url != null) Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }
            if (action == "Rdp")
            {
                Process.Start(new ProcessStartInfo(SystemTools.Path("mstsc.exe"), "/v:" + (servicePort.HasValue ? ScanWorkspace.ServiceEndpoint(ip, servicePort.Value) : ip))
                { UseShellExecute = true, WorkingDirectory = SystemTools.WorkingDirectory });
                return;
            }
            string command;
            if (action.StartsWith("Ssh", StringComparison.Ordinal))
            {
                // Usernames belong to the Location, so another site's same IP never skips the prompt.
                var logins = ShellLocation.Logins;
                string? user = logins.Get(device.MacAddress, ip);
                if (user == null || action.StartsWith("SshAs", StringComparison.Ordinal))
                {
                    var dialog = new InputDialog(string.Format(Loc("Str_Ssh_Head"), ip), Loc("Str_Ssh_Detail"), Loc("Str_Ssh_User"), user ?? "", Loc("Str_Ssh_Connect"), Loc("Str_Btn_Cancel")) { Owner = this };
                    dialog.ShowDialog(); if (!dialog.Confirmed) return;
                    user = dialog.Value;
                    PersistenceFeedback.Check(logins.Set(device.MacAddress, ip, user), logins.Save, () => logins.SaveError);
                }
                // Invoke the SSH client directly; user input never goes through a command shell.
                command = "ssh.exe " + (servicePort.HasValue ? "-p " + servicePort.Value + " " : "")
                    + (string.IsNullOrWhiteSpace(user) ? "" : "-l " + QuoteArgument(user) + " ") + ip;
            }
            else if (action.StartsWith("Ping", StringComparison.Ordinal)) command = "ping.exe -t " + ip;
            else return;
            if (action.EndsWith("External", StringComparison.Ordinal))
            {
                int separator = command.IndexOf(' ');
                Process.Start(new ProcessStartInfo(SystemTools.Path(command[..separator]), command[(separator + 1)..])
                { UseShellExecute = true, WorkingDirectory = SystemTools.WorkingDirectory });
            }
            // Ping runs inside the styled shell, which is what tints the replies. SSH does not:
            // its command line carries a username the user typed, and that has to reach the
            // client as an argument rather than as text a shell gets to interpret.
            // The terminal starts it through CreateProcess, which also looks beside the exe first,
            // so it gets the same full path as the external window.
            else if (action.StartsWith("Ssh", StringComparison.Ordinal))
            {
                if (!ConfirmReplaceTerminal("SSH " + ip)) return;
                int separator = command.IndexOf(' ');
                NewTerminal(QuoteArgument(SystemTools.Path(command[..separator])) + command[separator..], "SSH " + ip);
            }
            else if (ConfirmReplaceTerminal("Ping " + ip)) NewTerminal(title: "Ping " + ip, shellCommand: PingCommand(ip));
        }

        /// <summary>A device's Ping or SSH takes over the terminal. A session or command still running
        /// there (an open SSH login, a ping -t) is ended only after asking, as the speed test does.</summary>
        private bool ConfirmReplaceTerminal(string what)
        {
            if (_terminalControl?.HasRunningCommand != true) return true;
            var dialog = new Controls.ConfirmDialog(what, string.Format(Loc("Str_Term_ReplaceRunning"), what),
                Loc("Str_Term_Replace"), Loc("Str_Btn_Cancel")) { Owner = this };
            dialog.ShowDialog();
            return dialog.Confirmed;
        }

        private void ClipWorkspaceSurface()
        {
            // Per corner: the pane squares the corners on the side its tabs join, and the content
            // (the table header included) has to square with it.
            double w = TerminalLayout.ActualWidth, h = TerminalLayout.ActualHeight;
            if (w <= 0 || h <= 0) return;
            var c = DevicesPane.CornerRadius;
            var figure = new PathFigure { StartPoint = new Point(c.TopLeft, 0), IsClosed = true };
            void Corner(Point line, Point end, double radius)
            {
                figure.Segments.Add(new LineSegment(line, false));
                if (radius > 0) figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, false, SweepDirection.Clockwise, false));
            }
            Corner(new Point(w - c.TopRight, 0), new Point(w, c.TopRight), c.TopRight);
            Corner(new Point(w, h - c.BottomRight), new Point(w - c.BottomRight, h), c.BottomRight);
            Corner(new Point(c.BottomLeft, h), new Point(0, h - c.BottomLeft), c.BottomLeft);
            Corner(new Point(0, c.TopLeft), new Point(c.TopLeft, 0), c.TopLeft);
            var clip = new PathGeometry(new[] { figure });
            clip.Freeze();
            TerminalLayout.Clip = clip;
        }

        private static string QuoteArgument(string value)
        {
            var result = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
                result.Append(c); slashes = 0;
            }
            result.Append('\\', slashes * 2); return result.Append('"').ToString();
        }
    }
}
