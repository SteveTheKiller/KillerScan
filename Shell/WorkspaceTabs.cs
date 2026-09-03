using System.Windows;
using System.Windows.Controls;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private enum ToolbarIconSize { Small, Large }
        private enum ToolbarLabelMode { None, Beside, Under, Only }
        private ToolbarIconSize _toolbarIconSize = ToolbarIconSize.Small;
        private ToolbarLabelMode _toolbarLabelMode = ToolbarLabelMode.Under;
        private readonly Dictionary<Button, (string Glyph, string Key)> _viewAppearance = [];
        private readonly ContextMenu _toolbarMenu = new();
        private readonly Button _toolbarOverflow = new()
        {
            Content = "\uE712", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
            FontSize = 14, Width = 36, Height = 34, Margin = new Thickness(8, 2, 8, 2),
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed
        };

        private void BuildWorkspaceNavigation()
        {
            if (Enum.TryParse(App.GetSetting("ToolbarIconSize"), out ToolbarIconSize size) && Enum.IsDefined(typeof(ToolbarIconSize), size))
                _toolbarIconSize = size;
            if (Enum.TryParse(App.GetSetting("ToolbarLabels"), out ToolbarLabelMode labels) && Enum.IsDefined(typeof(ToolbarLabelMode), labels))
                _toolbarLabelMode = labels;
            _workspaceNavigation.Margin = new Thickness(8, 2, 8, 2);
            AddViewButton("scan", "Str_View_Scan", "Ctrl+T", () => ShowScanView("devices"));
            AddViewButton("services", "Str_Services_Title", "F8", () => ServicesButton_Click(this, new RoutedEventArgs()));
            AddViewButton("topology", "Str_View_Topology", "F9", () => ShowScanView("topology"));
            AddViewButton("watch", "Str_View_KeepAlive", "F2", () => Watch_Click(this, new RoutedEventArgs()));
            AddViewButton("terminal", "Str_Workspace_Terminal", "Ctrl+Shift+T", NewTerminalView);
            _workspaceToolbar.ContextMenu = _toolbarMenu;
            _workspaceToolbar.Background = System.Windows.Media.Brushes.Transparent;
            BuildToolbarMenu();
            ApplyToolbarAppearance();
            _toolbarOverflow.SetResourceReference(StyleProperty, "ViewToolbarButton");
            _toolbarOverflow.SetResourceReference(ToolTipProperty, "Str_Toolbar_Header");
            _toolbarOverflow.Click += (_, _) => OpenToolbarOverflow();
            _workspaceToolbar.SizeChanged += (_, _) => FitToolbarViews();
        }

        private void AddViewButton(string view, string key, string shortcut, Action action)
        {
            var button = new Button
            {
                Margin = new Thickness(0, 0, 2, 0), Padding = new Thickness(6, 4, 6, 4),
                Height = 52, VerticalAlignment = VerticalAlignment.Center
            };
            button.SetResourceReference(StyleProperty, "ViewToolbarButton");
            string glyph = view switch
            {
                "scan" => "\uE8FD",
                "services" => "\uE950",
                "topology" => "\uE968",
                "watch" => "\uE9D9",
                _ => "\uE756"
            };
            var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            content.Children.Add(new TextBlock
            {
                Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"),
                FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center
            });
            var label = new TextBlock
            {
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 10,
                Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            label.SetResourceReference(TextBlock.TextProperty, key);
            content.Children.Add(label);
            button.Content = content;
            _viewAppearance.Add(button, (glyph, key));
            button.SetResourceReference(System.Windows.Automation.AutomationProperties.NameProperty, key);
            var tip = new TextBlock();
            var caption = new System.Windows.Documents.Run();
            caption.SetResourceReference(System.Windows.Documents.Run.TextProperty, key);
            tip.Inlines.Add(caption);
            tip.Inlines.Add(" (" + shortcut + ")");
            button.ToolTip = tip;
            button.Click += (_, _) => action();
            _viewButtons.Add(view, button);
            _workspaceNavigation.Children.Add(button);
        }

        private void UpdateWorkspaceNavigation()
        {
            foreach (var pair in _viewButtons)
            {
                bool selected = pair.Key == _workspaceView;
                pair.Value.Tag = selected ? "on" : null;
            }
        }

        private void BuildToolbarMenu()
        {
            var heading = new MenuItem { IsEnabled = false };
            heading.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_Toolbar_Header");
            _toolbarMenu.Items.Add(heading);
            _toolbarMenu.Items.Add(new Separator());
            AddToolbarChoice(ToolbarIconSize.Small, "Str_Toolbar_SmallIcons", 1);
            AddToolbarChoice(ToolbarIconSize.Large, "Str_Toolbar_LargeIcons", 2);
            _toolbarMenu.Items.Add(new Separator());
            AddToolbarChoice(ToolbarLabelMode.None, "Str_Toolbar_TextNone", 3);
            AddToolbarChoice(ToolbarLabelMode.Beside, "Str_Toolbar_TextBeside", 4);
            AddToolbarChoice(ToolbarLabelMode.Under, "Str_Toolbar_TextUnder", 5);
            AddToolbarChoice(ToolbarLabelMode.Only, "Str_Toolbar_TextOnly", 6);
        }

        private void AddToolbarChoice(object value, string key, int shortcut)
        {
            var item = new MenuItem { Tag = value, IsCheckable = true, StaysOpenOnClick = true,
                InputGestureText = "Ctrl+Shift+" + shortcut };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
            item.Click += (_, _) => SelectToolbarAppearance(shortcut);
            _toolbarMenu.Items.Add(item);
        }

        private void SelectToolbarAppearance(int choice)
        {
            if (choice <= 2)
            {
                if (_toolbarLabelMode == ToolbarLabelMode.Only) return;
                _toolbarIconSize = choice == 1 ? ToolbarIconSize.Small : ToolbarIconSize.Large;
                App.SetSetting("ToolbarIconSize", _toolbarIconSize.ToString());
            }
            else
            {
                _toolbarLabelMode = (ToolbarLabelMode)(choice - 3);
                App.SetSetting("ToolbarLabels", _toolbarLabelMode.ToString());
            }
            ApplyToolbarAppearance();
        }

        private void ApplyToolbarAppearance()
        {
            bool large = _toolbarIconSize == ToolbarIconSize.Large;
            bool under = _toolbarLabelMode == ToolbarLabelMode.Under;
            bool only = _toolbarLabelMode == ToolbarLabelMode.Only;
            bool none = _toolbarLabelMode == ToolbarLabelMode.None;
            foreach (var entry in _viewAppearance)
            {
                var button = entry.Key;
                button.Width = none ? (large ? 46 : 36) : double.NaN;
                button.Height = under ? (large ? 56 : 52) : none ? (large ? 42 : 32) : 34;
                button.Padding = under ? new Thickness(6, 4, 6, 4) : new Thickness(8, 5, 8, 5);
                var panel = new StackPanel { Orientation = under ? Orientation.Vertical : Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (!only) panel.Children.Add(new TextBlock { Text = entry.Value.Glyph,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = large ? 20 : 14,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
                if (!none)
                {
                    var caption = new TextBlock { FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                        FontSize = under ? 10 : 12, HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = under ? new Thickness(0, 2, 0, 0) : only ? new Thickness(0) : new Thickness(7, 0, 0, 0) };
                    caption.SetResourceReference(TextBlock.TextProperty, entry.Value.Key);
                    panel.Children.Add(caption);
                }
                button.Content = panel;
            }
            foreach (var item in _toolbarMenu.Items.OfType<MenuItem>())
            {
                if (item.Tag is ToolbarIconSize size)
                {
                    item.IsChecked = size == _toolbarIconSize;
                    item.IsEnabled = !only;
                }
                else if (item.Tag is ToolbarLabelMode labels) item.IsChecked = labels == _toolbarLabelMode;
            }
            FitToolbarViews();
        }

        private void FitToolbarViews()
        {
            if (_workspaceToolbar.ActualWidth <= 0) return;
            double width = _workspaceNavigation.Margin.Left + _workspaceNavigation.Margin.Right;
            foreach (Button button in _workspaceNavigation.Children)
            {
                button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                width += button.DesiredSize.Width;
            }
            bool overflow = width > _workspaceToolbar.ActualWidth - 240;
            _workspaceNavigation.Visibility = overflow ? Visibility.Collapsed : Visibility.Visible;
            _toolbarOverflow.Visibility = overflow ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenToolbarOverflow()
        {
            var menu = new ContextMenu { PlacementTarget = _toolbarOverflow,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (Button button in _workspaceNavigation.Children)
            {
                var item = new MenuItem { IsEnabled = button.IsEnabled };
                item.SetResourceReference(HeaderedItemsControl.HeaderProperty, _viewAppearance[button].Key);
                item.InputGestureText = _viewAppearance[button].Key switch
                {
                    "Str_View_Scan" => "Ctrl+T",
                    "Str_Services_Title" => "F8",
                    "Str_View_Topology" => "F9",
                    "Str_View_KeepAlive" => "F2",
                    "Str_Workspace_Terminal" => "Ctrl+Shift+T",
                    _ => "Ctrl+E"
                };
                item.ToolTip = button.ToolTip;
                item.Click += (_, _) =>
                {
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (button.ContextMenu?.IsOpen == true) button.ContextMenu.PlacementTarget = _toolbarOverflow;
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private void NewTerminalView() => NewTerminal();
    }
}
