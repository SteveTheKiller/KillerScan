using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerScan.Controls;

namespace KillerScan.Shell
{
    // Keyboard shortcuts and the F1 list and keyboard-map overlay.
    // The shortcut table feeds both views so their gestures and descriptions stay together.
    public partial class MainWindow
    {
        // (gesture, description resource key, category). Order is the list order within a
        // category and the map tooltip order. The category drives both the list heading a row
        // sits under and the color its key takes on the keyboard map.
        private static readonly (string Keys, string Desc, string Cat)[] ShortcutRows =
        [
            ("Ctrl + T",        "Str_View_Scan",            "Views"),
            ("F8",              "Str_Services_Title",       "Views"),
            ("F9",              "Str_TT_Topology",          "Views"),
            ("F2",              "Str_View_KeepAlive",       "Views"),
            ("Ctrl + Shift + T", "Str_Workspace_Terminal",  "Views"),
            ("F6",              "Str_History_Title",        "Views"),
            ("F10",             "Str_Profiles_Title",       "Views"),

            ("F5",              "Str_Sc_Scan",              "Scan"),
            ("Esc",             "Str_Sc_Cancel",            "Scan"),
            ("Ctrl + R",        "Str_Sc_Rescan",            "Scan"),
            ("Ctrl + F",        "Str_Sc_Subnet",            "Scan"),
            ("Ctrl + Shift + F", "Str_FilterPlaceholder",   "Scan"),
            ("Ctrl + A",        "Str_Sc_SelectAll",         "Scan"),
            ("Ctrl + E",        "Str_Sc_Export",            "Scan"),

            ("Enter",           "Str_Sc_Browser",           "Device"),
            ("F3",              "Str_Diag_Title",           "Device"),
            ("Ctrl + P",        "Str_Sc_Ping",              "Device"),
            ("Ctrl + D",        "Str_Sc_Rdp",               "Device"),
            ("Ctrl + S",        "Str_Sc_Ssh",               "Device"),
            ("Ctrl + Shift + S", "Str_Sc_SshAs",            "Device"),
            ("Ctrl + C",        "Str_Sc_CopyIp",            "Device"),
            ("Ctrl + Shift + C", "Str_Sc_CopyMac",          "Device"),
            ("Ctrl + Alt + C",  "Str_Sc_CopyHost",          "Device"),
            ("Shift + F10",     "Str_Sc_DeviceMenu",        "Device"),

            ("F7",              "Str_Sc_CycleTopologyOrder", "Topology"),
            ("Ctrl + 1",        "Str_Topology_Role",        "Topology"),
            ("Ctrl + 2",        "Str_Col_Type",             "Topology"),
            ("Ctrl + 3",        "Str_Col_Ip",               "Topology"),
            ("Ctrl + 4",        "Str_Col_Vendor",           "Topology"),

            ("Ctrl + Shift + 1", "Str_Toolbar_SmallIcons",  "Toolbar"),
            ("Ctrl + Shift + 2", "Str_Toolbar_LargeIcons",  "Toolbar"),
            ("Ctrl + Shift + 3", "Str_Toolbar_TextNone",    "Toolbar"),
            ("Ctrl + Shift + 4", "Str_Toolbar_TextBeside",  "Toolbar"),
            ("Ctrl + Shift + 5", "Str_Toolbar_TextUnder",   "Toolbar"),
            ("Ctrl + Shift + 6", "Str_Toolbar_TextOnly",    "Toolbar"),

            ("Ctrl + Shift + +", "Str_Sc_AppBigger",        "App"),
            ("Ctrl + Shift + -", "Str_Sc_AppSmaller",       "App"),
            ("Ctrl + Shift + 0", "Str_Sc_AppReset",         "App"),
            ("F1",              "Str_Sc_Help",              "App"),
            ("F12",             "Str_Sc_About",             "App"),
        ];

        /// <summary>
        /// Heading and column for each category, in the order the sections appear. The split is
        /// authored rather than balanced at runtime, so a new shortcut cannot silently move a
        /// whole section across the card.
        /// </summary>
        private static readonly (string Cat, string TitleKey, bool Right)[] ShortcutGroups =
        [
            ("Views",    "Str_KS_Views",    false),
            ("Scan",     "Str_KS_Scan",     false),
            ("Device",   "Str_KS_Device",   false),
            ("Topology", "Str_KS_Topology", true),
            ("Toolbar",  "Str_KS_Toolbar",  true),
            ("App",      "Str_KS_App",      true),
        ];

        private static readonly (string Id, string Cap, double Width)[][] KeyboardRows =
        [
            [("Esc", "Esc", 1), ("", "", .8), ("F1", "F1", 1), ("F2", "F2", 1), ("F3", "F3", 1),
             ("F4", "F4", 1), ("", "", .6), ("F5", "F5", 1), ("F6", "F6", 1), ("F7", "F7", 1),
             ("F8", "F8", 1), ("", "", .6), ("F9", "F9", 1), ("F10", "F10", 1), ("F11", "F11", 1), ("F12", "F12", 1)],
            [("Grave", "`", 1), ("D1", "1", 1), ("D2", "2", 1), ("D3", "3", 1), ("D4", "4", 1),
             ("D5", "5", 1), ("D6", "6", 1), ("D7", "7", 1), ("D8", "8", 1), ("D9", "9", 1),
             ("D0", "0", 1), ("Minus", "-", 1), ("Equals", "=", 1), ("Back", "Back", 2)],
            [("Tab", "Tab", 1.5), ("Q", "Q", 1), ("W", "W", 1), ("E", "E", 1), ("R", "R", 1),
             ("T", "T", 1), ("Y", "Y", 1), ("U", "U", 1), ("I", "I", 1), ("O", "O", 1),
             ("P", "P", 1), ("LBr", "[", 1), ("RBr", "]", 1), ("BSl", "\\", 1.5)],
            [("Caps", "Caps", 1.8), ("A", "A", 1), ("S", "S", 1), ("D", "D", 1), ("F", "F", 1),
             ("G", "G", 1), ("H", "H", 1), ("J", "J", 1), ("K", "K", 1), ("L", "L", 1),
             ("Semi", ";", 1), ("Quote", "'", 1), ("Enter", "Enter", 2.2)],
            [("Shift", "Shift", 2.3), ("Z", "Z", 1), ("X", "X", 1), ("C", "C", 1), ("V", "V", 1),
             ("B", "B", 1), ("N", "N", 1), ("M", "M", 1), ("Comma", ",", 1), ("Period", ".", 1),
             ("Slash", "/", 1), ("RShift", "Shift", 2.7)],
            [("Ctrl", "Ctrl", 1.5), ("Win", "Win", 1.2), ("Alt", "Alt", 1.5), ("Space", "", 6.8),
             ("RAlt", "Alt", 1.5), ("Menu", "Menu", 1), ("RCtrl", "Ctrl", 1.5)]
        ];

        private const double KeyboardUnit = 42;
        private bool _shortcutMapView;

        // Wired from MainWindow.xaml (PreviewKeyDown on the window) so the keys work wherever
        // focus is, including inside the results grid.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            var modifiers = Keyboard.Modifiers;
            bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
            bool shift = modifiers.HasFlag(ModifierKeys.Shift);
            bool alt = modifiers.HasFlag(ModifierKeys.Alt);
            if (ctrl && !alt)
            {
                if (shift && e.Key >= Key.D1 && e.Key <= Key.D6)
                {
                    SelectToolbarAppearance((int)e.Key - (int)Key.D1 + 1);
                    e.Handled = true; return;
                }
                if (e.Key == Key.T)
                {
                    if (shift) ToggleTerminalPanel(); else NewScan();
                    e.Handled = true; return;
                }
            }
            if (e.Key == Key.Escape)
            {
                if (ShortcutsOverlay.Visibility == Visibility.Visible) { HideShortcuts(); e.Handled = true; return; }
                if (AboutOverlay.Visibility == Visibility.Visible) { AboutClose_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
                if (InterruptTerminalPing()) { e.Handled = true; return; }
            }
            if (Keyboard.FocusedElement is KillerScan.Terminal.TerminalControl) return;
            if (modifiers == ModifierKeys.None)
            {
                switch (e.Key)
                {
                    case Key.F1: ToggleShortcuts(); e.Handled = true; return;
                    case Key.F2: Watch_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.F3: Diagnose_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.F6: HistoryButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.F8: ServicesButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.F9: TopologyButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.F10: ProfilesButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.F12:
                        if (AboutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(AboutOverlay);
                        else ShowAboutOverlay();
                        e.Handled = true; return;
                }
            }
            if (ctrl && shift && !alt)
            {
                switch (e.Key)
                {
                    case Key.OemPlus: case Key.Add:
                        ApplyAppScale(_appScale + 0.05, persist: true); e.Handled = true; return;
                    case Key.OemMinus: case Key.Subtract:
                        ApplyAppScale(_appScale - 0.05, persist: true); e.Handled = true; return;
                    case Key.D0: case Key.NumPad0:
                        ApplyAppScale(1.0, persist: true); e.Handled = true; return;
                }
            }
            ActiveScan?.HandleKey(e);
        }

        // Title-bar "?" button (MainWindow.xaml), matching KillerNotes and KillerPDF.
        private void ShortcutHelp_Click(object sender, RoutedEventArgs e) => ToggleShortcuts();

        private void ToggleShortcuts()
        {
            if (ShortcutsOverlay.Visibility == Visibility.Visible) HideShortcuts();
            else ShowShortcuts();
        }

        private void ShowShortcuts()
        {
            ApplyShortcutView(read: true);
            FadeOverlayIn(ShortcutsOverlay);
        }

        // Same fade-out the About overlay uses (About.cs), so both dismiss identically.
        private void HideShortcuts() => FadeOverlayOut(ShortcutsOverlay);

        // Click-away and the close X.
        private void ShortcutsOverlay_Click(object sender, MouseButtonEventArgs e) => HideShortcuts();
        private void ShortcutsCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void ShortcutsClose_Click(object sender, RoutedEventArgs e) => HideShortcuts();

        private void ShortcutListView_Click(object sender, RoutedEventArgs e) => ApplyShortcutView(false, persist: true);
        private void ShortcutMapView_Click(object sender, RoutedEventArgs e) => ApplyShortcutView(true, persist: true);

        private void ApplyShortcutView(bool map = false, bool persist = false, bool read = false)
        {
            if (read) map = App.GetSetting("ShortcutsMapView") == "1";
            _shortcutMapView = map;
            if (persist) App.SetSetting("ShortcutsMapView", map ? "1" : "0");

            ShortcutListHost.Visibility = map ? Visibility.Collapsed : Visibility.Visible;
            ShortcutMapHost.Visibility = map ? Visibility.Visible : Visibility.Collapsed;
            // Active tab is foreground only, matching KillerPDF.
            ShortcutListViewButton.SetResourceReference(ForegroundProperty,
                map ? "MutedTextBrush" : "PrimaryBrush");
            ShortcutMapViewButton.SetResourceReference(ForegroundProperty,
                map ? "PrimaryBrush" : "MutedTextBrush");
            // Two columns need room; the map needs more still.
            ShortcutCardGrid.MaxWidth = map ? 900 : 760;
            if (map) BuildKeyboardMap(); else BuildShortcutRows();
        }

        /// <summary>Fill the list once. Rebuilt on every show so a language switch is picked up.</summary>
        private void BuildShortcutRows()
        {
            BuildShortcutColumn(ShortcutLeftColumn, right: false);
            BuildShortcutColumn(ShortcutRightColumn, right: true);
            ShortcutsTitle.Text = Loc("Str_Shortcuts_Title");
            ShortcutsHint.Text = Loc("Str_Sc_Hint");
        }

        /// <summary>
        /// One column of category sections. Each column is its own shared-size scope, so the
        /// gesture column sizes to the widest gesture in that column rather than being dragged
        /// wide by a long one on the other side.
        /// </summary>
        private void BuildShortcutColumn(StackPanel host, bool right)
        {
            host.Children.Clear();
            Grid.SetIsSharedSizeScope(host, true);

            var groups = ShortcutGroups.Where(g => g.Right == right).ToArray();
            for (int s = 0; s < groups.Length; s++)
            {
                var group = groups[s];
                var header = new TextBlock
                {
                    Text = Loc(group.TitleKey),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, s == 0 ? 0 : 14, 0, 5),
                };
                // The category's own color, the same key the keyboard map lights its keys with,
                // so a section reads as the same color in both views.
                header.SetResourceReference(TextBlock.ForegroundProperty, "KsCat" + group.Cat);
                host.Children.Add(header);

                var rows = ShortcutRows.Where(r => r.Cat == group.Cat).ToArray();
                for (int i = 0; i < rows.Length; i++)
                {
                    var (keys, descKey, _) = rows[i];
                    var row = new Grid { Margin = new Thickness(0, 0, 0, i == rows.Length - 1 ? 0 : 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition
                    { Width = GridLength.Auto, SharedSizeGroup = "KsKeys", MinWidth = 112 });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var k = new TextBlock
                    {
                        Text = keys,
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 12, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Right,
                    };
                    k.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                    Grid.SetColumn(k, 0);
                    row.Children.Add(k);

                    var d = new TextBlock
                    {
                        Text = ShortcutDescription(keys, descKey),
                        FontSize = 11,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    d.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                    Grid.SetColumn(d, 1);
                    row.Children.Add(d);

                    host.Children.Add(row);
                }
            }
        }

        private void BuildKeyboardMap()
        {
            ShortcutMapRows.Children.Clear();
            var bindings = ShortcutRows
                .Select(row => (Id: ShortcutKeyId(row.Keys), row.Keys, row.Desc))
                .Where(row => row.Id.Length > 0)
                .GroupBy(row => row.Id)
                .ToDictionary(group => group.Key, group => group.ToList());

            foreach (var row in KeyboardRows)
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
                foreach (var (id, cap, width) in row)
                {
                    if (id.Length == 0)
                    {
                        panel.Children.Add(new Border { Width = KeyboardUnit * width });
                        continue;
                    }
                    bindings.TryGetValue(id, out var actions);
                    panel.Children.Add(BuildKeyboardKey(cap, width, actions));
                }
                ShortcutMapRows.Children.Add(panel);
            }
            ShortcutsTitle.Text = Loc("Str_Shortcuts_Title");
        }

        private static string ShortcutKeyId(string keys)
        {
            if (keys.EndsWith(" +", StringComparison.Ordinal)) return "Equals";
            if (keys.EndsWith(" -", StringComparison.Ordinal)) return "Minus";
            string key = keys[(keys.LastIndexOf('+') + 1)..].Trim();
            return key switch
            {
                "Esc" => "Esc",
                "Enter" => "Enter",
                "Tab" => "Tab",
                "\\" => "BSl",
                _ when key.Length == 1 && char.IsDigit(key[0]) => "D" + key,
                _ when key.Length == 1 => key.ToUpperInvariant(),
                _ when key.StartsWith("F", StringComparison.Ordinal) => key,
                _ => ""
            };
        }

        private Border BuildKeyboardKey(string cap, double width, List<(string Id, string Keys, string Desc)>? actions)
        {
            var capText = new TextBlock
            {
                Text = cap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0)
            };
            capText.SetResourceReference(TextBlock.ForegroundProperty, actions == null ? "DimTextBrush" : "TextBrush");

            var grid = new Grid();
            grid.Children.Add(capText);
            var key = new Border
            {
                Width = KeyboardUnit * width - 4,
                Height = 40,
                Margin = new Thickness(0, 0, 4, 0),
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Child = grid
            };
            key.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            key.SetResourceReference(Border.BorderBrushProperty, actions == null ? "CardBorderBrush" : "PrimaryBrush");

            if (actions != null)
            {
                var actionText = new TextBlock
                {
                    Text = ShortcutDescription(actions[0].Keys, actions[0].Desc),
                    FontSize = 7.5,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(2, 0, 2, 5)
                };
                actionText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                var bar = new Rectangle
                {
                    Height = 3,
                    Margin = new Thickness(3, 0, 3, 0),
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                bar.SetResourceReference(Shape.FillProperty, "PrimaryBrush");
                grid.Children.Add(actionText);
                grid.Children.Add(bar);
                key.ToolTip = string.Join(Environment.NewLine,
                    actions.Select(action => $"{action.Keys}  {ShortcutDescription(action.Keys, action.Desc)}"));
            }
            return key;
        }

        private string ShortcutDescription(string keys, string descriptionKey)
        {
            if (keys.StartsWith("Ctrl + ", StringComparison.Ordinal) &&
                keys.Length == 8 && keys[^1] is >= '1' and <= '4')
                return $"{Loc("Str_TT_TopologyOrder")} {Loc(descriptionKey)}";
            return Loc(descriptionKey);
        }
    }
}
