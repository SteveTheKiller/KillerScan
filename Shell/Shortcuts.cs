using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerScan.Controls;

namespace KillerScan.Shell
{
    // Keyboard shortcuts and the F1 list overlay.
    //
    // Deliberately lighter than the KillerNotes / KillerPDF treatment: those apps have 40-80
    // shortcuts and earn a drawn keyboard with Ctrl and Shift layers. KillerScan has ten, so
    // this is a plain two-column list on the same card chrome the About overlay uses - no new
    // theme brushes, no category colors, one entry point.
    //
    // Single source of truth: the Rows table below feeds both the overlay and nothing else, so
    // adding a shortcut means adding its handler in Window_PreviewKeyDown and a row here.
    public partial class MainWindow
    {
        // (gesture, description resource key). Order is the order shown.
        private static readonly (string Keys, string Desc)[] ShortcutRows =
        [
            ("F5",              "Str_Sc_Scan"),
            ("F6",              "Str_TT_Topology"),
            ("F7",              "Str_Topology_Role"),
            ("F8",              "Str_Col_Type"),
            ("F9",              "Str_Col_Ip"),
            ("F10",             "Str_Col_Vendor"),
            ("Esc",             "Str_Sc_Cancel"),
            ("Ctrl + R",        "Str_Sc_Rescan"),
            ("Ctrl + F",        "Str_Sc_Subnet"),
            ("Ctrl + A",        "Str_Sc_SelectAll"),
            ("Ctrl + E",        "Str_Sc_Export"),
            ("Enter",           "Str_Sc_Browser"),
            ("Ctrl + P",        "Str_Sc_Ping"),
            ("Ctrl + D",        "Str_Sc_Rdp"),
            ("Ctrl + S",        "Str_Sc_Ssh"),
            ("Ctrl + Shift + S", "Str_Sc_SshAs"),
            ("Ctrl + C",        "Str_Sc_CopyIp"),
            ("Ctrl + Shift + C", "Str_Sc_CopyMac"),
            ("Ctrl + Alt + C",  "Str_Sc_CopyHost"),
            ("Shift + F10",     "Str_Sc_DeviceMenu"),
            ("Ctrl + Shift + +", "Str_Sc_AppBigger"),
            ("Ctrl + Shift + -", "Str_Sc_AppSmaller"),
            ("Ctrl + Shift + 0", "Str_Sc_AppReset"),
            ("F1",              "Str_Sc_Help"),
            ("F12",             "Str_Sc_About"),
        ];

        // Wired from MainWindow.xaml (PreviewKeyDown on the window) so the keys work wherever
        // focus is, including inside the results grid.
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl  = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            bool alt   = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

            // Esc closes the overlays first, then falls through to canceling a running scan.
            // (F12 below toggles About; Esc remains the close for both overlays.)
            if (e.Key == Key.Escape)
            {
                if (ShortcutsOverlay.Visibility == Visibility.Visible) { HideShortcuts(); e.Handled = true; return; }
                if (AboutOverlay.Visibility == Visibility.Visible) { AboutClose_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
                if (_active?.Cts != null) { ScanBtn_Click(this, new RoutedEventArgs()); e.Handled = true; }
                return;
            }

            if (e.Key == Key.F1) { ToggleShortcuts(); e.Handled = true; return; }

            // Family standard: F12 is always the About card.
            if (e.Key == Key.F12)
            {
                if (AboutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(AboutOverlay);
                else ShowAboutOverlay();
                e.Handled = true; return;
            }

            // Typing in the subnet or filter box must keep its own Ctrl+A / Ctrl+F behavior.
            bool inTextBox = Keyboard.FocusedElement is TextBox;

            if (ctrl && shift)
            {
                switch (e.Key)
                {
                    case Key.S:
                        if (!inTextBox && GetSelectedDevice() != null)
                        { SshAsDevice_Click(this, new RoutedEventArgs()); e.Handled = true; }
                        return;
                    case Key.C:
                        if (!inTextBox && GetSelectedDevice() != null)
                        { CopyMac_Click(this, new RoutedEventArgs()); e.Handled = true; }
                        return;
                    case Key.OemPlus: case Key.Add:
                        ApplyAppScale(_appScale + 0.05, persist: true); e.Handled = true; return;
                    case Key.OemMinus: case Key.Subtract:
                        ApplyAppScale(_appScale - 0.05, persist: true); e.Handled = true; return;
                    case Key.D0: case Key.NumPad0:
                        ApplyAppScale(1.0, persist: true); e.Handled = true; return;
                }
                return;
            }

            if (ctrl && alt)
            {
                if (e.Key == Key.C && !inTextBox && GetSelectedDevice() != null)
                { CopyHostname_Click(this, new RoutedEventArgs()); e.Handled = true; }
                return;
            }

            if (ctrl)
            {
                switch (e.Key)
                {
                    case Key.F:
                        SubnetInput.Focus(); SubnetInput.SelectAll(); e.Handled = true; return;
                    case Key.E:
                        if (ExportButton.IsEnabled) { ExportCsv_Click(this, new RoutedEventArgs()); e.Handled = true; }
                        return;
                    case Key.R:
                        if (RescanMenuItem.IsEnabled && ResultsGrid.SelectedItems.Count > 0)
                        { RescanSelected_Click(this, new RoutedEventArgs()); e.Handled = true; }
                        return;
                    case Key.A:
                        if (!inTextBox) { ResultsGrid.SelectAll(); e.Handled = true; }
                        return;
                    case Key.C:
                        if (!inTextBox && GetSelectedDevice() != null)
                        { CopyIp_Click(this, new RoutedEventArgs()); e.Handled = true; }
                        return;
                    case Key.P:
                        if (!inTextBox && GetSelectedDevice() != null)
                        { PingDevice_Click(this, new RoutedEventArgs()); e.Handled = true; }
                        return;
                    case Key.D:
                        if (!inTextBox && GetSelectedDevice() != null)
                        { RdpDevice_Click(this, new RoutedEventArgs()); e.Handled = true; }
                        return;
                    case Key.S:
                        if (!inTextBox && GetSelectedDevice() != null)
                        { SshDevice_Click(this, new RoutedEventArgs()); e.Handled = true; }
                        return;
                }
                return;
            }

            if (e.Key == Key.F5) { ScanBtn_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key == Key.F6) { TopologyButton_Click(this, new RoutedEventArgs()); e.Handled = true; }
            else if (e.Key is Key.F7 or Key.F8 or Key.F9 or Key.F10)
            {
                if (!_showTopology) TopologyButton_Click(this, new RoutedEventArgs());
                SetTopologyOrder(e.Key switch
                {
                    Key.F8 => TopologyOrder.Type,
                    Key.F9 => TopologyOrder.Ip,
                    Key.F10 => TopologyOrder.Vendor,
                    _ => TopologyOrder.Role
                });
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && ResultsGrid.IsKeyboardFocusWithin && GetSelectedDevice() != null)
            { OpenBrowser_Click(this, new RoutedEventArgs()); e.Handled = true; }
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
            BuildShortcutRows();
            FadeOverlayIn(ShortcutsOverlay);
        }

        // Same fade-out the About overlay uses (About.cs), so both dismiss identically.
        private void HideShortcuts() => FadeOverlayOut(ShortcutsOverlay);

        // Click-away and the close X.
        private void ShortcutsOverlay_Click(object sender, MouseButtonEventArgs e) => HideShortcuts();
        private void ShortcutsCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void ShortcutsClose_Click(object sender, RoutedEventArgs e) => HideShortcuts();

        /// <summary>Fill the list once. Rebuilt on every show so a language switch is picked up.</summary>
        private void BuildShortcutRows()
        {
            ShortcutList.RowDefinitions.Clear();
            ShortcutList.Children.Clear();

            for (int i = 0; i < ShortcutRows.Length; i++)
            {
                var (keys, descKey) = ShortcutRows[i];
                ShortcutList.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var k = new TextBlock
                {
                    Text = keys,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    FontSize = 12,
                    Margin = new Thickness(0, 3, 16, 3),
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                k.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                Grid.SetRow(k, i); Grid.SetColumn(k, 0);
                ShortcutList.Children.Add(k);

                var d = new TextBlock
                {
                    Text = Loc(descKey),
                    FontSize = 12,
                    Margin = new Thickness(0, 3, 0, 3),
                    TextWrapping = TextWrapping.Wrap,
                };
                d.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                Grid.SetRow(d, i); Grid.SetColumn(d, 1);
                ShortcutList.Children.Add(d);
            }

            ShortcutsTitle.Text = Loc("Str_Shortcuts_Title");
            ShortcutsHint.Text = Loc("Str_Sc_Hint");
        }
    }
}
