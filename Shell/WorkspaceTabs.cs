using System.Windows;
using System.Windows.Controls;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private enum ToolbarIconSize { Small, Large }
        private enum ToolbarLabelMode { None, Beside, Under, Only }
        // Small icons with the caption beside them: the bar also carries the target box, the
        // scan buttons, filter and arrange, and large icons crowd them out at ordinary widths.
        private ToolbarIconSize _toolbarIconSize = ToolbarIconSize.Small;
        private ToolbarLabelMode _toolbarLabelMode = ToolbarLabelMode.Beside;
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
            AddViewButton("scan", "Str_View_Devices", "F6", () => ShowScanView("devices"));
            AddViewButton("services", "Str_Services_Title", "F7", () => ServicesButton_Click(this, new RoutedEventArgs()));
            AddViewButton("topology", "Str_View_Topology", "F8", () => ShowScanView("topology"));
            AddViewButton("watch", "Str_View_KeepAlive", "F9", () => Watch_Click(this, new RoutedEventArgs()));
            AddViewButton("terminal", "Str_Workspace_Terminal", "F10", NewTerminalView);
            _workspaceToolbar.ContextMenu = _toolbarMenu;
            _workspaceToolbar.Background = System.Windows.Media.Brushes.Transparent;
            BuildToolbarMenu();
            ApplyToolbarAppearance();
            _toolbarOverflow.SetResourceReference(StyleProperty, "ViewToolbarButton");
            _toolbarOverflow.SetResourceReference(ToolTipProperty, "Str_Toolbar_Header");
            _toolbarOverflow.Click += (_, _) => OpenToolbarOverflow();
            // Last in the strip rather than a sibling layered over it: as its own child of the
            // toolbar grid it shared a column with these buttons and simply painted on top of them.
            _workspaceNavigation.Children.Add(_toolbarOverflow);
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
                "scan" => "\uE772",
                "services" => "\uE8FD",
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

        /// <summary>
        /// Marks the current view the way KillerPDF marks the selected tool: the caller sets
        /// the button's Background and Foreground by resource reference, so the pair tracks a
        /// theme swap, and clears them again for the rest. Clearing rather than assigning a
        /// transparent value matters, because a local Background would outrank the style's
        /// hover trigger and the inactive buttons would stop lighting up under the pointer.
        /// </summary>
        private void UpdateWorkspaceNavigation()
        {
            foreach (var pair in _viewButtons)
            {
                if (pair.Key == _workspaceView)
                {
                    pair.Value.SetResourceReference(Control.BackgroundProperty, "SelectionBg");
                    pair.Value.SetResourceReference(Control.ForegroundProperty, "SelectionFg");
                    // Read by the template, which gives a selected view its own shadow strength.
                    pair.Value.Tag = "selected";
                }
                else
                {
                    pair.Value.ClearValue(Control.BackgroundProperty);
                    pair.Value.ClearValue(Control.ForegroundProperty);
                    pair.Value.ClearValue(FrameworkElement.TagProperty);
                }
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

        /// <summary>
        /// Buttons that did not fit on the bar. The overflow menu lists these and only these, so
        /// a button is in exactly one of the two places.
        /// </summary>
        private readonly List<Button> _overflowedViews = [];

        /// <summary>
        /// Which button gives way first as the bar tightens, least wanted at the front. This is
        /// a judgement about the work, not about the layout, so it is written down rather than
        /// derived from the order the buttons happen to sit in: Services is the narrowest reading
        /// of a scan you already have, while Devices is the reason the window is open.
        /// </summary>
        private static readonly string[] ToolbarDropOrder =
        [
            "Str_Services_Title", "Str_View_Topology",
            "Str_Workspace_Terminal", "Str_View_KeepAlive", "Str_View_Devices"
        ];

        /// <summary>
        /// Keeps as many view buttons on the bar as the space beside the active view's own
        /// controls allows, and moves the rest into the overflow menu.
        /// </summary>
        /// <remarks>
        /// The view buttons are what gives way, one at a time in ToolbarDropOrder. The input box
        /// and its buttons are what the window is FOR in a view, so they keep their measured
        /// width rather than being squeezed until they wrap. Scan's bar and Keep Alive's are
        /// different widths, so the budget is measured from whichever is showing.
        /// </remarks>
        private void FitToolbarViews()
        {
            if (_workspaceToolbar.ActualWidth <= 0) return;

            // The overflow button lives in this strip too, so it has to be kept out of the list of
            // things that can be pushed into the overflow.
            var buttons = _workspaceNavigation.Children.OfType<Button>()
                .Where(b => b != _toolbarOverflow).ToList();
            foreach (var button in buttons)
            {
                button.Visibility = Visibility.Visible;
                button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }

            double reserved = _workspaceNavigation.Margin.Left + _workspaceNavigation.Margin.Right;
            string key = _viewToolbars.ContainsKey(_workspaceView) ? _workspaceView : "scan";
            if (_viewToolbars.TryGetValue(key, out var toolbar))
            {
                toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                reserved += toolbar.DesiredSize.Width + toolbar.Margin.Left + toolbar.Margin.Right;
            }

            _toolbarOverflow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double available = _workspaceToolbar.ActualWidth - reserved;
            double total = buttons.Sum(b => b.DesiredSize.Width);

            _overflowedViews.Clear();
            if (total > available)
            {
                // The overflow button is about to appear, so it has to come out of the same
                // budget. Drop buttons from the left until what is left fits beside it.
                available -= _toolbarOverflow.DesiredSize.Width;
                _viewButtons.TryGetValue(_workspaceView, out var active);
                var order = buttons
                    .OrderBy(b => Array.IndexOf(ToolbarDropOrder,
                        _viewAppearance.TryGetValue(b, out var look) ? look.Key : string.Empty) is var i && i < 0
                        ? int.MaxValue : i);
                foreach (var button in order)
                {
                    if (total <= available) break;
                    // The view you are in keeps its place on the bar however tight it gets:
                    // it is the one carrying the selected state, and hiding it would leave
                    // nothing on screen saying where you are.
                    if (button == active) continue;
                    total -= button.DesiredSize.Width;
                    button.Visibility = Visibility.Collapsed;
                    _overflowedViews.Add(button);
                }
            }

            _toolbarOverflow.Visibility = _overflowedViews.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenToolbarOverflow()
        {
            var menu = new ContextMenu { PlacementTarget = _toolbarOverflow,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            // Bar order, not the order they were dropped: the menu reads as the rest of the same
            // toolbar rather than as a list of casualties.
            foreach (var button in _workspaceNavigation.Children.OfType<Button>().Where(_overflowedViews.Contains))
            {
                var item = new MenuItem { IsEnabled = button.IsEnabled };
                item.SetResourceReference(HeaderedItemsControl.HeaderProperty, _viewAppearance[button].Key);
                item.InputGestureText = _viewAppearance[button].Key switch
                {
                    "Str_View_Devices" => "F6",
                    "Str_Services_Title" => "F7",
                    "Str_View_Topology" => "F8",
                    "Str_View_KeepAlive" => "F9",
                    "Str_Workspace_Terminal" => "F10",
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
