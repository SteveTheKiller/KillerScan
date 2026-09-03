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
            Grid.SetColumn(_toolbarOverflow, 1);
            _workspaceToolbar.Children.Add(_toolbarOverflow);
            var toolbarSurface = new Grid { UseLayoutRounding = true };
            toolbarSurface.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
            var grain = new Border { IsHitTestVisible = false };
            grain.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
            grain.SetResourceReference(OpacityProperty, "GrainOpacity");
            toolbarSurface.Children.Add(_workspaceToolbar);
            toolbarSurface.Children.Add(grain);
            WorkspaceHost.Children.Add(toolbarSurface);
            TerminalLayout.Children.Add(_workspaceBody);
            TerminalLayout.SizeChanged += (_, _) => ClipWorkspaceSurface();
            NewScan(_startupScanTarget ?? string.Empty);
        }

        private void ShowWorkspaceContent(FrameworkElement content, string view)
        {
            if (!_workspaceBody.Children.Contains(content)) _workspaceBody.Children.Add(content);
            _selectedWorkspace = content;
            _workspaceView = view;
            foreach (FrameworkElement child in _workspaceBody.Children)
                child.Visibility = child == content ? Visibility.Visible : Visibility.Collapsed;
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
                scanToolbar.VerticalAlignment = VerticalAlignment.Center;
                _workspaceToolbar.Children.Add(scanToolbar);
                var networkDetails = new StackPanel();
                foreach (string name in new[] { "LocalIpLabel", "GatewayLabel", "DnsLabel", "InterfaceLabel" })
                {
                    var label = (TextBlock)_scanWorkspace.FindName(name);
                    var group = (FrameworkElement)label.Parent;
                    ((Panel)group.Parent).Children.Remove(group);
                    networkDetails.Children.Add(group);
                }
                NetworkFooter.ToolTip = networkDetails;
                NetworkFooter.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Text")
                    { Source = _scanWorkspace.FindName("LocalIpLabel") });
                var count = (TextBlock)_scanWorkspace.FindName("DeviceCount");
                ((Panel)count.Parent).Children.Remove(count);
                count.Margin = new Thickness(8, 0, 0, 0);
                count.FontSize = 10;
                DeviceCountFooter.Children.Add(count);
                var export = (Button)_scanWorkspace.FindName("ExportButton");
                ((Panel)export.Parent).Children.Remove(export);
                export.ClearValue(Control.TemplateProperty);
                export.ClearValue(Control.BackgroundProperty);
                export.Margin = new Thickness(0, 0, 2, 0);
                export.SetResourceReference(StyleProperty, "ViewToolbarButton");
                _viewAppearance.Add(export, ("\uE896", "Str_TT_Export"));
                _workspaceNavigation.Children.Insert(0, export);
                ApplyToolbarAppearance();
                _scanWorkspace.DeviceAction += (_, e) => WorkspaceDeviceAction(e.Device, e.Action);
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
                };
                _scanWorkspace.ApplyScale(_appScale);
            }
            else if (!string.IsNullOrWhiteSpace(target) && !_scanWorkspace.IsScanning)
                _scanWorkspace.Targets = target;
            ShowScanView("devices");
            return _scanWorkspace;
        }

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
                : _workspaceView == "services" ? Loc("Str_Services_Title")
                : _workspaceView == "diagnostics" ? Loc("Str_Diag_Title") : string.Empty);
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
            else NewTerminal(command, (action.StartsWith("Ssh", StringComparison.Ordinal) ? "SSH " : "Ping ") + ip);
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
