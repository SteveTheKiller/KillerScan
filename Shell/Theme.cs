using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Shell
{
    // UI shell: the rail's theme flyout (named rows + accent dots) and its language menu.
    // Both moved off the title bar onto the icon rail on 2026-08-11.
    public partial class MainWindow
    {
        /// <summary>
        /// A theme row in the rail's flyout was picked. One handler serves all twelve rows: the
        /// Tag carries the Theme enum name, so adding a theme is one line of XAML.
        ///
        /// Guarded, because <see cref="UpdateThemeSwatchSelection"/> ticks the current row itself
        /// and RadioButton.Checked fires on a programmatic set exactly as it does on a click.
        /// Without the guard, syncing the flyout would re-apply the theme and re-enter this.
        /// </summary>
        private bool _syncingThemeRadios;
        private ContextMenu? _themeContextMenu;

        private void ThemeRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_syncingThemeRadios) return;
            if (sender is RadioButton rb && rb.Tag is string name && Enum.TryParse<Theme>(name, out var theme))
            {
                ThemeManager.Apply(theme);
                ApplyThemeBorder(this);   // retint the DWM frame border to the new palette
                ApplyFlatChrome();
                UpdateAccentSwatches();
            }
        }

        /// <summary>
        /// Everything about 98SE that a palette cannot express. Called on every theme change and
        /// once at startup.
        ///
        /// Keyed on the theme rather than on a "is it flat" token because KillerScan has exactly
        /// one flat theme; if a second ever lands, this is the one place that has to learn about it.
        ///
        /// Three things, none of which a ResourceDictionary can do:
        ///   - The pane's drop shadow is a StaticResource Effect, not a DynamicResource-bound
        ///     property, so no theme file can switch it off. Windows 98 had no drop shadows.
        ///   - A Win98 caption carries the window's NAME in plain bold, never a logotype, so the
        ///     wordmark and subtitle collapse and PlainTitle takes over. This is not cosmetic
        ///     preference: the wordmark's two runs are TextBrush and PrimaryBrush, which on 98SE
        ///     are black and navy - both invisible on the navy caption.
        ///   - A Win98 caption is 22px, not 36. The row height and WindowChrome.CaptionHeight have
        ///     to move together or the draggable strip stops matching the bar you can see.
        /// </summary>
        private void ApplyFlatChrome()
        {
            bool flat = ThemeManager.Current == Theme.SE98;

            if (FindName("DevicesPane") is System.Windows.Controls.Border pane)
                pane.Effect = flat ? null : TryFindResource("PaneShadow") as System.Windows.Media.Effects.Effect;

            // The About card's shadow layer. Collapsed rather than Effect-nulled: its inline
            // DropShadowEffect could not be restored after a null, and collapsing the whole layer
            // also removes the second background it paints.
            if (FindName("AboutShadow") is UIElement sh)
                sh.Visibility = flat ? Visibility.Collapsed : Visibility.Visible;
            if (FindName("AboutInfoShadow") is UIElement infoShadow)
                infoShadow.Visibility = flat ? Visibility.Collapsed : Visibility.Visible;

            var wordmark = flat ? Visibility.Collapsed : Visibility.Visible;
            var plain    = flat ? Visibility.Visible   : Visibility.Collapsed;
            if (FindName("LogoBar")      is UIElement lb) lb.Visibility = wordmark;
            if (FindName("SubtitleText") is UIElement st) st.Visibility = wordmark;
            if (FindName("PlainTitle")   is UIElement pt) pt.Visibility = plain;

            // The icon shrinks with the bar; 27px does not fit a 22px caption.
            if (FindName("TitleIcon") is FrameworkElement icon)
            {
                icon.Width = icon.Height = flat ? 16 : 27;
                icon.Margin = new Thickness(0, 0, flat ? 5 : 7, 0);
            }

            double h = flat ? 22 : 36;
            if (FindName("RootGrid") is System.Windows.Controls.Grid root && root.RowDefinitions.Count > 0)
                root.RowDefinitions[0].Height = new GridLength(h);
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (chrome != null) chrome.CaptionHeight = h;
        }

        private void AccentSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string name && Enum.TryParse<Accent>(name, out var accent))
            {
                ThemeManager.ApplyAccent(ThemeManager.Current, accent);
                UpdateThemeSwatchSelection();   // ring colors follow the accent
                UpdateAccentSwatches();
            }
        }

        /// <summary>Ticks the row for the theme that is actually active. Called at startup and
        /// whenever the flyout opens, so a theme restored from settings shows as selected.</summary>
        private void UpdateThemeSwatchSelection()
        {
            if (FindName("ThemeRadios") is not Panel panel) return;
            string current = ThemeManager.Current.ToString();
            _syncingThemeRadios = true;
            try
            {
                foreach (var child in panel.Children)
                    if (child is RadioButton rb && rb.Tag is string name)
                        rb.IsChecked = name == current;
            }
            finally { _syncingThemeRadios = false; }
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            // Family standard: theme and locale both use ContextMenu. The old transparent Popup
            // followed different DPI placement math and rasterized its text without ClearType.
            // Move the existing named XAML content once so all theme handlers remain intact.
            if (_themeContextMenu == null &&
                FindName("ThemePopup") is System.Windows.Controls.Primitives.Popup oldPopup &&
                FindName("ThemeMenuContent") is StackPanel content)
            {
                if (content.Parent is Panel parent) parent.Children.Remove(content);
                oldPopup.IsOpen = false;
                content.Margin = new Thickness(12, 10, 14, 10);

                var itemStyle = new Style(typeof(MenuItem));
                var itemTemplate = new ControlTemplate(typeof(MenuItem));
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
                presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
                presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
                itemTemplate.VisualTree = presenter;
                itemStyle.Setters.Add(new Setter(Control.TemplateProperty, itemTemplate));

                _themeContextMenu = new ContextMenu { ItemContainerStyle = itemStyle };
                _themeContextMenu.Items.Add(content);
                _themeContextMenu.PreviewMouseWheel += Menu_ForwardWheelToGrid;
            }

            if (_themeContextMenu == null) return;
            FlyoutPlacement.Attach(_themeContextMenu);
            _themeContextMenu.IsOpen = true;

            // Sync on OPEN: the theme can have changed since the menu was last visited.
            UpdateThemeSwatchSelection();
            UpdateAccentSwatches();
            Anim.FadeIn(_themeContextMenu);
        }

        /// <summary>Shows the accent dots for accent-capable themes and highlights the active accent.</summary>
        private void UpdateAccentSwatches()
        {
            if (FindName("AccentSwatches") is not Panel panel) return;
            var t = ThemeManager.Current;
            // Asked of ThemeManager rather than listed again here: the two lists drifting apart
            // would show dots for a theme that has no Accents/ folder, or hide them for one that has.
            bool hasAccents = ThemeManager.SupportsAccents(t);
            panel.Visibility = hasAccents ? Visibility.Visible : Visibility.Collapsed;
            if (hasAccents)
            {
                RecolorAccentDots(panel, t);
                HighlightSwatches(panel, ThemeManager.AccentChoiceFor(t).ToString());
                PositionAccentRow(panel, t.ToString());
            }
        }

        /// <summary>The six Windows 98 accents, keyed by the same Accent names the other families
        /// use. These are each overlay's own PrimaryBrush, so a dot always shows the color it
        /// actually applies - a bright modern dot next to a muted Win98 accent would be a lie
        /// about what you are picking.</summary>
        private static readonly Dictionary<string, string> Se98DotColors = new()
        {
            ["Red"]    = "#700038",   // maroon; nothing in the period palette was a bright red
            ["Orange"] = "#804000",
            ["Green"]  = "#004f00",
            ["Teal"]   = "#006060",
            ["Blue"]   = "#000080",   // the Win98 navy, and 98SE's default
            ["Purple"] = "#4b286d",
        };

        /// <summary>Swaps the accent dots between the modern set painted in the XAML and the Win98
        /// set above. Only 98SE differs, so every other theme restores the markup's own value from
        /// the dot's Tag via the modern table.</summary>
        private static readonly Dictionary<string, string> ModernDotColors = new()
        {
            ["Red"]    = "#DD504B",
            ["Orange"] = "#E8962C",
            ["Green"]  = "#1ea54c",
            ["Teal"]   = "#1FB8A8",
            ["Blue"]   = "#50AEE8",
            ["Purple"] = "#B982E3",
        };

        private static void RecolorAccentDots(Panel panel, Theme t)
        {
            var table = t == Theme.SE98 ? Se98DotColors : ModernDotColors;

            // 98SE's native/default blue is the first choice. The modern families retain the
            // established ROYGBIV presentation order.
            string[] order = t == Theme.SE98
                ? ["Blue", "Red", "Orange", "Green", "Teal", "Purple"]
                : ["Red", "Orange", "Green", "Teal", "Blue", "Purple"];
            var buttons = panel.Children.OfType<Button>()
                .Where(b => b.Tag is string)
                .ToDictionary(b => (string)b.Tag);
            panel.Children.Clear();
            foreach (string name in order)
                if (buttons.TryGetValue(name, out var button)) panel.Children.Add(button);

            foreach (var child in panel.Children)
            {
                if (child is not Button b || b.Tag is not string name) continue;
                if (!table.TryGetValue(name, out var hex)) continue;
                b.Background = (Brush)new BrushConverter().ConvertFromString(hex)!;
            }
        }

        /// <summary>
        /// Moves the accent dots so they sit directly under the theme row they belong to. An accent
        /// is a property of ONE theme - Dark, Light and Black each remember their own - so a single
        /// accent block at the foot of a twelve-row list is next to whichever theme happens to be
        /// last, which is never the one it applies to.
        ///
        /// Reparenting rather than three hardcoded rows (KillerShell's approach): one set of dots
        /// cannot drift out of step with its copies, and adding an accent-capable theme needs no
        /// new markup. The panel keeps its x:Name registration through the move, because the name
        /// scope belongs to the window, not to the panel.
        /// </summary>
        private void PositionAccentRow(Panel dots, string currentTag)
        {
            if (FindName("ThemeRadios") is not Panel rows) return;

            rows.Children.Remove(dots);

            int idx = -1;
            for (int i = 0; i < rows.Children.Count; i++)
                if (rows.Children[i] is RadioButton rb && rb.Tag as string == currentTag) { idx = i; break; }

            // Unknown theme (should not happen): put them back at the end rather than dropping
            // them out of the tree entirely.
            rows.Children.Insert(idx < 0 ? rows.Children.Count : idx + 1, dots);
        }

        private void HighlightSwatches(Panel? panel, string current)
        {
            if (panel == null) return;
            var activeRing = TryFindResource("PrimaryBrush") as Brush ?? Brushes.White;
            var idleRing   = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            foreach (var child in panel.Children)
            {
                if (child is not Button b || b.Tag is not string name) continue;
                bool active = name == current;
                b.BorderBrush     = active ? activeRing : idleRing;
                b.BorderThickness = new Thickness(active ? 2 : 1);
            }
        }

        // ---- Language menu (scaffold; English only until the i18n pass) ----

        private void LangButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.ContextMenu != null)
            {
                BuildLanguageMenu(b.ContextMenu);
                FlyoutPlacement.Attach(b.ContextMenu);
                b.ContextMenu.IsOpen = true;
                Anim.FadeIn(b.ContextMenu);
            }
        }

        // English pinned on top; the rest alphabetical by locale code (the file name). Native name on
        // the left, code right-aligned in the flyout.
        private static readonly (Services.Locale Loc, string Name, string Code)[] Languages =
        [
            (Services.Locale.EnUS, "English",    "en-US"),
            (Services.Locale.Bn,   "বাংলা",       "bn"),
            (Services.Locale.CsCZ, "Čeština",    "cs-CZ"),
            (Services.Locale.De,   "Deutsch",    "de-DE"),
            (Services.Locale.Es,   "Español",    "es"),
            (Services.Locale.Fr,   "Français",   "fr-FR"),
            (Services.Locale.Ja,   "日本語",      "ja-JP"),
            (Services.Locale.TrTR, "Türkçe",     "tr-TR"),
            (Services.Locale.ZhCN, "中文 (简体)", "zh-CN"),
            (Services.Locale.ZhTW, "中文 (繁體)", "zh-TW"),
        ];

        private void BuildLanguageMenu(ContextMenu menu)
        {
            menu.Items.Clear();
            var current = Services.LocaleManager.Current;

            foreach (var (loc, name, code) in Languages)
            {
                var grid = new Grid { MinWidth = 160 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var nameBlock = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
                var codeBlock = new TextBlock
                {
                    Text = "(" + code + ")",
                    Opacity = 0.5,
                    Margin = new Thickness(22, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(codeBlock, 1);
                grid.Children.Add(nameBlock);
                grid.Children.Add(codeBlock);

                var item = new MenuItem
                {
                    Header = grid,
                    Tag = loc.ToString(),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    IsChecked = loc == current,
                };
                if (loc == current && TryFindResource("PrimaryBrush") is Brush accent)
                {
                    nameBlock.Foreground = accent;
                    nameBlock.FontWeight = FontWeights.SemiBold;
                    codeBlock.Foreground = accent;
                    codeBlock.Opacity = 0.85;
                }
                item.Click += Lang_Click;
                menu.Items.Add(item);
            }
        }

        private void Lang_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem mi && mi.Tag is string tag
                && Enum.TryParse<Services.Locale>(tag, out var loc))
            {
                Services.LocaleManager.Apply(loc);
                RelocalizeDynamicUi();
            }
        }

        /// <summary>Look up a localized string; falls back to the key name if missing.</summary>
        private string Loc(string key) => Application.Current.TryFindResource(key) as string ?? key;

        /// <summary>Re-applies strings to UI built in code (column headers, status, count, scan button),
        /// so a live language switch updates them. Static {DynamicResource Str_*} XAML updates itself.</summary>
        private void RelocalizeDynamicUi()
        {
            // DataGridColumn.Header DynamicResource does not refresh on a live dictionary swap - re-set it.
            if (ColIp != null)     ColIp.Header     = Loc("Str_Col_Ip");
            if (ColHost != null)   ColHost.Header   = Loc("Str_Col_Host");
            if (ColMac != null)    ColMac.Header    = Loc("Str_Col_Mac");
            if (ColVendor != null) ColVendor.Header = Loc("Str_Col_Vendor");
            if (ColType != null)   ColType.Header   = Loc("Str_Col_Type");
            if (ColPorts != null)  ColPorts.Header  = Loc("Str_Col_Ports");

            if (_active != null)
            {
                ScanBtn.Content = Loc(_active.IsScanning ? "Str_Btn_Stop" : "Str_Btn_Scan");
                RefreshDeviceCount();
                if (!_active.IsScanning)
                {
                    var ready = string.Format(Loc("Str_Status_Ready"), Services.OuiLookup.Count.ToString("N0"));
                    _active.Status = ready;
                    StatusText.Text = ready;
                }
            }
        }

        // The theme flyout and context menus take mouse capture while open (StaysOpen=False),
        // which otherwise swallows the wheel. If the cursor is over the device table, forward the
        // wheel to its scroll viewer so the list still scrolls without closing the menu first.
        private void Menu_ForwardWheelToGrid(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            if (FindName("ResultsGrid") is not DataGrid grid) return;

            var pos = Mouse.GetPosition(grid);
            if (pos.X < 0 || pos.Y < 0 || pos.X > grid.ActualWidth || pos.Y > grid.ActualHeight) return;

            if (FindDescendant<ScrollViewer>(grid) is not ScrollViewer sv) return;
            if (sv.CanContentScroll)
                sv.ScrollToVerticalOffset(sv.VerticalOffset + (e.Delta > 0 ? -3 : 3));   // row units
            else
                sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);                   // pixel units
            e.Handled = true;
        }

        // Clips the column-header strip to a shape with rounded top corners and a flat bottom,
        // so the header reads as the top of a card while its underline stays straight. Re-run on
        // resize because the strip width tracks the table.
        private void HeaderStrip_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Border b) return;
            double w = b.ActualWidth, h = b.ActualHeight;
            if (w <= 0 || h <= 0) { b.Clip = null; return; }
            double r = Math.Min(6, Math.Min(w / 2, h));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, h), true, true);
                ctx.LineTo(new Point(0, r), true, false);
                ctx.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(w - r, 0), true, false);
                ctx.ArcTo(new Point(w, r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                ctx.LineTo(new Point(w, h), true, false);
            }
            geo.Freeze();
            b.Clip = geo;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T hit) return hit;
                if (FindDescendant<T>(child) is T deeper) return deeper;
            }
            return null;
        }
    }
}
