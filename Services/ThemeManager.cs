using System;
using System.Windows;
using System.Windows.Threading;

namespace KillerScan.Services
{
    // Ported from KillerShell 2026-08-11: the six flat grunge palettes join the original six.
    // Order is cosmetic - the saved setting round-trips by NAME, not by ordinal - but it follows
    // KillerShell's so the two apps list their themes the same way.
    internal enum Theme
    {
        Dark, Light, Black, Blood, Greed, Cyanotic,
        Ectoplasm, Decay, Mourning, Sepulchre, Delirium, Malaise
    }

    // Accent-hue variants for the accent-capable families (Dark, Light, Black).
    // Green is the base theme (no overlay); the others apply a small overlay
    // dictionary that recolors only the accent-family keys. Shared skin tokens
    // come from the linked KillerUI contract.
    internal enum Accent { Green, Red, Blue, Purple, Orange, Teal }

    /// <summary>
    /// Swaps the theme color dictionary (MergedDictionaries[0]) in place at runtime.
    /// Control styles live in App.xaml and bind brushes via DynamicResource, so an
    /// in-place per-key update repaints everything without structural churn.
    /// KillerScan has a custom (WindowStyle=None) title bar, so there is no native
    /// DWM title bar to recolor.
    /// </summary>
    internal static class ThemeManager
    {
        private static Theme _current = Theme.Black;
        // Dark, Light, and Black each remember their own accent independently.
        private static Accent _darkAccent  = Accent.Green;
        private static Accent _lightAccent = Accent.Green;
        private static Accent _blackAccent = Accent.Orange;

        public static Theme Current => _current;
        public static Accent AccentChoiceFor(Theme t) => AccentFor(t);

        private static Accent AccentFor(Theme t) =>
            t == Theme.Light ? _lightAccent : t == Theme.Black ? _blackAccent : _darkAccent;

        // Only these families carry accent-hue overlays. The grunge palettes deliberately do not:
        // each one is built around one signature accent, and letting the user swap that out is
        // what would make them all look like the same theme in six colors. Matches KillerShell,
        // which ships Accents/ folders for Dark, Light and Black only.
        private static bool HasAccents(Theme t) =>
            t == Theme.Dark || t == Theme.Light || t == Theme.Black;

        /// <summary>Fired after the theme dictionary has been updated.</summary>
        public static event Action? ThemeChanged;

        /// <summary>
        /// Call once at startup, before MainWindow is created, to restore the saved theme.
        /// </summary>
        public static void Initialize()
        {
            _current     = Enum.TryParse<Theme>(App.GetSetting("Theme"),        out var t)  ? t  : Theme.Black;
            _darkAccent  = Enum.TryParse<Accent>(App.GetSetting("DarkAccent"),  out var da) ? da : Accent.Green;
            _lightAccent = Enum.TryParse<Accent>(App.GetSetting("LightAccent"), out var la) ? la : Accent.Green;
            _blackAccent = Enum.TryParse<Accent>(App.GetSetting("BlackAccent"), out var ba) ? ba : Accent.Orange;
            LoadDict(_current);
        }

        /// <summary>Change theme, persist the choice, and repaint.</summary>
        public static void Apply(Theme theme)
        {
            _current = theme;
            App.SetSetting("Theme", theme.ToString());
            LoadDict(theme);
            ThemeChanged?.Invoke();
        }

        /// <summary>
        /// Change a family's accent hue, persist it, and reapply if that family is active.
        /// Dark/Light/Black keep independent accents, so changing one never disturbs another.
        /// </summary>
        public static void ApplyAccent(Theme family, Accent accent)
        {
            if      (family == Theme.Light) { _lightAccent = accent; App.SetSetting("LightAccent", accent.ToString()); }
            else if (family == Theme.Black) { _blackAccent = accent; App.SetSetting("BlackAccent", accent.ToString()); }
            else                            { _darkAccent  = accent; App.SetSetting("DarkAccent",  accent.ToString()); }

            if (_current == family)
            {
                LoadDict(_current);
                ThemeChanged?.Invoke();
            }
        }

        private static void LoadDict(Theme theme)
        {
            var uri = new Uri($"pack://application:,,,/Themes/{theme}.xaml");
            var newDict = new ResourceDictionary { Source = uri };
            KillerThemeContract.Apply(newDict, theme.ToString());
            var merged  = Application.Current.Resources.MergedDictionaries;

            // In-place per-key update: fires a targeted change notification for each key
            // without structurally modifying MergedDictionaries (a structural swap fires a
            // synchronous ResourcesChanged that can re-enter lookups before the new dict
            // is fully in place). Theme dictionaries hold colors/brushes only.
            if (merged.Count > 0)
            {
                var existing = merged[0];
                foreach (object key in newDict.Keys)
                    existing[key] = newDict[key];
            }
            else
            {
                merged.Add(newDict);
            }

            // Accent overlay: Dark/Light/Black recolor their accent-family keys on top of
            // the base green. Green is the base itself, so it needs no overlay (re-applying
            // the base above already restored green). Overlays live in Accents/<Family>/.
            var accent = AccentFor(theme);
            if (HasAccents(theme) && accent != Accent.Green)
            {
                string family = theme == Theme.Light ? "Light" : theme == Theme.Black ? "Black" : "Dark";
                try
                {
                    var accentDict = new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/Themes/Accents/{family}/{accent}.xaml")
                    };
                    var target = merged[0];
                    foreach (object key in accentDict.Keys)
                        target[key] = accentDict[key];
                }
                catch { /* overlay file not present yet - base theme stands */ }
            }
        }
    }
}
