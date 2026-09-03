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
        private readonly WrapPanel _workspaceNavigation = new();
        private readonly Dictionary<string, Button> _viewButtons = new();
        private ScanWorkspace? _scanWorkspace;
        private FrameworkElement? _selectedWorkspace;
        private string _workspaceView = "scan";
        private ScanWorkspace? ActiveScan => _selectedWorkspace == _scanWorkspace ? _scanWorkspace : null;

        private void InitializeWorkspace()
        {
            WorkspaceHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            WorkspaceHost.RowDefinitions.Add(new RowDefinition());
            BuildWorkspaceNavigation();
            WorkspaceHost.Children.Add(_workspaceNavigation);
            Grid.SetRow(_workspaceBody, 1);
            WorkspaceHost.Children.Add(_workspaceBody);
            DevicesPane.SizeChanged += (_, _) => ClipWorkspaceSurface();
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

        private ScanWorkspace NewScan(string target = "", bool beside = false)
        {
            if (_scanWorkspace == null)
            {
                _scanWorkspace = new ScanWorkspace(target, DemoMode);
                _scanWorkspace.DeviceAction += (_, e) => WorkspaceDeviceAction(e.Device, e.Action);
                _scanWorkspace.StateChanged += (_, _) =>
                {
                    if (ActiveScan != null)
                    {
                        _workspaceView = ActiveScan.View == "topology" ? "topology" : "scan";
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
            ShowWorkspaceContent(_scanWorkspace, view == "topology" ? "topology" : "scan");
        }

        private void UpdateWorkspaceStatus()
        {
            StatusText.Text = ActiveScan?.Status ?? (_workspaceView == "watch" ? Loc("Str_View_KeepAlive")
                : _workspaceView == "history" ? Loc("Str_History_Title")
                : _workspaceView == "diagnostics" ? Loc("Str_Diag_Title") : string.Empty);
            ScanProgress.Value = ActiveScan?.Progress ?? 0;
            ScanProgress.Visibility = ActiveScan?.IsProgressVisible == true ? Visibility.Visible : Visibility.Collapsed;
            if (_workspaceView == "terminal") UpdateTerminalPanelStatus();
        }

        private void DisposeWorkspace()
        {
            DisposeTerminalPanel();
            foreach (var content in _workspaceBody.Children.OfType<FrameworkElement>())
                if (content != _terminalControl) (content as IDisposable)?.Dispose();
        }

        private void ApplyWorkspaceScale(double scale)
        {
            _workspaceNavigation.LayoutTransform = new ScaleTransform(scale, scale);
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
            UpdateWorkspaceNavigation();
            UpdateWorkspaceStatus();
        }

        private void WorkspaceDeviceAction(NetworkDevice device, string action, bool beside = false)
        {
            if (!IPAddress.TryParse(device.IpAddress, out var address)) return;
            string ip = address.ToString();
            if (action == "Watch" || action == "Diagnose") { OpenNetworkTool(device, action == "Diagnose", beside); return; }
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
            else NewTerminal(command, (action.StartsWith("Ssh", StringComparison.Ordinal) ? "SSH " : "Ping ") + ip, beside);
        }

        private void ClipWorkspaceSurface()
        {
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
