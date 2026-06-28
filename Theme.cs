using System;
using System.Windows;
using System.Windows.Controls;
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
    }
}
