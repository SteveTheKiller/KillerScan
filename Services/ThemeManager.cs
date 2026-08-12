using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace KillerScan.Services
{
    // Ported from KillerShell 2026-08-11: the six flat grunge palettes join the original six.
    // Order is cosmetic - the saved setting round-trips by NAME, not by ordinal - but it follows
    // KillerShell's so the two apps list their themes the same way.
    // SE98, not 98SE: a C# enum member cannot start with a digit, and LoadDict derives the
    // palette's filename from the member, so Themes/SE98.xaml has to match. The user never sees
    // it - the picker shows Str_Theme_SE98, which is "98SE" in every locale.
    internal enum Theme
    {
        Dark, Light, Black, SE98, Blood, Greed, Cyanotic,
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
        private static Accent _darkAccent  = Accent.Orange;
        private static Accent _lightAccent = Accent.Orange;
        private static Accent _blackAccent = Accent.Orange;
        // Blue is the Windows 98 default and the one this theme is built around, so it is 98SE's
        // starting accent rather than the Green base every other family starts on.
        private static Accent _se98Accent  = Accent.Blue;

        public static Theme Current => _current;
        public static Accent AccentChoiceFor(Theme t) => AccentFor(t);

        private static Accent AccentFor(Theme t) =>
            t == Theme.Light ? _lightAccent
            : t == Theme.Black ? _blackAccent
            : t == Theme.SE98 ? _se98Accent
            : _darkAccent;

        // Only these families carry accent-hue overlays. The grunge palettes deliberately do not:
        // each one is built around one signature accent, and letting the user swap that out is
        // what would make them all look like the same theme in six colors. Matches KillerShell,
        // which ships Accents/ folders for Dark, Light and Black only.
        private static bool HasAccents(Theme t) =>
            t == Theme.Dark || t == Theme.Light || t == Theme.Black || t == Theme.SE98;

        /// <summary>True for the themes that carry an Accents/ folder. Public so the window can
        /// show or hide the accent dots without repeating the list and letting the two drift.</summary>
        public static bool SupportsAccents(Theme t) => HasAccents(t);

        /// <summary>
        /// File stem for a theme, and the name of its accent folder. Only SE98 differs: the enum
        /// member cannot start with a digit, but the palette, its KillerUI half and its accent
        /// folder are all named "98SE" - the KillerUI half especially, because that file is
        /// SHARED with every other app in the family and is not KillerScan's to rename.
        ///
        /// Use this and never theme.ToString() for anything that touches a path or the saved
        /// setting. Getting that wrong is what made picking 98SE throw
        /// "Cannot locate resource themes/killerui/se98.xaml".
        /// </summary>
        private static string ThemeFileName(Theme theme) =>
            theme == Theme.SE98 ? "98SE" : theme.ToString();

        /// <summary>Fired after the theme dictionary has been updated.</summary>
        public static event Action? ThemeChanged;

        /// <summary>
        /// Call once at startup, before MainWindow is created, to restore the saved theme.
        /// </summary>
        public static void Initialize()
        {
            // "98SE" is the file name and what the rest of the family writes, so accept it here
            // as well as the enum spelling.
            string? saved = App.GetSetting("Theme");
            _current     = saved == "98SE" ? Theme.SE98
                         : Enum.TryParse<Theme>(saved, out var t) ? t : Theme.Black;
            _darkAccent  = Enum.TryParse<Accent>(App.GetSetting("DarkAccent"),  out var da) ? da : Accent.Orange;
            _lightAccent = Enum.TryParse<Accent>(App.GetSetting("LightAccent"), out var la) ? la : Accent.Orange;
            _blackAccent = Enum.TryParse<Accent>(App.GetSetting("BlackAccent"), out var ba) ? ba : Accent.Orange;
            _se98Accent  = Enum.TryParse<Accent>(App.GetSetting("SE98Accent"),  out var sa) ? sa : Accent.Blue;

            // The pre-release accent picker wrote Green as Dark and Light's default, and local
            // test installs may also have persisted Green for Black. Move those installations to
            // KillerScan's orange brand default once. The marker makes this a migration rather
            // than a permanent override: choosing Green afterward still survives every restart.
            if (App.GetSetting("NeutralAccentDefaultsV2") != "1")
            {
                _darkAccent = _lightAccent = _blackAccent = Accent.Orange;
                App.SetSetting("DarkAccent", Accent.Orange.ToString());
                App.SetSetting("LightAccent", Accent.Orange.ToString());
                App.SetSetting("BlackAccent", Accent.Orange.ToString());
                App.SetSetting("NeutralAccentDefaultsV2", "1");
            }
            LoadDict(_current);
        }

        /// <summary>Change theme, persist the choice, and repaint.</summary>
        public static void Apply(Theme theme)
        {
            _current = theme;
            // The FILE name, not the enum name, so the stored value reads "98SE" like the rest
            // of the family's settings do.
            App.SetSetting("Theme", ThemeFileName(theme));
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
            else if (family == Theme.SE98)  { _se98Accent  = accent; App.SetSetting("SE98Accent",  accent.ToString()); }
            else                            { _darkAccent  = accent; App.SetSetting("DarkAccent",  accent.ToString()); }

            if (_current == family)
            {
                LoadDict(_current);
                ThemeChanged?.Invoke();
            }
        }

        private static void LoadDict(Theme theme)
        {
            string name = ThemeFileName(theme);

            // Layer order, and it matters: Defaults, then the app palette, then the shared KillerUI
            // layer, then the accent overlay. Each wins over the one before it.
            //
            // Defaults carries the flat-theme value of every token that only the 98SE palette
            // defines - caption metrics, footer cells, bevels, the window frame. Without it, a
            // control template that references one of those resolves to nothing on the other twelve
            // themes and the control collapses to its CLR default. See Themes/Defaults.xaml.
            var newDict = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/Defaults.xaml")
            };
            var palette = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/Themes/{name}.xaml")
            };
            foreach (object key in palette.Keys) newDict[key] = palette[key];

            KillerThemeContract.Apply(newDict, name);

            // The outer window frame now uses KillerNotes' exact shared five-pixel geometry.
            // Only the recessed client wells remain app-specific continuous ramps.
            if (theme == Theme.SE98)
            {
                newDict["PaneBevelLightThickness"] = new Thickness(0);
                newDict["PaneBevelDarkThickness"] = new Thickness(0);
                newDict["PaneBevel2LightThickness"] = new Thickness(0);
                newDict["PaneBevel2DarkThickness"] = new Thickness(0);
            }

            // These two family surface roles predate most palettes. Keep ordinary themes on
            // their existing pane color, while allowing 98SE's shared contract to provide the
            // white list/about wells used by KillerShell's Event Viewer and dialogs.
            if (!newDict.Contains("ListPaneBrush"))
                newDict["ListPaneBrush"] = newDict["PaneBrush"];
            if (!newDict.Contains("AboutPanelBrush"))
                newDict["AboutPanelBrush"] = newDict["PaneBrush"];
            if (!newDict.Contains("ScanContentPaneBrush"))
                newDict["ScanContentPaneBrush"] = newDict["ListPaneBrush"];
            if (!newDict.Contains("TableRowBrush"))
                newDict["TableRowBrush"] = Brushes.Transparent;

            // Outline buttons use neutral Win98 faces when that palette supplies them. Every
            // ordinary palette falls back to the original hollow-accent behavior.
            if (!newDict.Contains("OutlineFaceBrush"))
                newDict["OutlineFaceBrush"] = Brushes.Transparent;
            if (!newDict.Contains("OutlineTextBrush"))
                newDict["OutlineTextBrush"] = newDict["OutlineBtnBrush"];
            if (!newDict.Contains("OutlineRestBrush"))
                newDict["OutlineRestBrush"] = newDict["OutlineBtnBrush"];
            if (!newDict.Contains("OutlineHoverBrush"))
                newDict["OutlineHoverBrush"] = newDict["OutlineBtnBrush"];
            if (!newDict.Contains("OutlineHoverTextBrush"))
                newDict["OutlineHoverTextBrush"] = newDict["OnPrimaryBrush"];
            if (!newDict.Contains("OutlinePressedBrush"))
                newDict["OutlinePressedBrush"] = Brushes.Transparent;
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
                string family = theme == Theme.Light ? "Light"
                              : theme == Theme.Black ? "Black"
                              : theme == Theme.SE98  ? "98SE"
                              : "Dark";
                try
                {
                    var accentDict = new ResourceDictionary
                    {
                        Source = new Uri($"pack://application:,,,/Themes/Accents/{family}/{accent}.xaml")
                    };
                    var target = merged[0];
                    foreach (object key in accentDict.Keys)
                        target[key] = accentDict[key];

                    // OutlineButton consumes the derived roles below, not OutlineBtnBrush
                    // directly. They were created from the base palette before this overlay was
                    // applied, so without refreshing them the table line and selection changed
                    // hue while the Scan button stayed base green. 98SE supplies purpose-built
                    // outline roles in its overlays and must keep those instead.
                    if (theme != Theme.SE98)
                    {
                        target["OutlineTextBrush"] = target["OutlineBtnBrush"];
                        target["OutlineRestBrush"] = target["OutlineBtnBrush"];
                        target["OutlineHoverBrush"] = target["OutlineBtnBrush"];
                    }
                }
                catch { /* overlay file not present yet - base theme stands */ }
            }
        }
    }
}
