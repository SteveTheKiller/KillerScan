using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
                UpdateAccentStrip(animate: true);
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
            chrome?.CaptionHeight = h;
        }

        private void AccentSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string name && Enum.TryParse<Accent>(name, out var accent))
            {
                ThemeManager.ApplyAccent(ThemeManager.Current, accent);
                UpdateThemeSwatchSelection();   // ring colors follow the accent
                RingAccentStrip();
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
                FindName("ThemeMenuContent") is Grid content)
            {
                if (content.Parent is Panel parent) parent.Children.Remove(content);
                oldPopup.IsOpen = false;
                content.Margin = new Thickness(12, 10, 3, 10);

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
            UpdateAccentStrip(animate: false);
            Anim.FadeIn(_themeContextMenu);
        }

        private static readonly (Accent Accent, string Hex)[] DarkStripColors =
            [(Accent.Red, "#DD504B"), (Accent.Orange, "#E8962C"), (Accent.Green, "#1EA54C"),
             (Accent.Teal, "#1FB8A8"), (Accent.Blue, "#4580D9"), (Accent.Purple, "#B982E3")];
        private static readonly (Accent Accent, string Hex)[] LightStripColors =
            [(Accent.Red, "#931A1A"), (Accent.Orange, "#C7710F"), (Accent.Green, "#1B5E20"),
             (Accent.Teal, "#0D827E"), (Accent.Blue, "#18608E"), (Accent.Purple, "#5A1690")];
        private static readonly (Accent Accent, string Hex)[] BlackStripColors =
            [(Accent.Red, "#FF2929"), (Accent.Orange, "#FF910A"), (Accent.Green, "#00FF66"),
             (Accent.Teal, "#0AFFE7"), (Accent.Blue, "#298DFF"), (Accent.Purple, "#B829FF")];
        private static readonly (Accent Accent, string Hex)[] SE98StripColors =
            [(Accent.Red, "#800040"), (Accent.Orange, "#A05000"), (Accent.Green, "#006000"),
             (Accent.Teal, "#008080"), (Accent.Blue, "#000080"), (Accent.Purple, "#5A376E")];

        private static (Accent Accent, string Hex)[] StripColorsFor(Theme family) => family switch
        {
            Theme.Light => LightStripColors,
            Theme.Black => BlackStripColors,
            Theme.SE98 => SE98StripColors,
            _ => DarkStripColors,
        };

        private Theme _stripFamily = Theme.Dark;
        private bool _stripOpen;
        private const double AccentStripWidth = 39;
        private const double AccentStripSlideMs = 180;
        private Button[] StripDots =>
            [AccentStripDot0, AccentStripDot1, AccentStripDot2, AccentStripDot3, AccentStripDot4, AccentStripDot5];

        private void PopulateAccentStrip(Theme family)
        {
            var colors = StripColorsFor(family);
            var dots = StripDots;
            for (int i = 0; i < dots.Length; i++)
            {
                dots[i].Background = (Brush)new BrushConverter().ConvertFromString(colors[i].Hex)!;
                dots[i].Tag = colors[i].Accent.ToString();
                dots[i].ToolTip = colors[i].Accent.ToString();
            }
            _stripFamily = family;
            RingAccentStrip();
        }

        private void RingAccentStrip()
        {
            var activeRing = TryFindResource("TextBrush") as Brush ?? Brushes.White;
            var chosen = ThemeManager.AccentChoiceFor(_stripFamily).ToString();
            foreach (var dot in StripDots)
            {
                bool selected = dot.Tag as string == chosen;
                dot.BorderBrush = selected ? activeRing : Brushes.Transparent;
                dot.BorderThickness = new Thickness(selected ? 2 : 1);
            }
        }

        private void UpdateAccentStrip(bool animate)
        {
            var current = ThemeManager.Current;
            bool show = ThemeManager.SupportsAccents(current);
            if (show)
            {
                if (animate && _stripOpen && _stripFamily != current)
                {
                    var target = current;
                    var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(90));
                    fadeOut.Completed += (_, _) =>
                    {
                        PopulateAccentStrip(target);
                        AccentStrip.BeginAnimation(OpacityProperty,
                            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(90)));
                    };
                    AccentStrip.BeginAnimation(OpacityProperty, fadeOut);
                }
                else PopulateAccentStrip(current);
            }
            SlideAccentStrip(show, animate);
        }

        private void SlideAccentStrip(bool show, bool animate)
        {
            if (show == _stripOpen && animate) return;
            _stripOpen = show;
            AccentStripHost.BeginAnimation(WidthProperty, null);
            if (!animate)
            {
                AccentStripHost.Width = show ? AccentStripWidth : 0;
                return;
            }
            double from = double.IsNaN(AccentStripHost.Width) ? AccentStripHost.ActualWidth : AccentStripHost.Width;
            var animation = new DoubleAnimation(from, show ? AccentStripWidth : 0,
                TimeSpan.FromMilliseconds(AccentStripSlideMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            animation.Completed += (_, _) =>
            {
                AccentStripHost.BeginAnimation(WidthProperty, null);
                AccentStripHost.Width = _stripOpen ? AccentStripWidth : 0;
            };
            AccentStripHost.BeginAnimation(WidthProperty, animation);
        }

        // ---- Language menu ----

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
            (Services.Locale.HuHU, "Magyar",      "hu-HU"),
            (Services.Locale.ItIT, "Italiano",    "it-IT"),
            (Services.Locale.Ja,   "日本語",      "ja-JP"),
            (Services.Locale.KkKZ, "Қазақша",    "kk-KZ"),
            (Services.Locale.PlPL, "Polski",      "pl-PL"),
            (Services.Locale.RuRU, "Русский",    "ru-RU"),
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
            ColIp?.Header     = Loc("Str_Col_Ip");
            ColHost?.Header   = Loc("Str_Col_Host");
            ColMac?.Header    = Loc("Str_Col_Mac");
            ColVendor?.Header = Loc("Str_Col_Vendor");
            ColType?.Header   = Loc("Str_Col_Type");
            ColPorts?.Header  = Loc("Str_Col_Ports");

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
