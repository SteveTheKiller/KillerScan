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
    // dictionary that recolors only the accent-family keys.
    internal enum Accent { Green, Red, Blue, Purple, Orange, Teal, Yellow, Magenta }

    /// <summary>
    /// Swaps the theme color dictionary (MergedDictionaries[0]) in place at runtime.
    /// Control styles live in App.xaml and bind brushes via DynamicResource, so an
    /// in-place per-key update repaints everything without structural churn.
    /// KillerScan has a custom (WindowStyle=None) title bar, so there is no native
    /// DWM title bar to recolor.
    /// </summary>
    internal static class ThemeManager
    {
        /// <summary>Killer Sans UI (Killer Sans scaled to Segoe UI proportions) with the Segoe fallback stack.</summary>
        internal static readonly FontFamily KillerSansUiStack = new(new Uri("pack://application:,,,/KillerScan;component/"), "./Fonts/#Killer Sans UI, Segoe UI, Microsoft JhengHei UI, Nirmala UI");
        internal static readonly FontFamily LacquerHeadingStack = new(new Uri("pack://application:,,,/"), "./Fonts/#Lacquer, ./Fonts/#Killer Sans UI, Segoe UI, Microsoft JhengHei UI, Nirmala UI");
        internal static readonly FontFamily KillerSansFont = new(new Uri("pack://application:,,,/"), "./Fonts/#Killer Sans");
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
        /// member cannot start with a digit, but the palette and accent folder are named "98SE".
        ///
        /// Use this and never theme.ToString() for anything that touches a path or the saved
        /// setting. Getting that wrong is what made picking 98SE throw
        /// "Cannot locate resource themes/se98.xaml".
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

            LoadDict(_current);
        }

        /// <summary>Load the standalone installer palette without changing saved preferences.</summary>
        internal static void InitializeInstaller() => LoadDict(Theme.Black, Accent.Orange);

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

        // Theme brushes are read from many threads' worth of bindings and never mutated, so they
        // are frozen once here rather than left open for WPF to clone on every lookup.
        private static SolidColorBrush Frozen(System.Windows.Media.Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        // Filled buttons and the outline button's hover fill carry the family's soft vertical
        // gradient (the KillerNotes treatment): the outline color itself at the top, 16% darker at the bottom.
        // 98SE, and any value that is already a gradient, stay exactly as they are.
        private static object ButtonFill(object brush, bool flat)
        {
            if (flat || brush is not System.Windows.Media.SolidColorBrush solid) return brush;
            var c = solid.Color;
            var top = c;
            var bottom = System.Windows.Media.Color.FromArgb(c.A, (byte)(c.R * 0.84), (byte)(c.G * 0.84), (byte)(c.B * 0.84));
            var gradient = new System.Windows.Media.LinearGradientBrush(top, bottom, 90);
            gradient.Freeze();
            return gradient;
        }

        private static void LoadDict(Theme theme, Accent? accentOverride = null)
        {
            string name = ThemeFileName(theme);

            // Layer order, and it matters: Defaults, then the complete app palette, then the
            // optional accent overlay. Each wins over the one before it.
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
            bool hasExplicitComboHighlightText = palette.Contains("ComboHighlightTextBrush");


            // The outer window frame now uses KillerNotes' exact shared five-pixel geometry.
            // Only the recessed client wells remain app-specific continuous ramps.
            // The flat themes have no sunken edge, so the four bevel layers resolve to nothing at
            // all rather than relying on an unresolved DynamicResource falling back to the
            // property default, which works but only by accident.
            foreach (var key in new[] { "PaneBevelLightThickness", "PaneBevelDarkThickness",
                                        "PaneBevel2LightThickness", "PaneBevel2DarkThickness" })
                if (!newDict.Contains(key)) newDict[key] = new Thickness(0);
            if (!newDict.Contains("PaneBevelInnerMargin")) newDict["PaneBevelInnerMargin"] = new Thickness(0);
            foreach (var key in new[] { "PaneBevelDarkBrush", "PaneBevelLightBrush",
                                        "PaneBevelDark2Brush", "PaneBevelLight2Brush" })
                if (!newDict.Contains(key)) newDict[key] = Brushes.Transparent;

            // 98SE keeps its pane bevels. They used to be zeroed here, which left the content pane
            // flat: Win32's EDGE_SUNKEN is two tones, #808080 then #000000 down the top and left
            // and #ffffff then the face color up the bottom and right, and the palette carries all
            // four. MainWindow draws them as four layers, the same way KillerNotes does.

            // These two family surface roles predate most palettes. Keep ordinary themes on
            // their existing pane color, while allowing 98SE's shared contract to provide the
            // white list/about wells used by KillerShell's Event Viewer and dialogs.
            if (!newDict.Contains("ListPaneBrush"))
                newDict["ListPaneBrush"] = newDict["PaneBrush"];
            if (!newDict.Contains("ScanContentPaneBrush"))
                newDict["ScanContentPaneBrush"] = newDict["ListPaneBrush"];
            // Topology nodes default to the menu surface, which is what they used before they had
            // keys of their own. A theme that needs the node to separate from its pane overrides
            // these two without dragging every menu along with it.
            if (!newDict.Contains("TopologyNodeBrush"))
                newDict["TopologyNodeBrush"] = newDict["MenuBackgroundBrush"];
            if (!newDict.Contains("TopologyNodeBorderBrush"))
                newDict["TopologyNodeBorderBrush"] = newDict["MenuBorderBrush"];
            if (!newDict.Contains("TopologyNodeHoverBrush"))
                newDict["TopologyNodeHoverBrush"] = newDict["MenuHoverBrush"];
            if (!newDict.Contains("TopologyNodeTextBrush"))
                newDict["TopologyNodeTextBrush"] = newDict["MenuTextBrush"];
            // Hover text defaults to the resting text, so a theme with a subtle hover fill keeps
            // its current look and only the themes that hover with a solid color override it.
            if (!newDict.Contains("TopologyNodeHoverTextBrush"))
                newDict["TopologyNodeHoverTextBrush"] = newDict["TopologyNodeTextBrush"];
            if (!newDict.Contains("SidebarPaneBrush"))
                newDict["SidebarPaneBrush"] = Brushes.Transparent;
            if (!newDict.Contains("TableRowBrush"))
                newDict["TableRowBrush"] = Brushes.Transparent;

            // File picker tokens, completed with KillerPDF's defaults. 98SE states its own
            // values; every other theme gets the modern picker look.
            void Complete(string key, object value) { if (!newDict.Contains(key)) newDict[key] = value; }
            Complete("ComboFieldBrush", newDict["PaneBrush"]);
            Complete("ComboFieldHoverBrush", newDict["RowHoverBrush"]);
            Complete("ComboPopupBrush", newDict["PaneBrush"]);
            Complete("ComboButtonBrush", Brushes.Transparent);
            Complete("ComboButtonHoverBrush", newDict["RowHoverBrush"]);
            Complete("ComboButtonSize", 18.0);
            Complete("ComboButtonHeight", newDict["ComboButtonSize"]);
            Complete("ComboButtonMinWidth", 22.0);
            Complete("ComboChevGlyph", ((char)0xE70D).ToString());
            Complete("ComboChevFont", new FontFamily("Segoe MDL2 Assets"));
            Complete("ComboChevMargin", new Thickness(0));
            Complete("DialogTitleBarBrush", newDict["TitleBarBrush"]);
            Complete("DialogFrameBrush", newDict["AppBorderBrush"]);
            Complete("DialogFrameThickness", new Thickness(1));
            Complete("DialogFramePadding", new Thickness(0));
            Complete("DialogWindowFrameThickness", new Thickness(0));
            Complete("DialogWindowFramePadding", new Thickness(0));
            Complete("ButtonBevelLightThickness", newDict["BevelLightThickness"]);
            Complete("ButtonBevelDarkThickness", newDict["BevelDarkThickness"]);
            Complete("SurfaceButtonEdgeBrush", newDict.Contains("ButtonEdgeBrush") ? newDict["ButtonEdgeBrush"] : newDict["CardBorderBrush"]);
            Complete("FileDialogPaneBrush", newDict["ScanContentPaneBrush"]);
            Complete("MenuFontFamily", KillerSansUiStack);
            Complete("MenuFontSize", 12.0);
            Complete("MenuItemPadding", new Thickness(8, 6, 10, 6));
            bool compactDialogCaption = newDict.Contains("UseDialogCaption") && newDict["UseDialogCaption"] is true;
            // The UI face: Killer Sans UI over the Segoe stack; a classic caption theme keeps its menu font.
            Complete("UiFont", compactDialogCaption ? newDict["MenuFontFamily"] : KillerSansUiStack);
            // The titlebar subtitle sits on the wordmark baseline in Killer Sans, as KillerPDF's filename does.
            Complete("FileNameFont", compactDialogCaption ? newDict["MenuFontFamily"] : KillerSansFont);
            // Primary headings (dialog headings, page and period titles): Lacquer, the app-name face of
            // the wordmark, falling back to Killer Sans UI for scripts Lacquer does not cover. 98SE keeps
            // Killer Sans. Secondary headings, the footer and the interface stay Killer Sans.
            Complete("HeadingFont", compactDialogCaption ? KillerSansFont : LacquerHeadingStack);
            Complete("FileNameFontSize", compactDialogCaption ? 11.0 : 18.0);
            if (!newDict.Contains("FileNameTransform"))
            {
                var fileNameShift = new TranslateTransform(0, compactDialogCaption ? 0 : -1);
                fileNameShift.Freeze();
                newDict["FileNameTransform"] = fileNameShift;
            }
            Complete("DialogCloseWidth", compactDialogCaption ? newDict["CaptionButtonWidth"] : 28.0);
            Complete("DialogCloseHeight", compactDialogCaption ? newDict["CaptionButtonHeight"] : 26.0);
            Complete("DialogCaptionButtonsMargin", new Thickness(0, 0,
                newDict["CaptionButtonsMargin"] is Thickness captionButtonsMargin ? captionButtonsMargin.Right : 0, 0));

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
            // The About info panel. Tracks the pane surface unless a theme names its own; 98SE
            // does, because there the panel is a white client area rather than a card.
            if (!newDict.Contains("AboutPanelBrush"))
                newDict["AboutPanelBrush"] = newDict["PaneBrush"];
            if (!newDict.Contains("AboutPanelEdgeBrush"))
                newDict["AboutPanelEdgeBrush"] = newDict["PaneBorderBrush"];
            // The keyboard map's key caps. Tracks the raised surface unless a theme names its own;
            // 98SE does, because its caps are white rather than button-face gray.
            if (!newDict.Contains("KeyCapBrush"))
                newDict["KeyCapBrush"] = newDict["SurfaceBrush"];
            if (!newDict.Contains("SurfaceHoverBrush"))
                newDict["SurfaceHoverBrush"] = newDict["RowHoverBrush"];
            // The theme picker's checked row while HOVERED (ring, dot, label). Defaults to the
            // accent so nothing changes; Sepulchre, whose accent is its hover teal, sets white.
            // KillerPDF's key.
            if (!newDict.Contains("RadioHoverFgBrush"))
                newDict["RadioHoverFgBrush"] = newDict["PrimaryBrush"];
            // Keep Alive's per-card event log. Tracks the flyout surface unless a theme names its
            // own; 98SE does, because a scrolling list there is a white client area, not menu gray.
            if (!newDict.Contains("WatchLogBrush"))
                newDict["WatchLogBrush"] = newDict["MenuBackgroundBrush"];
            // Up / down / waiting on a Keep Alive card. The terminal palette already carries a red
            // and green tuned per theme, so those are the default. They are completed here rather
            // than left absent because the merged dictionary is only ever written to: a key that
            // exists on one theme and not the next would otherwise survive the switch.
            var watch = Terminal.TerminalPalette.AnsiFor(theme);
            if (!newDict.Contains("WatchUpBrush"))
                newDict["WatchUpBrush"] = Frozen(watch[2]);
            if (!newDict.Contains("WatchDownBrush"))
                newDict["WatchDownBrush"] = Frozen(watch[1]);
            if (!newDict.Contains("WatchIdleBrush"))
                newDict["WatchIdleBrush"] = Frozen(watch[8]);
            // Accent overlay: Dark/Light/Black recolor their accent-family keys on top of
            // the base green. Green is the base itself, so it needs no overlay (the base
            // palette loaded above is already green). Overlays live in Accents/<Family>/.
            bool accentOwnsMenuHover = false;
            var accent = accentOverride ?? AccentFor(theme);
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
                    var target = newDict;
                    foreach (object key in accentDict.Keys)
                        target[key] = accentDict[key];
                    accentOwnsMenuHover = accentDict.Contains("MenuHoverBrush");
                    if (accentDict.Contains("ComboHighlightTextBrush")) hasExplicitComboHighlightText = true;
                    if (!palette.Contains("RadioHoverFgBrush") && !accentDict.Contains("RadioHoverFgBrush"))
                        target["RadioHoverFgBrush"] = target["PrimaryBrush"];
                    // The picker caption follows the accent's title bar unless the overlay names
                    // its own, the same way the main window's caption does.
                    if (accentDict.Contains("TitleBarBrush") && !accentDict.Contains("DialogTitleBarBrush")
                        && !palette.Contains("DialogTitleBarBrush"))
                        target["DialogTitleBarBrush"] = accentDict["TitleBarBrush"];

                    // OutlineButton consumes the derived roles below, not OutlineBtnBrush
                    // directly. They were created from the base palette before this overlay was
                    // applied, so without refreshing them the table line and selection changed
                    // hue while the Scan button stayed base green. 98SE supplies purpose-built
                    // outline roles in its overlays and must keep those instead.
                    if (theme != Theme.SE98)
                    {
                        target["OutlineTextBrush"] = target["OutlineBtnBrush"];
                        if (!palette.Contains("OutlineRestBrush"))
                            target["OutlineRestBrush"] = target["OutlineBtnBrush"];
                        target["OutlineHoverBrush"] = target["OutlineBtnBrush"];
                        // The hover text sits on that accent fill, so it follows the overlay's
                        // text-on-accent color unless a palette names its own.
                        if (!palette.Contains("OutlineHoverTextBrush") && !accentDict.Contains("OutlineHoverTextBrush"))
                            target["OutlineHoverTextBrush"] = target["OnPrimaryBrush"];
                    }
                }
                catch { /* overlay file not present yet - base theme stands */ }
            }
            Complete("MenuSeparatorBrush", newDict["MenuBorderBrush"]);
            // The wordmark's bold run follows the accent unless a theme states its own
            // (Light/Yellow: yellow text is unreadable, so its text accent is charcoal).
            if (!newDict.Contains("AccentLogo"))
                newDict["AccentLogo"] = newDict["PrimaryBrush"];
            // The two window-like overlays follow the fully merged outer-window surface, including
            // gradients in the material palettes; they are not context menus.
            if (!newDict.Contains("FooterBackgroundBrush"))
                newDict["FooterBackgroundBrush"] = newDict["BackgroundBrush"];
            newDict["OverlayWindowBrush"] = newDict["BackgroundBrush"];
            // Their 1px edges. A single border color over a gradient surface reads as one end's
            // color stuck to the other end (Delirium's red border under its purple side), so a
            // gradient surface gets a gradient edge: the same stops lifted toward white. A solid
            // surface keeps the border it always had.
            Brush? gradientEdge = newDict["BackgroundBrush"] is LinearGradientBrush surface ? LiftedEdge(surface) : null;
            newDict["OverlayEdgeBrush"] = gradientEdge ?? newDict["AppBorderBrush"];
            newDict["OverlayRuleBrush"] = gradientEdge ?? newDict["PaneBorderBrush"];
            if (!newDict.Contains("WordmarkAccentShadowOpacity")) newDict["WordmarkAccentShadowOpacity"] = 0.0;
            if (!hasExplicitComboHighlightText)
                newDict["ComboHighlightTextBrush"] = newDict["PrimaryBrush"];
            // Classic menu highlights follow the chosen accent after its overlay is merged.
            if (theme == Theme.SE98 && !accentOwnsMenuHover) newDict["MenuHoverBrush"] = newDict["PrimaryBrush"];
            // A hovered menu or list item takes the accent for its text, nudged lighter or darker
            // only as far as it takes to read at 4.5:1 on that theme's hover fill. 98SE keeps its
            // Win98 highlight text.
            newDict["MenuHoverTextBrush"] =
                newDict.Contains("UseDialogCaption") && newDict["UseDialogCaption"] is true
                    || newDict["PrimaryBrush"] is not SolidColorBrush hoverAccent
                    || newDict["MenuHoverBrush"] is not SolidColorBrush hoverFill
                ? newDict["ComboHighlightTextBrush"]
                : Frozen(ColorContrast.ReadableOn(hoverAccent.Color, hoverFill.Color, 4.5));
            // A palette can fill buttons with a deeper shade than its accent text (PrimaryFillBaseBrush).
            newDict["PrimaryFillBrush"] = ButtonFill(newDict.Contains("PrimaryFillBaseBrush") ? newDict["PrimaryFillBaseBrush"] : newDict["PrimaryBrush"], theme == Theme.SE98);
            newDict["OutlineHoverBrush"] = ButtonFill(newDict.Contains("OutlineHoverFillBaseBrush") ? newDict["OutlineHoverFillBaseBrush"] : newDict["OutlineHoverBrush"], theme == Theme.SE98);
            Publish(newDict);
        }

        /// <summary>
        /// Attaches a fully built palette as MergedDictionaries[0], in ONE assignment, the way
        /// KillerNotes and KillerShell do. A per-key copy into the live dictionary can overwrite a
        /// key but never remove one, so a key only some themes define (98SE's caption and bevel
        /// tokens) leaked into every theme chosen after it, and every key fired its own
        /// invalidation pass. Building the whole palette off-tree first, accent overlay and derived
        /// roles included, means the swap publishes a finished dictionary and nothing can read a
        /// half-built one.
        /// </summary>
        private static void Publish(ResourceDictionary target)
        {
            var merged = Application.Current.Resources.MergedDictionaries;
            if (merged.Count > 0) merged[0] = target;
            else merged.Add(target);
        }

        /// <summary>
        /// The surface gradient with every stop mixed one fifth of the way to white, which lands
        /// near each gradient theme's own border tone at that end, as KillerShell's card edge does.
        /// </summary>
        internal static LinearGradientBrush LiftedEdge(LinearGradientBrush surface)
        {
            var edge = new LinearGradientBrush
            {
                StartPoint = surface.StartPoint,
                EndPoint = surface.EndPoint,
                MappingMode = surface.MappingMode,
                SpreadMethod = surface.SpreadMethod,
                ColorInterpolationMode = surface.ColorInterpolationMode,
            };
            static byte Lift(byte channel) => (byte)(channel + (255 - channel) / 5);
            foreach (var stop in surface.GradientStops)
                edge.GradientStops.Add(new GradientStop(
                    Color.FromArgb(255, Lift(stop.Color.R), Lift(stop.Color.G), Lift(stop.Color.B)), stop.Offset));
            edge.Freeze();
            return edge;
        }
    }
}
