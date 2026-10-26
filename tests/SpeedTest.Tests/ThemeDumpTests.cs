using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

/// <summary>
/// Writes every resolved theme resource for every theme and accent, one "theme/accent key value"
/// line each, so a theme refactor can be compared value by value before and after. Not a suite:
/// it produces a file and passes or fails nothing. The path comes from KILLERSCAN_THEME_DUMP.
/// </summary>
internal static class ThemeDumpTests
{
    internal static void Run()
    {
        string path = Environment.GetEnvironmentVariable("KILLERSCAN_THEME_DUMP")
            ?? throw new InvalidOperationException("Set KILLERSCAN_THEME_DUMP to the output file.");
        var assembly = typeof(KillerScan.App).Assembly;
        var manager = assembly.GetType("KillerScan.Services.ThemeManager", true)!;
        var themeType = assembly.GetType("KillerScan.Services.Theme", true)!;
        var accentType = assembly.GetType("KillerScan.Services.Accent", true)!;
        var load = manager.GetMethod("LoadDict", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!;
        var lines = new List<string>();
        foreach (var theme in Enum.GetValues(themeType))
            foreach (var accent in Enum.GetValues(accentType))
            {
                manager.GetField("_current", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, theme);
                load.Invoke(null, new[] { theme, accent });
                var keys = new SortedSet<string>(StringComparer.Ordinal);
                void Collect(ResourceDictionary d)
                {
                    foreach (var key in d.Keys) if (key is string s) keys.Add(s);
                    foreach (var child in d.MergedDictionaries) Collect(child);
                }
                foreach (var d in Application.Current.Resources.MergedDictionaries) Collect(d);
                foreach (string key in keys)
                    lines.Add(theme + "/" + accent + " " + key + " " + Describe(Application.Current.TryFindResource(key)));
            }
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        Console.WriteLine("DUMPED: " + lines.Count + " values to " + path);
    }

    private static string Describe(object? value) => value switch
    {
        null => "null",
        SolidColorBrush b => "brush " + b.Color + " o" + F(b.Opacity),
        GradientBrush g => g.GetType().Name + " " + string.Join(",", g.GradientStops.Select(s => s.Color + "@" + F(s.Offset))) + " o" + F(g.Opacity),
        Color c => "color " + c,
        double d => "double " + F(d),
        bool b => "bool " + b,
        Thickness t => "thickness " + t,
        CornerRadius r => "radius " + r,
        DropShadowEffect e => "shadow " + e.Color + " b" + F(e.BlurRadius) + " d" + F(e.ShadowDepth) + " a" + F(e.Direction) + " o" + F(e.Opacity),
        string s => "string " + s.Replace("\n", "\\n"),
        FontFamily f => "font " + f.Source,
        _ => value.GetType().Name,
    };

    private static string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
}
