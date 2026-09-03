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
            internal readonly System.Windows.Controls.Primitives.UniformGrid Strip = new() { Rows = 1 };
            internal readonly ContentControl Body = new();
            internal readonly Grid EmptyBackdrop = new();
            internal readonly Border Frame = new();
            internal readonly Border HeaderLine = new() { Height = 1, VerticalAlignment = VerticalAlignment.Bottom };
            internal readonly List<WorkspaceTab> Tabs = [];
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
                Width = 7, HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext, Visibility = Visibility.Collapsed
            };
            _workspaceSplitter.Background = Brushes.Transparent;
            _workspaceSplitter.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='GridSplitter'>" +
                "<Grid Background='Transparent'><Border Width='1' HorizontalAlignment='Center' Background='{DynamicResource PaneBorderBrush}'/></Grid></ControlTemplate>");
            Grid.SetColumn(_workspaceSplitter, 1);
            WorkspaceHost.Children.Add(_workspaceSplitter);
            _rightWorkspace.Root.Visibility = Visibility.Collapsed;
            _focusedWorkspace = _leftWorkspace;
            InitializeTerminalPanel();
            DevicesPane.SizeChanged += (_, _) => ClipWorkspaceSurface();
            NewScan(_startupScanTarget ?? string.Empty);
        }

        private void BuildWorkspacePane(WorkspacePane pane, int column)
        {
            pane.Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pane.Root.RowDefinitions.Add(new RowDefinition());
            pane.Root.PreviewMouseDown += (_, _) => ActivatePane(pane);
            pane.Root.GotKeyboardFocus += (_, _) => ActivatePane(pane);
            var header = pane.Header;
            var chrome = new Grid();
            chrome.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
            var grain = new Border { IsHitTestVisible = false };
            grain.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
            grain.SetResourceReference(OpacityProperty, "GrainOpacity");
            chrome.Children.Add(grain);
            chrome.Children.Add(pane.HeaderLine);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            var add = WorkspaceButton("+", "Str_Workspace_NewScan", () => ShowWorkspaceMenu(pane, (FrameworkElement)actions.Children[0]));
            actions.Children.Add(add);
            actions.Children.Add(WorkspaceButton("\uE756", "Str_Workspace_Terminal", ToggleTerminalPanel, true));
            DockPanel.SetDock(actions, Dock.Right);
            header.Children.Add(actions);
            header.Children.Add(new ScrollViewer
            {
                Content = pane.Strip, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
            chrome.Children.Add(header);
            pane.Root.Children.Add(chrome);
            pane.EmptyBackdrop.SetResourceReference(Panel.BackgroundProperty, "ScanContentPaneBrush");
            var emptyGrain = new Border { IsHitTestVisible = false };
            emptyGrain.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
            emptyGrain.SetResourceReference(OpacityProperty, "GrainOpacity");
            pane.EmptyBackdrop.Children.Add(emptyGrain);
            var body = new Grid();
            body.Children.Add(pane.EmptyBackdrop);
            body.Children.Add(pane.Body);
            pane.Frame.Child = body;
            pane.Frame.SizeChanged += (_, _) => ClipWorkspacePaneBottom(pane);
            pane.Frame.BorderThickness = new Thickness(1, 0, 1, 1);
            Grid.SetRow(pane.Frame, 1);
            pane.Root.Children.Add(pane.Frame);
            Grid.SetColumn(pane.Root, column);
            WorkspaceHost.Children.Add(pane.Root);
        }

        private Button WorkspaceButton(string text, string tooltip, Action click, bool glyph = false)
        {
            var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2), MinWidth = 26, VerticalAlignment = VerticalAlignment.Center };
            button.SetResourceReference(StyleProperty, "ChromeButton");
            button.Width = button.Height = 26;
            var tip = new TextBlock();
            var caption = new System.Windows.Documents.Run();
            caption.SetResourceReference(System.Windows.Documents.Run.TextProperty, tooltip);
            tip.Inlines.Add(caption);
            tip.Inlines.Add(tooltip == "Str_Workspace_Terminal" ? " (Ctrl+Shift+T)" : " (Ctrl+T)");
            button.ToolTip = tip;
            button.FontFamily = new FontFamily(glyph ? "Segoe MDL2 Assets" : "Consolas");
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
            Add("Str_Workspace_Terminal", "Ctrl+Shift+T", ToggleTerminalPanel);
            Add("Str_Watch_Title", "F2", () => Watch_Click(this, new RoutedEventArgs()));
            menu.Items.Add(new Separator());
            Add("Str_Workspace_Move", "", MoveWorkspaceTab);
            Add("Str_Workspace_Close", "Ctrl+W", CloseWorkspaceTab);
            menu.IsOpen = true;
        }

        private void ActivatePane(WorkspacePane pane)
        {
            _focusedWorkspace = pane;
            foreach (var workspace in WorkspacePanes()) UpdateWorkspacePaneAppearance(workspace);
            if (pane.Selected?.Content is ScanWorkspace scan) StatusText.Text = scan.Status;
            else StatusText.Text = pane.Selected?.Status ?? string.Empty;
            ScanProgress.Value = ActiveScan?.Progress ?? 0;
            ScanProgress.Visibility = ActiveScan?.IsProgressVisible == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateWorkspaceRail();
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
                if (tab.Title != scan.Targets)
                {
                    tab.Title = scan.Targets;
                    foreach (var pane in WorkspacePanes())
                        if (pane.Tabs.Contains(tab)) RenderWorkspaceTabs(pane);
                }
                if (ActiveScan == scan && _terminalControl?.IsKeyboardFocusWithin != true) ActivatePane(CurrentPane);
            };
            scan.ApplyScale(_appScale);
            AddWorkspaceTab(tab, beside);
            return scan;
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
            WorkspaceHost.ColumnDefinitions[1].Width = new GridLength(split ? 7 : 0);
            WorkspaceHost.ColumnDefinitions[2].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _rightWorkspace.Root.Visibility = split ? Visibility.Visible : Visibility.Collapsed;
            _workspaceSplitter?.SetCurrentValue(VisibilityProperty, split ? Visibility.Visible : Visibility.Collapsed);
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
            if (CurrentPane == pane) ActivatePane(pane);
            if (_leftWorkspace.Tabs.Count + _rightWorkspace.Tabs.Count == 0) { ActivatePane(_leftWorkspace); NewScan(); }
        }

        private void NextWorkspaceTab()
        {
            var pane = CurrentPane;
            if (pane.Tabs.Count > 0) SelectWorkspaceTab(pane, pane.Tabs[(pane.Tabs.IndexOf(pane.Selected!) + 1) % pane.Tabs.Count]);
        }
        private void DisposeWorkspace()
        {
            DisposeTerminalPanel();
            foreach (var tab in WorkspacePanes().SelectMany(p => p.Tabs)) (tab.Content as IDisposable)?.Dispose();
        }
        private void ApplyWorkspaceScale(double scale)
        {
            ApplyTerminalPanelScale(scale);
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
            RefreshTerminalPanelTheme();
            ClipWorkspaceSurface();
            foreach (var pane in WorkspacePanes())
            {
                RenderWorkspaceTabs(pane);
                foreach (var tab in pane.Tabs)
                    if (tab.Content is ScanWorkspace scan) scan.RefreshLocale();
                    else if (tab.Content is HistoryWorkspace history) history.RefreshLocale();
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
