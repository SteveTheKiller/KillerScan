using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerScan.Services;

namespace KillerScan
{
    // UI shell: title-bar theme + accent pickers and the language menu.
    public partial class MainWindow
    {
        private void ThemeSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string name && Enum.TryParse<Theme>(name, out var theme))
            {
                ThemeManager.Apply(theme);
                UpdateThemeSwatchSelection();
                UpdateAccentSwatches();
            }
        }

        private void AccentSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string name && Enum.TryParse<Accent>(name, out var accent))
            {
                ThemeManager.ApplyAccent(ThemeManager.Current, accent);
                UpdateThemeSwatchSelection();   // ring colours follow the accent
                UpdateAccentSwatches();
            }
        }

        /// <summary>Highlights the active theme's swatch.</summary>
        private void UpdateThemeSwatchSelection()
            => HighlightSwatches(FindName("ThemeSwatches") as Panel, ThemeManager.Current.ToString());

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            if (FindName("ThemePopup") is System.Windows.Controls.Primitives.Popup p)
                p.IsOpen = !p.IsOpen;
        }

        /// <summary>Shows the accent dots for accent-capable themes and highlights the active accent.</summary>
        private void UpdateAccentSwatches()
        {
            if (FindName("AccentSwatches") is not Panel panel) return;
            var t = ThemeManager.Current;
            bool hasAccents = t == Theme.Dark || t == Theme.Light || t == Theme.Black;
            var vis = hasAccents ? Visibility.Visible : Visibility.Collapsed;
            panel.Visibility = vis;
            if (FindName("AccentLabel") is UIElement lbl) lbl.Visibility = vis;
            if (hasAccents)
                HighlightSwatches(panel, ThemeManager.AccentChoiceFor(t).ToString());
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
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.IsOpen = true;
            }
        }

        private void Lang_Click(object sender, RoutedEventArgs e)
        {
            // Locale switching lands with the i18n pass; English is the only option for now.
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
