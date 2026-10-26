using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;

/// <summary>
/// Renders the shipped theme-menu radio and checkbox styles with each real theme palette loaded
/// and measures, in device pixels, how far the ring's center sits from the center of the label's
/// capitals. Half a pixel is rasterization; anything more is a visible misalignment.
/// </summary>
internal static class RadioCenterUiTests
{
    private sealed class TestApplication : KillerScan.App { protected override void OnStartup(StartupEventArgs e) { } }

    public static async Task RunIsolated()
    {
        using var process = Process.Start(new ProcessStartInfo(typeof(RadioCenterUiTests).Assembly.Location, "--radio-center-ui")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => process.WaitForExit(120000))) throw new InvalidOperationException("Radio centering suite finishes within two minutes.");
        Console.Write(await output);
        if (process.ExitCode != 0) throw new InvalidOperationException("Isolated radio centering suite: " + await output + await error);
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

        var failures = new List<string>();
        foreach (string theme in new[] { "Dark", "Light", "Black", "SE98", "Blood" })
        {
            load.Invoke(null, new[] { Enum.Parse(themeType, theme), Enum.Parse(accentType, "Green") });
            foreach (double zoom in new[] { 1.0, 1.25, 1.5, 2.0 })
            foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
            {
                var (dx, dy) = MeasureDot(app, zoom, dpi);
                // A 6 DIP dot leaves a 4 DIP gap to each side of the 14 DIP ring. When 4 x scale is not a whole
                // number of pixels the dot cannot sit exactly in the middle, so half a pixel is allowed there.
                double gap = 4 * zoom * dpi, allowed = Math.Abs(gap - Math.Round(gap)) > 0.01 ? 0.5 : 0.25;
                if (Math.Abs(dx) > allowed || Math.Abs(dy) > allowed) failures.Add($"{theme} dot zoom {zoom} dpi {dpi}: {dx:+0.0;-0.0;0.0},{dy:+0.0;-0.0;0.0}px");
            }
            foreach (string style in new[] { "ThemeRadio", "ThemeCheck" })
            foreach (double zoom in new[] { 1.0, 1.25, 1.5, 2.0 })
            foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
            {
                double offset = Measure(app, style, zoom, dpi);
                // At 100% and 200% the ring must sit on the capitals (half a pixel covers an odd cap
                // height against the even ring). At fractional scales WPF snaps the text to the
                // pixel grid separately from the ring and a one-pixel step remains, so those scales
                // are measured but not enforced.
                double total = zoom * dpi;
                bool whole = Math.Abs(total - 1.0) < 0.01 || Math.Abs(total - 2.0) < 0.01;
                if (whole && Math.Abs(offset) > 0.6) failures.Add($"{theme} {style} zoom {zoom} dpi {dpi}: {offset:+0.0;-0.0}px");
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException("Indicator is off the middle of its label's capitals: " + string.Join("; ", failures));
    }

    /// <summary>Dot center minus ring center on a checked radio, in device pixels (x, y).</summary>
    private static (double, double) MeasureDot(Application app, double zoom, double dpi)
    {
        var root = new Border { Background = Brushes.White, Width = 200, UseLayoutRounding = true, SnapsToDevicePixels = true };
        root.LayoutTransform = new ScaleTransform(zoom, zoom);
        root.Resources["DimTextBrush"] = Brushes.White;
        root.Resources["TextBrush"] = Brushes.White;
        root.Resources["PrimaryBrush"] = new SolidColorBrush(Color.FromRgb(255, 0, 0));
        root.Resources["IconShadowOpacity"] = 0.0;
        var radio = new RadioButton { Content = "HHHH", IsChecked = true, Style = (Style)app.FindResource("ThemeRadio"), Margin = new Thickness(12, 4, 0, 4) };
        root.Child = radio;
        var px = Render(root, zoom, dpi, out int w, out int h);
        // Ring and dot are both the accent when checked. The dot is the ink inside the ring's gap.
        // The checked label is the accent too, so scan only the indicator's own column: the row's
        // 12 margin, -8 border margin and 10 padding put the 16-wide indicator at 14..30.
        int limit = Math.Min(w, (int)Math.Ceiling(31 * zoom * dpi));
        double minX = w, maxX = -1, minY = h, maxY = -1;
        for (int y = 0; y < h; y++) for (int x = 0; x < limit; x++) if (Red(px, w, x, y) > 40) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        if (maxX < 0) throw new InvalidOperationException("The checked radio did not render.");
        double cx = (minX + maxX + 1) / 2, cy = (minY + maxY + 1) / 2, inner = 4.25 * zoom * dpi;
        double ringX = 0, ringY = 0, ringInk = 0, dotX = 0, dotY = 0, dotInk = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < limit; x++)
        {
            int ink = Red(px, w, x, y); if (ink <= 40) continue;
            double ddx = x + 0.5 - cx, ddy = y + 0.5 - cy;
            if (ddx * ddx + ddy * ddy <= inner * inner) { dotX += (x + 0.5) * ink; dotY += (y + 0.5) * ink; dotInk += ink; }
            else { ringX += (x + 0.5) * ink; ringY += (y + 0.5) * ink; ringInk += ink; }
        }
        if (dotInk == 0 || ringInk == 0) throw new InvalidOperationException("The checked radio has no separate dot.");
        return (dotX / dotInk - ringX / ringInk, dotY / dotInk - ringY / ringInk);
    }

    private static int Red(byte[] px, int w, int x, int y)
    {
        int i = (y * w + x) * 4;
        return Math.Max(0, px[i + 2] - Math.Max(px[i], px[i + 1]));
    }

    private static byte[] Render(FrameworkElement root, double zoom, double dpi, out int w, out int h)
    {
        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        w = (int)Math.Ceiling(root.ActualWidth * zoom * dpi); h = (int)Math.Ceiling(root.ActualHeight * zoom * dpi);
        var bitmap = new RenderTargetBitmap(w, h, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var px = new byte[w * h * 4];
        bitmap.CopyPixels(px, w * 4, 0);
        return px;
    }

    /// <summary>Ring center minus capital center, in device pixels. Positive means the ring is low.</summary>
    private static double Measure(Application app, string styleKey, double zoom, double dpi)
    {
        var root = new Border { Background = Brushes.White, Width = 200, UseLayoutRounding = true, SnapsToDevicePixels = true };
        root.LayoutTransform = new ScaleTransform(zoom, zoom);
        // Only the indicator outline and the label are colored, so the scan sees nothing else.
        root.Resources["DimTextBrush"] = new SolidColorBrush(Color.FromRgb(255, 0, 0));
        // On 98SE the checkbox is a sunken well whose bevel covers the ring, so the bevel is the
        // indicator's outline there and is colored the same.
        foreach (string bevel in new[] { "PaneBevelDarkBrush", "PaneBevelLightBrush", "PaneBevelDark2Brush", "PaneBevelLight2Brush" })
            root.Resources[bevel] = new SolidColorBrush(Color.FromRgb(255, 0, 0));
        root.Resources["TextBrush"] = new SolidColorBrush(Color.FromRgb(0, 0, 255));
        root.Resources["PrimaryBrush"] = Brushes.Transparent;
        root.Resources["IconShadowOpacity"] = 0.0;
        // This process is not KillerScan.exe, so "pack://application:,,,/" names the test program
        // and the app's embedded fonts would silently fall back to Segoe UI. Name the assembly so
        // the label draws in the same face the app does.
        if (app.FindResource("UiFont") is FontFamily ui && ui.Source.StartsWith("./Fonts/", StringComparison.Ordinal))
            root.Resources["UiFont"] = new FontFamily(new Uri("pack://application:,,,/KillerScan;component/"), ui.Source);
        Control indicator = styleKey == "ThemeRadio" ? new RadioButton { Content = "HHHH" } : new CheckBox { Content = "HHHH" };
        indicator.Style = (Style)app.FindResource(styleKey);
        indicator.Margin = new Thickness(12, 4, 0, 4);
        root.Child = indicator;

        root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();
        int w = (int)Math.Ceiling(root.ActualWidth * zoom * dpi), h = (int)Math.Ceiling(root.ActualHeight * zoom * dpi);
        var bitmap = new RenderTargetBitmap(w, h, 96 * dpi, 96 * dpi, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var px = new byte[w * h * 4];
        bitmap.CopyPixels(px, w * 4, 0);

        // Weighted by ink, so antialiased edges count for what they are.
        double ringSum = 0, ringInk = 0, capTop = double.MaxValue, capBottom = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int b = px[i], g = px[i + 1], r = px[i + 2];
                int red = Math.Max(0, r - Math.Max(g, b)), blue = Math.Max(0, b - Math.Max(g, r));
                if (red > 40) { ringSum += (y + 0.5) * red; ringInk += red; }
                if (blue > 128) { capTop = Math.Min(capTop, y); capBottom = Math.Max(capBottom, y); }
            }
        if (ringInk == 0 || capBottom < 0) throw new InvalidOperationException(styleKey + " did not render its ring and label.");
        return ringSum / ringInk - (capTop + capBottom + 1) / 2.0;
    }
}
