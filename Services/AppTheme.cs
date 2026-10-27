using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace KillerScan.Services
{
    /// <summary>
    /// KillerScan's side of the family theme engine. ThemeManager.cs is the KillerUI template and
    /// is never edited here; everything particular to KillerScan comes in through its hooks:
    /// the saved settings, the Black/Orange first run, the values in Themes/App, and the tokens
    /// KillerScan's own views derive (topology nodes, Keep Alive cards, the scan panes).
    /// </summary>
    internal static class AppTheme
    {
        /// <summary>Call once before ThemeManager.Initialize or InitializeInstaller.</summary>
        internal static void Configure()
        {
            ThemeManager.GetSetting = App.GetSetting;
            ThemeManager.SetSetting = App.SetSetting;
            ThemeManager.SetDefaults(Theme.Black, dark: Accent.Orange, light: Accent.Orange, black: Accent.Orange, se98: Accent.Blue);
            ThemeManager.AppTokens = AppTokens;
            ThemeManager.AppDerivedTokens = AppDerivedTokens;
            // Dialog captions (Controls/DialogChrome.cs): the file picker's title icon comes from here.
            Controls.DialogChrome.AppName = "KillerScan";
            Controls.DialogChrome.WordmarkSplit = 6;
            Controls.DialogChrome.IconUri = new Uri("pack://application:,,,/KillerScan;component/Resources/ks-icon.ico");
        }

        private static string FileName(Theme theme) => theme == Theme.SE98 ? "98SE" : theme.ToString();

        // Before the family contract: KillerScan's per-theme values, then its accent values, then
        // its defaults for whatever a theme left unsaid.
        private static void AppTokens(ResourceDictionary d, Theme theme, Accent accent)
        {
            Merge(d, $"Themes/App/{FileName(theme)}.xaml", overwrite: true);
            if (ThemeManager.SupportsAccents(theme))
                Merge(d, $"Themes/App/Accents/{FileName(theme)}/{accent}.xaml", overwrite: true);
            Merge(d, "Themes/App/Defaults.xaml", overwrite: false);

            // The title bar subtitle sits one pixel higher on KillerScan's frame than on
            // KillerShell's, so it lines up with the "Killer" baseline here.
            bool flat = d.Contains("UseDialogCaption") && d["UseDialogCaption"] is bool f && f;
            if (!d.Contains("FileNameTransform"))
            {
                var shift = new TranslateTransform(0, flat ? 0 : -1);
                shift.Freeze();
                d["FileNameTransform"] = shift;
            }
        }

        // After the family contract: tokens built from family ones.
        private static void AppDerivedTokens(ResourceDictionary d, Theme theme, Accent accent)
        {
            ThemeManager.Mirror(d, "ScanContentPaneBrush", "ListPaneBrush");

            // Topology nodes take the menu surface unless a theme separates them from the pane.
            ThemeManager.Mirror(d, "TopologyNodeBrush", "MenuBackgroundBrush");
            ThemeManager.Mirror(d, "TopologyNodeBorderBrush", "MenuBorderBrush");
            ThemeManager.Mirror(d, "TopologyNodeHoverBrush", "MenuHoverBrush");
            ThemeManager.Mirror(d, "TopologyNodeTextBrush", "MenuTextBrush");
            ThemeManager.Mirror(d, "TopologyNodeHoverTextBrush", "TopologyNodeTextBrush");

            // Keep Alive: the event log on the flyout surface, and up / down / waiting in the
            // terminal palette's red and green, which are already tuned per theme.
            ThemeManager.Mirror(d, "WatchLogBrush", "MenuBackgroundBrush");
            var ansi = Terminal.TerminalPalette.AnsiFor(theme);
            ThemeManager.SetIfAbsent(d, "WatchUpBrush", Frozen(ansi[2]));
            ThemeManager.SetIfAbsent(d, "WatchDownBrush", Frozen(ansi[1]));
            ThemeManager.SetIfAbsent(d, "WatchIdleBrush", Frozen(ansi[8]));
        }

        private static void Merge(ResourceDictionary d, string path, bool overwrite)
        {
            ResourceDictionary source;
            try { source = new ResourceDictionary { Source = new Uri($"pack://application:,,,/{path}") }; }
            catch (IOException) { return; }   // no file for this theme or accent
            foreach (object key in source.Keys)
                if (overwrite || !d.Contains(key)) d[key] = source[key];
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
