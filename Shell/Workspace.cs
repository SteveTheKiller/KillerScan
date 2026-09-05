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
        private readonly StackPanel _workspaceNavigation = new() { Orientation = Orientation.Horizontal };
        private readonly Grid _workspaceToolbar = new();
        private readonly Dictionary<string, Button> _viewButtons = [];
        private ScanWorkspace? _scanWorkspace;
        private FrameworkElement? _selectedWorkspace;
        private string _workspaceView = "scan";
        private ScanWorkspace? ActiveScan => _selectedWorkspace == _scanWorkspace ? _scanWorkspace : null;

        private void InitializeWorkspace()
        {
            WorkspaceHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WorkspaceHost.RowDefinitions.Add(new RowDefinition());
            BuildWorkspaceNavigation();
            _workspaceToolbar.ColumnDefinitions.Add(new ColumnDefinition());
            _workspaceToolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_workspaceNavigation, 1);
            _workspaceToolbar.Children.Add(_workspaceNavigation);
            var toolbarSurface = new Grid { UseLayoutRounding = true };
            toolbarSurface.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
            var grain = new Border { IsHitTestVisible = false };
            grain.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
            grain.SetResourceReference(OpacityProperty, "GrainOpacity");
            // Grain first, so it textures the bar itself and the controls sit ON it. Added after
            // the toolbar it was painting over every control in the bar, which is why the subnet
            // box looked like textured background rather than a field you can type in.
            toolbarSurface.Children.Add(grain);
            toolbarSurface.Children.Add(_workspaceToolbar);
            WorkspaceHost.Children.Add(toolbarSurface);
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
            _workspaceToolbar.Children.Add(toolbar);
            UpdateViewToolbar();
        }

        private void UpdateViewToolbar()
        {
            // Scan owns the bar for every view built on the scan workspace; the rest fall back
            // to it too, so switching to a view without its own controls is never a blank bar.
            string key = _viewToolbars.ContainsKey(_workspaceView) ? _workspaceView : "scan";
            foreach (var pair in _viewToolbars)
                pair.Value.Visibility = pair.Key == key ? Visibility.Visible : Visibility.Collapsed;
            // The bars are different widths, so what fits beside them changes with the view.
            FitToolbarViews();
        }

        private void ShowWorkspaceContent(FrameworkElement content, string view)
        {
            if (!_workspaceBody.Children.Contains(content)) _workspaceBody.Children.Add(content);
            _selectedWorkspace = content;
            _workspaceView = view;
            // The export menu lives on the rail whatever is in front, so it is told which view it
            // is acting for. Anything built on the scan workspace resolves itself.
            if (_scanWorkspace != null)
                _scanWorkspace.ExportContext = view is "watch" or "terminal" ? view : "scan";
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
                RegisterViewToolbar("scan", scanToolbar);
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
                NetworkFooterDetail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text")
                    { Source = _scanWorkspace.FindName("FooterNetDetail") });
                NetworkFooterIcon.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text")
                    { Source = _scanWorkspace.FindName("InterfaceIcon") });
                // The badge appears and disappears with portable mode, and the address changes
                // width with the network, so the fit is re-run on both rather than once.
                UpdateScanLight();
                FooterBar.SizeChanged += (_, _) => FitFooterNetwork();
                PortableBadge.IsVisibleChanged += (_, _) => FitFooterNetwork();
                NetworkFooter.SizeChanged += (_, _) => FitFooterNetwork();
                FitFooterNetwork();
                var count = (TextBlock)_scanWorkspace.FindName("DeviceCount");
                ((Panel)count.Parent).Children.Remove(count);
                count.Margin = new Thickness(8, 0, 0, 0);
                count.FontSize = 10;
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
                var exportTip = new TextBlock();
                var exportCaption = new System.Windows.Documents.Run();
                exportCaption.SetResourceReference(System.Windows.Documents.Run.TextProperty, "Str_TT_Export");
                exportTip.Inlines.Add(exportCaption);
                exportTip.Inlines.Add(" (Ctrl+E)");
                export.ToolTip = exportTip;
                export.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, "Str_TT_Export");
                RailButtons.Children.Insert(0, export);
                ApplyToolbarAppearance();
                _scanWorkspace.DeviceAction += (_, e) => WorkspaceDeviceAction(e.Device, e.Action);
                _scanWorkspace.ShellExportRequested += ShellExport;
                _scanWorkspace.HistoryRecorded += (_, _) =>
                {
                    if (!_sidebarCollapsed) RefreshHistoryList();
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
            if (NetworkFooterDetail == null) return;

            if (!_footerNetExpanded)
            {
                NetworkFooterDetail.Visibility = Visibility.Collapsed;
                NetworkFooterDetail.MaxWidth = double.PositiveInfinity;
                return;
            }

            double footer = FooterBar.ActualWidth;
            if (footer <= 0) return;

            // Half the bar, less half the badge, is where the right cell has to stop. With no
            // badge showing the only limit is the bar itself.
            double badge = PortableBadge.Visibility == Visibility.Visible ? PortableBadge.ActualWidth : 0;
            double budget = footer / 2 - badge / 2 - FooterNetworkGap
                          - VersionLabel.ActualWidth - NetworkFooter.ActualWidth - NetworkFooterIcon.ActualWidth;

            // Below the threshold there is not enough room for the ellipsis to say anything
            // useful, so the detail goes entirely rather than reading as "Int...".
            if (budget < FooterDetailMinimum)
            {
                NetworkFooterDetail.Visibility = Visibility.Collapsed;
                NetworkFooterDetail.MaxWidth = double.PositiveInfinity;
            }
            else
            {
                NetworkFooterDetail.Visibility = Visibility.Visible;
                NetworkFooterDetail.MaxWidth = budget;
            }
        }

        /// <summary>Padding and cell margins the budget above cannot measure directly.</summary>
        private const double FooterNetworkGap = 48;

        /// <summary>Narrower than this and the adapter and speed are dropped rather than trimmed.</summary>
        private const double FooterDetailMinimum = 70;

        private void ShowScanView(string view)
        {
            if (_scanWorkspace == null) return;
            _scanWorkspace.SetView(view);
            ShowWorkspaceContent(_scanWorkspace, view == "topology" ? "topology"
                : view == "services" ? "services" : "scan");
        }

        private void UpdateWorkspaceStatus()
        {
            StatusText.Text = ActiveScan?.Status ?? (_workspaceView == "watch" ? Loc("Str_View_KeepAlive")
                : _workspaceView == "history" ? Loc("Str_History_Title")
                : _workspaceView == "services" ? Loc("Str_Services_Title") : string.Empty);
            ScanProgress.Value = ActiveScan?.Progress ?? 0;
            ScanProgress.Visibility = ActiveScan?.IsProgressVisible == true ? Visibility.Visible : Visibility.Collapsed;
            if (_workspaceView == "terminal") UpdateTerminalPanelStatus();
            if (_scanWorkspace?.FindName("DeviceCount") is TextBlock count)
                count.Visibility = !string.IsNullOrEmpty(count.Text) && StatusText.Text.Contains(count.Text)
                    ? Visibility.Collapsed : Visibility.Visible;
        }

        private void DisposeWorkspace()
        {
            DisposeTerminalPanel();
            foreach (var content in _workspaceBody.Children.OfType<FrameworkElement>())
                if (content != _terminalControl) (content as IDisposable)?.Dispose();
        }

        private void ApplyWorkspaceScale(double scale)
        {
            _workspaceToolbar.LayoutTransform = new ScaleTransform(scale, scale);
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

        private void WorkspaceDeviceAction(NetworkDevice device, string action)
        {
            if (!IPAddress.TryParse(device.IpAddress, out var address)) return;
            string ip = address.ToString();
            if (action == "Watch" || action == "Diagnose") { OpenNetworkTool(device, action == "Diagnose"); return; }
            if (action == "Browser") { Process.Start(new ProcessStartInfo("http://" + ip) { UseShellExecute = true }); return; }
            if (action == "Rdp") { Process.Start(new ProcessStartInfo("mstsc.exe", "/v:" + ip) { UseShellExecute = true }); return; }
            string command;
            if (action.StartsWith("Ssh", StringComparison.Ordinal))
            {
                string? user = DeviceLogins.Get(device.MacAddress, ip);
                if (user == null || action.StartsWith("SshAs", StringComparison.Ordinal))
                {
                    var dialog = new InputDialog(string.Format(Loc("Str_Ssh_Head"), ip), Loc("Str_Ssh_Detail"), Loc("Str_Ssh_User"), user ?? "", Loc("Str_Ssh_Connect"), Loc("Str_Btn_Cancel")) { Owner = this };
                    dialog.ShowDialog(); if (!dialog.Confirmed) return;
                    user = dialog.Value; DeviceLogins.Set(device.MacAddress, ip, user);
                }
                // Invoke the SSH client directly; user input never goes through a command shell.
                command = "ssh.exe " + (string.IsNullOrWhiteSpace(user) ? "" : "-l " + QuoteArgument(user) + " ") + ip;
            }
            else if (action.StartsWith("Ping", StringComparison.Ordinal)) command = "ping.exe -t " + ip;
            else return;
            if (action.EndsWith("External", StringComparison.Ordinal))
            {
                int separator = command.IndexOf(' ');
                Process.Start(new ProcessStartInfo(command[..separator], command[(separator + 1)..]) { UseShellExecute = true });
            }
            // Ping runs inside the styled shell, which is what tints the replies. SSH does not:
            // its command line carries a username the user typed, and that has to reach the
            // client as an argument rather than as text a shell gets to interpret.
            else if (action.StartsWith("Ssh", StringComparison.Ordinal)) NewTerminal(command, "SSH " + ip);
            else NewTerminal(title: "Ping " + ip, shellCommand: PingCommand(ip));
        }

        private void ClipWorkspaceSurface()
        {
            if (TryFindResource("RailSeparatorMargin") is Thickness railMargin)
            {
                railMargin.Top += WorkspaceHost.RowDefinitions[0].ActualHeight;
                WorkspaceRailSeparator.Margin = railMargin;
            }
            double radius = DevicesPane.CornerRadius.BottomLeft;
            if (TerminalLayout.ActualWidth > 0 && TerminalLayout.ActualHeight > 0)
                TerminalLayout.Clip = new RectangleGeometry(new Rect(0, 0, TerminalLayout.ActualWidth, TerminalLayout.ActualHeight), radius, radius);
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
