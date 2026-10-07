using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;

internal static class ReleaseUiTests
{
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private sealed class TestApplication : KillerScan.App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    public static Task Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Capture(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(120)), "Offscreen UI checks finish within two minutes.");
        if (failure != null) throw new InvalidOperationException("Release UI check failed.", failure);
        return Task.CompletedTask;
    }

    private static void Capture()
    {
        var assembly = typeof(KillerScan.App).Assembly;
        typeof(Application).GetField("_resourceAssembly", Static)!.SetValue(null, assembly);
        var app = new TestApplication();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerScan.csproj"))) directory = directory.Parent;
        Require(directory != null, "App resource source exists.");
        var xml = new XmlDocument();
        xml.Load(Path.Combine(directory!.FullName, "App.xaml"));
        string dictionary = xml.DocumentElement!.FirstChild!.FirstChild!.OuterXml
            .Replace("clr-namespace:KillerScan.Controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/KillerScan;component/") };
        context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        context.XmlnsDictionary.Add("controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary, context);
        var manager = assembly.GetType("KillerScan.Services.ThemeManager", true)!;
        var themeType = assembly.GetType("KillerScan.Services.Theme", true)!;
        var accentType = assembly.GetType("KillerScan.Services.Accent", true)!;
        var localeManager = assembly.GetType("KillerScan.Services.LocaleManager", true)!;
        var localeType = assembly.GetType("KillerScan.Services.Locale", true)!;
        assembly.GetType("KillerScan.Services.DemoData", true)!.GetField("Enabled", Static)!.SetValue(null, true);
        void Theme(string theme, string accent)
        {
            manager.GetField("_current", Static)!.SetValue(null, Enum.Parse(themeType, theme));
            manager.GetMethod("LoadDict", Static)!.Invoke(null,
                new[] { Enum.Parse(themeType, theme), Enum.Parse(accentType, accent) });
        }
        void Locale(string locale)
        {
            object value = Enum.Parse(localeType, locale);
            localeManager.GetField("_current", Static)!.SetValue(null, value);
            localeManager.GetMethod("ApplyInternal", Static)!.Invoke(null, new[] { value });
            ((Action?)localeManager.GetField("LocaleChanged", Static)!.GetValue(null))?.Invoke();
        }
        Theme("Black", "Orange");
        Locale("EnUS");
        var window = new KillerScan.Shell.MainWindow();
        var root = (FrameworkElement)window.Content;
        root.Opacity = 1;
        ((UIElement)window.FindName("RootGrid")).Opacity = 1;
        var output = Path.Combine(Path.GetTempPath(), "KillerScan-release-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var samples = new List<(string Label, BitmapSource Image)>();
        int states = 0;
        foreach (string theme in Enum.GetNames(themeType))
        {
            string defaultAccent = theme == "SE98" ? "Blue" : "Orange";
            foreach (string accent in new[] { defaultAccent }.Concat(
                new[] { "Dark", "Light", "Black", "SE98" }.Contains(theme) ? Enum.GetNames(accentType) : Array.Empty<string>()))
            {
                Theme(theme, accent);
                window.GetType().GetMethod("ApplyFlatChrome", Instance)!.Invoke(window, null);
                bool flat = theme == "SE98";
                var panel = new StackPanel { Background = (Brush)app.FindResource("BackgroundBrush"), Width = 370 };
                foreach (string style in new[] { "PrimaryButton", "OutlineButton" })
                {
                    foreach (string state in new[] { "normal", "hover", "pressed", "disabled" })
                    {
                        var button = new Button { Content = style + " " + state, Style = (Style)app.FindResource(style), Margin = new Thickness(8, 3, 8, 3) };
                        panel.Children.Add(button);
                        button.ApplyTemplate();
                        if (state is "hover" or "pressed")
                            button.SetValue((DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", Static)!.GetValue(null)!, true);
                        if (state == "pressed")
                            button.SetValue((DependencyPropertyKey)typeof(ButtonBase).GetField("IsPressedPropertyKey", Static)!.GetValue(null)!, true);
                        if (state == "disabled") button.IsEnabled = false;
                        if (style == "PrimaryButton")
                            Require(flat || button.Background is LinearGradientBrush, theme + "/" + accent + ": primary fill uses a gradient.");
                        if (style == "OutlineButton" && state == "hover")
                        {
                            var border = (Border)button.Template.FindName("border", button);
                            Require(flat || border.Background is LinearGradientBrush, theme + "/" + accent + ": outline hover uses a gradient.");
                        }
                        if (style == "OutlineButton" && state == "pressed")
                            Require(Equals(button.Foreground, app.FindResource("SelectionFg")), "Pressed outline text follows selection contrast.");
                    }
                }
                var image = Render(panel, 370, 340);
                samples.Add((theme + "/" + accent, image));
                states++;
                if (accent == defaultAccent)
                    Save(Render(root, 1200, 780), Path.Combine(output, theme + ".png"));
            }
        }
        Require(states == 45, "All 45 default and explicit theme/accent states render.");
        Theme("Black", "Orange");
        foreach (string locale in Enum.GetNames(localeType))
        {
            Locale(locale);
            window.GetType().GetMethod("RefreshWorkspaceLocale", Instance)!.Invoke(window, null);
            Require(app.FindResource("Str_Btn_Scan") is string text && text.Length > 0, locale + ": scan label resolves.");
            if (locale is "UkUA" or "NbNO" or "PtBR")
                foreach (double scale in new[] { 1.0, 1.5 })
                {
                    window.GetType().GetMethod("ApplyAppScale", Instance)!.Invoke(window, new object[] { scale, false });
                    Save(Render(root, 1200, 780), Path.Combine(output, locale + "-" + scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".png"));
                }
        }
        window.GetType().GetMethod("ApplyAppScale", Instance)!.Invoke(window, new object[] { 1.0, false });
        var menu = new ContextMenu();
        window.GetType().GetMethod("BuildLanguageMenu", Instance)!.Invoke(window, new object[] { menu });
        var columnsPanel = (StackPanel)menu.Items[0];
        var leftColumn = (StackPanel)columnsPanel.Children[0];
        var rightColumn = (StackPanel)columnsPanel.Children[2];
        var languages = leftColumn.Children.Cast<RadioButton>().Concat(rightColumn.Children.Cast<RadioButton>()).ToArray();
        Require(leftColumn.Children.Count == 10 && rightColumn.Children.Count == 9, "Language menu has two balanced columns.");
        foreach (string locale in new[] { "UkUA", "NbNO", "PtBR" })
            Require(languages.Any(radio => Equals(radio.Tag, locale)), locale + ": language is selectable in the menu.");
        Require(languages.Count(radio => radio.IsChecked == true) == 1, "Language menu marks exactly one current locale.");
        menu.Items.Clear();
        var menuSurface = new Border { Child = columnsPanel, Background = (Brush)app.FindResource("MenuBackgroundBrush") };
        Save(Render(menuSurface, 450, 420), Path.Combine(output, "LanguageMenu.png"));
        var workspace = (KillerScan.Controls.ScanWorkspace)window.GetType().GetField("_scanWorkspace", Instance)!.GetValue(window)!;
        workspace.SetView("services");
        Render(root, 1200, 780);
        workspace.SetView("topology");
        Render(root, 1200, 780);
        string svg = (string)workspace.GetType().GetMethod("BuildTopologySvg", Instance)!.Invoke(workspace, null)!;
        Require(svg.Contains("<svg") && svg.Contains("</svg>"), "Topology export contains the full SVG drawing.");
        File.WriteAllText(Path.Combine(output, "topology.html"), svg);
        Save(Render(root, 1200, 780), Path.Combine(output, "Topology.png"));
        var canvas = (Canvas)workspace.FindName("TopologyCanvas");
        Require(canvas.Width > 0 && canvas.Height > 0 && canvas.Children.Count > 0, "Topology has arranged device content.");
        var formatType = workspace.GetType().GetNestedType("SnapshotFormat", BindingFlags.NonPublic)!;
        foreach (string format in new[] { "PngTransparent", "Jpeg" })
        {
            string path = Path.Combine(output, "Topology-export." + (format == "Jpeg" ? "jpg" : "png"));
            workspace.GetType().GetMethod("WriteTopologyRaster", Instance)!.Invoke(workspace,
                new object[] { path, Enum.Parse(formatType, format) });
            using var imageStream = File.OpenRead(path);
            var image = BitmapDecoder.Create(imageStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Require(image.PixelWidth == (int)Math.Ceiling(canvas.Width) && image.PixelHeight == (int)Math.Ceiling(canvas.Height),
                format + ": export covers the entire topology canvas.");
        }
        window.GetType().GetMethod("DisposeWorkspace", Instance)!.Invoke(window, null);
        int columns = 5, rows = (samples.Count + columns - 1) / columns;
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            for (int i = 0; i < samples.Count; i++)
            {
                var rect = new Rect(i % columns * 370, i / columns * 370, 370, 370);
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawText(new FormattedText(samples[i].Label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.Black, 1), new Point(rect.X + 8, rect.Y + 5));
                dc.DrawImage(samples[i].Image, new Rect(rect.X, rect.Y + 28, 370, 340));
            }
        var sheet = new RenderTargetBitmap(columns * 370, rows * 370, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(drawing);
        Save(sheet, Path.Combine(output, "ButtonStates.png"));
        Console.WriteLine("PASS: 45 theme/accent states, 19 locales, new-language scaling, services and topology rendered.");
        Console.WriteLine("Offscreen release UI: " + output);
    }

    private static BitmapSource Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        int painted = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] > 0 && (pixels[i] > 0 || pixels[i + 1] > 0 || pixels[i + 2] > 0)) painted++;
        Require(painted > width * height / 100, "Rendered content must contain visible colored pixels.");
        return image;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
