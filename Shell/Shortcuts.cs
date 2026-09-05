using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
            ("F6",              "Str_View_Devices",         "Views"),
            ("F7",              "Str_Services_Title",       "Views"),
            ("F8",              "Str_TT_Topology",          "Views"),
            ("F9",              "Str_View_KeepAlive",       "Views"),
            ("F10",             "Str_Workspace_Terminal",   "Views"),
            ("Ctrl + H",        "Str_History_Title",        "Views"),
            ("Ctrl + Shift + P", "Str_Profiles_Title",      "Views"),
            ("F4",              "Str_TT_SpeedTest",         "Views"),

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

            ("Ctrl + G",              "Str_Sc_CycleTopologyOrder", "Topology"),
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
            ("Topology", "Str_KS_Topology", false),
            ("Device",   "Str_KS_Device",   true),
            ("Toolbar",  "Str_KS_Toolbar",  true),
            ("App",      "Str_KS_App",      true),
        ];

        // The physical keyboard, layer bindings and board-building code now live in
        // KeyboardMap.cs alongside the rest of the layered map.

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
                    case Key.F3: Diagnose_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.F4: SpeedTestButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    // F6 to F10 in the order the view buttons sit on the toolbar.
                    case Key.F6: ShowScanView("devices"); e.Handled = true; return;
                    case Key.F7: ServicesButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.F8: TopologyButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.F9: Watch_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                    case Key.F10: NewTerminal(); e.Handled = true; return;
                    case Key.F12:
                        if (AboutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(AboutOverlay);
                        else ShowAboutOverlay();
                        e.Handled = true; return;
                }
            }
            if (ctrl && !shift && !alt)
            {
                switch (e.Key)
                {
                    // Ctrl+H rather than an F key: history is a panel you open beside your work,
                    // like the profiles list, not a view you switch to. Terminals never see this,
                    // because a focused terminal returns above and keeps its own ^H.
                    case Key.H: HistoryButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
                }
            }
            if (ctrl && shift && !alt)
            {
                switch (e.Key)
                {
                    // Profiles gave up F10 to the Terminal view. It sits with history
                    // instead: both are panels beside your work rather than views.
                    case Key.P: ProfilesButton_Click(this, new RoutedEventArgs()); e.Handled = true; return;
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
                var (cat, titleKey, _) = groups[s];
                var header = new TextBlock
                {
                    Text = Loc(titleKey),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, s == 0 ? 0 : 14, 0, 5),
                };
                // The category's own color, the same key the keyboard map lights its keys with,
                // so a section reads as the same color in both views.
                header.SetResourceReference(TextBlock.ForegroundProperty, "KsCat" + cat);
                host.Children.Add(header);

                var rows = ShortcutRows.Where(r => r.Cat == cat).ToArray();
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

        // BuildKeyboardMap and the keycap builder now live in KeyboardMap.cs.

        private string ShortcutDescription(string keys, string descriptionKey)
        {
            if (keys.StartsWith("Ctrl + ", StringComparison.Ordinal) &&
                keys.Length == 8 && keys[^1] is >= '1' and <= '4')
                return $"{Loc("Str_TT_TopologyOrder")} {Loc(descriptionKey)}";
            return Loc(descriptionKey);
        }

        // Opens the online help / how-to page in the user's default browser.
        private void OnlineHelp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("https://killerscan.net/help.html") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
