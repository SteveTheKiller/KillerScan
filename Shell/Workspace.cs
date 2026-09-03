using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerScan.Controls;
using KillerScan.Models;
using KillerScan.Services;
using KillerScan.Terminal;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private sealed class WorkspaceTab
        {
            internal FrameworkElement Content = null!;
            internal string Title = string.Empty;
            internal string? TitleKey;
            internal string TitleSuffix = string.Empty;
            internal string Status = string.Empty;
        }

        private sealed class WorkspacePane
        {
            internal readonly Grid Root = new();
            internal readonly DockPanel Header = new() { LastChildFill = true };
            internal readonly StackPanel Strip = new() { Orientation = Orientation.Horizontal };
            internal readonly ContentControl Body = new();
            internal readonly List<WorkspaceTab> Tabs = new();
            internal WorkspaceTab? Selected;
        }

        private readonly WorkspacePane _leftWorkspace = new();
        private readonly WorkspacePane _rightWorkspace = new();
        private WorkspacePane? _focusedWorkspace;
        private GridSplitter? _workspaceSplitter;
        private bool _workspaceSplit;
        private WorkspacePane CurrentPane => _focusedWorkspace ?? _leftWorkspace;
        private ScanWorkspace? ActiveScan => CurrentPane.Selected?.Content as ScanWorkspace;

        private void InitializeWorkspace()
        {
            WorkspaceHost.ColumnDefinitions.Add(new ColumnDefinition());
            WorkspaceHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            WorkspaceHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
            BuildWorkspacePane(_leftWorkspace, 0);
            BuildWorkspacePane(_rightWorkspace, 2);
            _workspaceSplitter = new GridSplitter
            {
                Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext, Visibility = Visibility.Collapsed
            };
            _workspaceSplitter.SetResourceReference(BackgroundProperty, "PaneBorderBrush");
            Grid.SetColumn(_workspaceSplitter, 1);
            WorkspaceHost.Children.Add(_workspaceSplitter);
            _rightWorkspace.Root.Visibility = Visibility.Collapsed;
            _focusedWorkspace = _leftWorkspace;
            NewScan(_startupScanTarget ?? string.Empty);
        }

        private void BuildWorkspacePane(WorkspacePane pane, int column)
        {
            pane.Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pane.Root.RowDefinitions.Add(new RowDefinition());
            pane.Root.PreviewMouseDown += (_, _) => ActivatePane(pane);
            pane.Root.GotKeyboardFocus += (_, _) => ActivatePane(pane);
            var header = pane.Header;
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var add = WorkspaceButton("+", "Str_Workspace_NewScan", () => ShowWorkspaceMenu(pane, (FrameworkElement)actions.Children[0]));
            actions.Children.Add(add);
            actions.Children.Add(WorkspaceButton("\uE89F", "Str_Workspace_Split", ToggleWorkspaceSplit, true));
            DockPanel.SetDock(actions, Dock.Right);
            header.Children.Add(actions);
            header.Children.Add(new ScrollViewer
            {
                Content = pane.Strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            pane.Root.Children.Add(header);
            Grid.SetRow(pane.Body, 1);
            pane.Root.Children.Add(pane.Body);
            Grid.SetColumn(pane.Root, column);
            WorkspaceHost.Children.Add(pane.Root);
        }

        private Button WorkspaceButton(string text, string tooltip, Action click, bool glyph = false)
        {
            var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2), MinWidth = 26, VerticalAlignment = VerticalAlignment.Center };
            button.SetResourceReference(StyleProperty, "SurfaceButton");
            button.SetResourceReference(ToolTipProperty, tooltip);
            if (glyph) button.FontFamily = new FontFamily("Segoe MDL2 Assets");
            button.Click += (_, _) => click();
            return button;
        }

        private void ShowWorkspaceMenu(WorkspacePane pane, FrameworkElement anchor)
        {
            ActivatePane(pane);
            var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Add(string key, string shortcut, Action action)
            {
                var item = new MenuItem { Header = Loc(key), InputGestureText = shortcut };
                item.Click += (_, _) => action(); menu.Items.Add(item);
            }
            Add("Str_Workspace_NewScan", "Ctrl+T", () => NewScan());
            Add("Str_Workspace_Terminal", "Ctrl+Shift+T", () => NewTerminal());
            Add("Str_Watch_Title", "F2", () => Watch_Click(this, new RoutedEventArgs()));
            menu.Items.Add(new Separator());
            Add("Str_Workspace_Move", "", MoveWorkspaceTab);
            Add("Str_Workspace_Close", "Ctrl+W", CloseWorkspaceTab);
            menu.IsOpen = true;
        }

        private void ActivatePane(WorkspacePane pane)
        {
            _focusedWorkspace = pane;
            if (pane.Selected?.Content is ScanWorkspace scan) StatusText.Text = scan.Status;
            else StatusText.Text = pane.Selected?.Status ?? string.Empty;
            UpdateWorkspaceRail();
        }

        private void RenderWorkspaceTabs(WorkspacePane pane)
        {
            pane.Strip.Children.Clear();
            foreach (var tab in pane.Tabs)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                string title = (tab.TitleKey == null ? tab.Title : Loc(tab.TitleKey)) + tab.TitleSuffix;
                var select = WorkspaceButton(title, "Str_Workspace_Move", () => SelectWorkspaceTab(pane, tab));
                select.MaxWidth = 260;
                select.ToolTip = title;
                if (tab == pane.Selected) select.SetResourceReference(StyleProperty, "OutlineButton");
                row.Children.Add(select);
                row.Children.Add(WorkspaceButton("\u00D7", "Str_Workspace_Close", () => CloseWorkspaceTab(pane, tab)));
                var menu = new ContextMenu();
                var move = new MenuItem { Header = Loc("Str_Workspace_Move") };
                move.Click += (_, _) => { SelectWorkspaceTab(pane, tab); MoveWorkspaceTab(); };
                menu.Items.Add(move); row.ContextMenu = menu;
                pane.Strip.Children.Add(row);
            }
        }

        private void SelectWorkspaceTab(WorkspacePane pane, WorkspaceTab tab)
        {
            if (!pane.Tabs.Contains(tab)) return;
            pane.Selected = tab;
            pane.Body.Content = tab.Content;
            ActivatePane(pane);
            RenderWorkspaceTabs(pane);
            if (tab.Content is TerminalControl terminal) terminal.Focus();
        }

        private void AddWorkspaceTab(WorkspaceTab tab, bool beside)
        {
            var pane = CurrentPane;
            if (beside)
            {
                SetWorkspaceSplit(true);
                pane = pane == _leftWorkspace ? _rightWorkspace : _leftWorkspace;
            }
            pane.Tabs.Add(tab);
            SelectWorkspaceTab(pane, tab);
        }

        private ScanWorkspace NewScan(string target = "", bool beside = false)
        {
            var scan = new ScanWorkspace(target, DemoMode);
            var tab = new WorkspaceTab { Content = scan, Title = scan.Targets };
            scan.DeviceAction += (_, e) => WorkspaceDeviceAction(e.Device, e.Action, e.Beside);
            scan.StateChanged += (_, _) =>
            {
                tab.Title = scan.Targets;
                foreach (var pane in WorkspacePanes())
                    if (pane.Tabs.Contains(tab)) RenderWorkspaceTabs(pane);
                if (ActiveScan == scan) { StatusText.Text = scan.Status; UpdateWorkspaceRail(); }
            };
            scan.ApplyScale(_appScale);
            AddWorkspaceTab(tab, beside);
            return scan;
        }

        private void NewTerminal(string? command = null, string? title = null, bool beside = false)
        {
            var terminal = new TerminalControl();
            var tab = new WorkspaceTab { Content = terminal, Title = title ?? string.Empty, TitleKey = title == null ? "Str_Workspace_Terminal" : null };
            terminal.LayoutTransform = new ScaleTransform(_appScale, _appScale);
            void Status(string text)
            {
                tab.Status = text;
                if (CurrentPane.Selected == tab) StatusText.Text = text;
            }
            terminal.Exited += code => Status(string.Format(Loc("Str_Workspace_Exited"), code));
            terminal.StartFailed += error => Status(string.Format(Loc("Str_Workspace_StartFailed"), error.Message));
            AddWorkspaceTab(tab, beside);
            string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            terminal.Start(command ?? "\"" + shell + "\" -NoLogo", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        }

        private IEnumerable<WorkspacePane> WorkspacePanes() { yield return _leftWorkspace; yield return _rightWorkspace; }
        private void ToggleWorkspaceSplit() => SetWorkspaceSplit(!_workspaceSplit);
        private void SetWorkspaceSplit(bool split)
        {
            if (_workspaceSplit == split) return;
            _workspaceSplit = split;
            if (!split)
            {
                var selected = CurrentPane.Selected;
                _rightWorkspace.Body.Content = null;
                _leftWorkspace.Tabs.AddRange(_rightWorkspace.Tabs);
                _rightWorkspace.Tabs.Clear(); _rightWorkspace.Selected = null;
                _focusedWorkspace = _leftWorkspace;
                RenderWorkspaceTabs(_rightWorkspace);
                if (selected != null) SelectWorkspaceTab(_leftWorkspace, selected);
                else if (_leftWorkspace.Selected == null && _leftWorkspace.Tabs.Count > 0)
                    SelectWorkspaceTab(_leftWorkspace, _leftWorkspace.Tabs[0]);
                else RenderWorkspaceTabs(_leftWorkspace);
            }
            WorkspaceHost.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            WorkspaceHost.ColumnDefinitions[1].Width = new GridLength(split ? 5 : 0);
            WorkspaceHost.ColumnDefinitions[2].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _rightWorkspace.Root.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            if (_workspaceSplitter != null) _workspaceSplitter.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MoveWorkspaceTab()
        {
            var source = CurrentPane;
            var tab = source.Selected;
            if (tab == null) return;
            SetWorkspaceSplit(true);
            var target = source == _leftWorkspace ? _rightWorkspace : _leftWorkspace;
            source.Body.Content = null; source.Tabs.Remove(tab); source.Selected = null;
            if (source.Tabs.Count > 0) SelectWorkspaceTab(source, source.Tabs[0]);
            else RenderWorkspaceTabs(source);
            target.Tabs.Add(tab); SelectWorkspaceTab(target, tab);
        }

        private void CloseWorkspaceTab() { if (CurrentPane.Selected is WorkspaceTab tab) CloseWorkspaceTab(CurrentPane, tab); }
        private void CloseWorkspaceTab(WorkspacePane pane, WorkspaceTab tab)
        {
            int index = pane.Tabs.IndexOf(tab);
            if (index < 0) return;
            if (pane.Selected == tab) { pane.Body.Content = null; pane.Selected = null; }
            pane.Tabs.Remove(tab);
            (tab.Content as IDisposable)?.Dispose();
            if (pane.Selected == null && pane.Tabs.Count > 0) SelectWorkspaceTab(pane, pane.Tabs[Math.Min(index, pane.Tabs.Count - 1)]);
            else RenderWorkspaceTabs(pane);
            if (_leftWorkspace.Tabs.Count + _rightWorkspace.Tabs.Count == 0) { ActivatePane(_leftWorkspace); NewScan(); }
        }

        private void NextWorkspaceTab()
        {
            var pane = CurrentPane;
            if (pane.Tabs.Count > 0) SelectWorkspaceTab(pane, pane.Tabs[(pane.Tabs.IndexOf(pane.Selected!) + 1) % pane.Tabs.Count]);
        }
        private void DisposeWorkspace()
        {
            foreach (var tab in WorkspacePanes().SelectMany(p => p.Tabs)) (tab.Content as IDisposable)?.Dispose();
        }
        private void ApplyWorkspaceScale(double scale)
        {
            foreach (var pane in WorkspacePanes())
            {
                pane.Header.LayoutTransform = new ScaleTransform(scale, scale);
                foreach (var tab in pane.Tabs)
                    if (tab.Content is ScanWorkspace scan) scan.ApplyScale(scale);
                    else if (tab.Content is NetworkToolsWindow tools) tools.ApplyScale(scale);
                    else tab.Content.LayoutTransform = new ScaleTransform(scale, scale);
            }
        }
        private void RefreshWorkspaceLocale()
        {
            foreach (var pane in WorkspacePanes())
            {
                RenderWorkspaceTabs(pane);
                foreach (var tab in pane.Tabs)
                    if (tab.Content is ScanWorkspace scan) scan.RefreshLocale();
                    else if (tab.Content is TerminalControl terminal) terminal.RefreshTheme();
            }
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
                Process.Start(new ProcessStartInfo(command.Substring(0, separator), command.Substring(separator + 1)) { UseShellExecute = true });
            }
            else NewTerminal(command, (action.StartsWith("Ssh", StringComparison.Ordinal) ? "SSH " : "Ping ") + ip, beside);
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
