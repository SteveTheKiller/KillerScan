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
            actions.Children.Add(WorkspaceButton("\uE89F", "Str_Workspace_Split", ToggleWorkspaceSplit, true));
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
            pane.Frame.BorderThickness = new Thickness(1, 0, 1, 1);
            Grid.SetRow(pane.Frame, 1);
            pane.Root.Children.Add(pane.Frame);
            Grid.SetColumn(pane.Root, column);
            WorkspaceHost.Children.Add(pane.Root);
        }

        private Button WorkspaceButton(string text, string tooltip, Action click, bool glyph = false)
        {
            var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(2), MinWidth = 26, VerticalAlignment = VerticalAlignment.Center };
            button.SetResourceReference(ForegroundProperty, "TextBrush");
            button.Background = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "<Border x:Name='Face' Background='{TemplateBinding Background}' Padding='{TemplateBinding Padding}' CornerRadius='{DynamicResource ControlCornerRadius}'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Face' Property='Background' Value='{DynamicResource OutlineHoverBrush}'/>" +
                "<Setter Property='Foreground' Value='{DynamicResource OutlineHoverTextBrush}'/></Trigger>" +
                "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Face' Property='Background' Value='{DynamicResource OutlineHoverBrush}'/></Trigger>" +
                "</ControlTemplate.Triggers></ControlTemplate>");
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
            foreach (var workspace in WorkspacePanes()) UpdateWorkspacePaneAppearance(workspace);
            if (pane.Selected?.Content is ScanWorkspace scan) StatusText.Text = scan.Status;
            else StatusText.Text = pane.Selected?.Status ?? string.Empty;
            ScanProgress.Value = ActiveScan?.Progress ?? 0;
            ScanProgress.Visibility = ActiveScan?.IsProgressVisible == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateWorkspaceRail();
        }

        private void RenderWorkspaceTabs(WorkspacePane pane)
        {
            pane.Strip.Children.Clear();
            bool flat = ThemeManager.Current == Theme.SE98;
            foreach (var tab in pane.Tabs)
            {
                bool active = tab == pane.Selected;
                var row = new DockPanel { LastChildFill = true };
                string title = (tab.TitleKey == null ? tab.Title : Loc(tab.TitleKey)) + tab.TitleSuffix;
                var label = new TextBlock
                {
                    Text = title, TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = new FontFamily("Consolas"), FontSize = active ? 11.5 : 11,
                    FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
                    VerticalAlignment = VerticalAlignment.Center
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, active ? "TextBrush" : "MutedTextBrush");
                var close = WorkspaceButton("x", "Str_Workspace_Close", () => CloseWorkspaceTab(pane, tab));
                close.Width = close.Height = 18;
                close.MinWidth = 0;
                close.Padding = new Thickness(0);
                close.Margin = new Thickness(6, 0, 0, 0);
                close.FocusVisualStyle = null;
                close.FontFamily = new FontFamily("Consolas");
                close.FontSize = 11;
                close.SetResourceReference(ForegroundProperty, "MutedTextBrush");
                DockPanel.SetDock(close, Dock.Right);
                row.Children.Add(close);
                row.Children.Add(label);
                var face = new Grid();
                var tabBorder = new Border
                {
                    Child = face, Tag = tab, MinWidth = 60, Cursor = Cursors.Hand, ToolTip = title,
                    CornerRadius = flat ? new CornerRadius(0) : new CornerRadius(6, 6, 0, 0),
                    Margin = active ? new Thickness(0, 3, 0, 0) : flat ? new Thickness(0, 5, 0, 2) : new Thickness(0, 3, 0, 1),
                    Background = Brushes.Transparent
                };
                if (active)
                {
                    tabBorder.SetResourceReference(Border.BackgroundProperty, "PaneBrush");
                    var texture = new Border
                    {
                        IsHitTestVisible = false, CornerRadius = tabBorder.CornerRadius,
                        Margin = new Thickness(-12, -4, -5, -5)
                    };
                    texture.SetResourceReference(Border.BackgroundProperty, "GrainTileBrush");
                    texture.SetResourceReference(OpacityProperty, "GrainOpacity");
                    face.Children.Add(texture);
                }
                face.Children.Add(row);
                if (flat)
                {
                    var light = new Border { IsHitTestVisible = false, Margin = new Thickness(-12, -4, -5, -4), BorderThickness = new Thickness(2, 2, 0, 0) };
                    light.SetResourceReference(Border.BorderBrushProperty, "BevelLightBrush");
                    var dark = new Border
                    {
                        IsHitTestVisible = false,
                        Margin = active ? new Thickness(-12, -4, -5, -2) : new Thickness(-12, -4, -5, -4),
                        BorderThickness = active ? new Thickness(0, 0, 2, 0) : new Thickness(0, 0, 2, 1)
                    };
                    dark.SetResourceReference(Border.BorderBrushProperty, "BevelDarkBrush");
                    face.Children.Add(light);
                    face.Children.Add(dark);
                    if (active)
                    {
                        var seam = new Border { IsHitTestVisible = false, Width = 2, Height = 2, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -5, -4) };
                        seam.SetResourceReference(Border.BackgroundProperty, "BevelLightBrush");
                        face.Children.Add(seam);
                    }
                    tabBorder.SizeChanged += (_, _) => ClipWorkspaceTab(tabBorder);
                }
                tabBorder.MouseLeftButtonDown += (_, e) => { SelectWorkspaceTab(pane, tab); e.Handled = true; };
                var menu = new ContextMenu();
                var move = new MenuItem { Header = Loc("Str_Workspace_Move") };
                move.Click += (_, _) => { SelectWorkspaceTab(pane, tab); MoveWorkspaceTab(); };
                menu.Items.Add(move); tabBorder.ContextMenu = menu;
                pane.Strip.Children.Add(tabBorder);
            }
            UpdateWorkspacePaneAppearance(pane);
        }

        private void UpdateWorkspacePaneAppearance(WorkspacePane pane)
        {
            bool flat = ThemeManager.Current == Theme.SE98;
            bool focused = pane == CurrentPane;
            string edge = focused && !flat ? "PrimaryBrush" : "PaneBorderBrush";
            pane.Frame.SetResourceReference(Border.BorderBrushProperty, edge);
            pane.HeaderLine.SetResourceReference(Border.BackgroundProperty, flat ? "BevelLightBrush" : edge);
            pane.EmptyBackdrop.Visibility = pane.Selected == null ? Visibility.Visible : Visibility.Collapsed;
            foreach (var border in pane.Strip.Children.OfType<Border>())
            {
                bool active = border.Tag == pane.Selected;
                border.BorderThickness = flat ? new Thickness(0) : active
                    ? focused ? new Thickness(1, 3, 1, 0) : new Thickness(0, 3, 0, 0)
                    : new Thickness(0, 0, 1, 0);
                border.Padding = flat ? new Thickness(12, 4, 5, 4) : active
                    ? focused ? new Thickness(11, 1, 4, 5) : new Thickness(12, 1, 5, 5)
                    : new Thickness(12, 4, 5, 5);
                border.SetResourceReference(Border.BorderBrushProperty, active ? edge : "PaneBorderBrush");
            }
        }

        private static void ClipWorkspaceTab(Border tab)
        {
            double w = tab.ActualWidth, h = tab.ActualHeight;
            if (w <= 0 || h <= 0) return;
            double cut = Math.Min(3, Math.Min(w / 2, h / 2));
            var shape = new StreamGeometry();
            using (var path = shape.Open())
            {
                path.BeginFigure(new Point(0, h + 16), true, true);
                path.LineTo(new Point(0, cut), true, false);
                path.LineTo(new Point(cut, 0), true, false);
                path.LineTo(new Point(w - cut, 0), true, false);
                path.LineTo(new Point(w, cut), true, false);
                path.LineTo(new Point(w, h + 16), true, false);
            }
            shape.Freeze();
            tab.Clip = shape;
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
                if (ActiveScan == scan) ActivatePane(CurrentPane);
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
                Process.Start(new ProcessStartInfo(command[..separator], command[(separator + 1)..]) { UseShellExecute = true });
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
