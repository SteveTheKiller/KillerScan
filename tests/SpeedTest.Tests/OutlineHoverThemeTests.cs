using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;

internal static class OutlineHoverThemeTests
{
    private sealed class TestApplication : KillerScan.App { protected override void OnStartup(StartupEventArgs e) { } }

    public static async Task RunIsolated()
    {
        using var process = Process.Start(new ProcessStartInfo(typeof(OutlineHoverThemeTests).Assembly.Location, "--outline-hover-ui")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => process.WaitForExit(60000))) throw new InvalidOperationException("Outline hover suite finishes within a minute.");
        Console.Write(await output);
        if (process.ExitCode != 0) throw new InvalidOperationException("Isolated outline hover suite: " + await output + await error);
    }

    public static Task Run()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Check(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
        return Task.CompletedTask;
    }

    private static void Check()
    {
        var directory = TestRepository.Find();
        if (directory == null) throw new InvalidOperationException("Repository app resources found.");
        var assembly = typeof(KillerScan.App).Assembly;
        typeof(Application).GetField("_resourceAssembly", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, assembly);
        var app = new TestApplication();
        KillerScan.Services.AppTheme.Configure();
        var xml = new XmlDocument();
        xml.Load(Path.Combine(directory.FullName, "App.xaml"));
        string dictionary = xml.DocumentElement!.FirstChild!.FirstChild!.OuterXml
            .Replace("clr-namespace:KillerScan.Controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan")
            .Replace("clr-namespace:KillerFamily.Layout\"", "clr-namespace:KillerFamily.Layout;assembly=KillerScan\"")
            .Replace("clr-namespace:KillerFamily.Controls\"", "clr-namespace:KillerFamily.Controls;assembly=KillerScan\"");
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/KillerScan;component/") };
        context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        context.XmlnsDictionary.Add("controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
        context.XmlnsDictionary.Add("snap", "clr-namespace:KillerFamily.Windowing;assembly=KillerScan");
        context.XmlnsDictionary.Add("kl", "clr-namespace:KillerFamily.Layout;assembly=KillerScan");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary, context);

        var manager = assembly.GetType("KillerScan.Services.ThemeManager", true)!;
        var themeType = assembly.GetType("KillerScan.Services.Theme", true)!;
        var accentType = assembly.GetType("KillerScan.Services.Accent", true)!;
        var load = manager.GetMethod("LoadDict", BindingFlags.NonPublic | BindingFlags.Static)!;

        // Hover text on a filled outline button must stay readable in every accent of the families
        // whose accent overlay recolors the outline button.
        var failures = new List<string>();
        foreach (string theme in new[] { "Light", "Dark" })
            foreach (string accent in Enum.GetNames(accentType))
            {
                load.Invoke(null, new[] { Enum.Parse(themeType, theme), Enum.Parse(accentType, accent) });
                var palette = Application.Current.Resources.MergedDictionaries[0];
                if (palette["OutlineHoverTextBrush"] is not SolidColorBrush text)
                    throw new InvalidOperationException($"{theme}/{accent} outline hover text is not a solid color.");
                // The raised button face is a gradient; the text has to read on every stop of it.
                var fills = palette["OutlineHoverBrush"] switch
                {
                    SolidColorBrush solid => new[] { solid.Color },
                    GradientBrush gradient => gradient.GradientStops.Select(stop => stop.Color).ToArray(),
                    var other => throw new InvalidOperationException($"{theme}/{accent} outline hover fill is {other?.GetType().Name}."),
                };
                foreach (var fill in fills)
                {
                    double ratio = Contrast(text.Color, fill);
                    if (ratio < 4.5) failures.Add($"{theme}/{accent} {text.Color} on {fill} = {ratio:0.00}:1");
                }
            }
        if (failures.Count > 0)
            throw new InvalidOperationException("Outline hover text is below 4.5:1: " + string.Join("; ", failures));
    }

    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
