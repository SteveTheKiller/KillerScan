using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Xml;
using KillerScan.Controls;
using KillerScan.Engine;

internal static class TopologyTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Geometry = typeof(ScanWorkspace).Assembly.GetType("KillerScan.Controls.TopologyGeometry", true)!;
    private static int _checks;

    private sealed class TestApplication : KillerScan.App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    public static Task Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckGeometry(); CheckUi(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(120)), "Topology checks finish within two minutes.");
        if (failure != null) throw new InvalidOperationException("Topology regression failed.", failure);
        Console.WriteLine("PASS: " + _checks + " topology geometry, zoom, interaction and rendering assertions.");
        return Task.CompletedTask;
    }

    public static async Task RunIsolated()
    {
        // WPF permits one Application per process. The terminal suite owns another one.
        using var process = Process.Start(new ProcessStartInfo(typeof(TopologyTests).Assembly.Location, "--topology")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Require(await Task.Run(() => process.WaitForExit(120000)), "Isolated topology suite finishes within two minutes.");
        Console.Write(await output);
        Require(process.ExitCode == 0, "Isolated topology suite: " + await error);
    }

    private static void CheckGeometry()
    {
        double Wheel(double zoom, int delta) => (double)Geometry.GetMethod("WheelZoom", Static)!.Invoke(null, new object[] { zoom, delta })!;
        Require(Wheel(1, -120) < 1 && Wheel(1, 120) > 1, "Wheel direction controls zoom.");
        Require(Wheel(1, -12000) == 0.25 && Wheel(1, 12000) == 3, "Zoom is bounded at 25% and 300%.");
        Require(Wheel(1, 0) == 1 && Wheel(1, 60) < Wheel(1, 120), "Zero and partial wheel deltas work.");
        foreach (double zoom in new[] { 0.25, 0.75, 1, 2, 3 })
            foreach (double next in new[] { 0.25, 0.5, 1, 2, 3 })
                foreach (double pointer in new[] { 0.0, 35, 400 })
                {
                    double offset = (double)Geometry.GetMethod("AnchoredOffset", Static)!.Invoke(null, new object[] { 200.0, pointer, zoom, next })!;
                    Require(Math.Abs((200 + pointer) / zoom - (offset + pointer) / next) < 0.00001, "Anchoring preserves the world coordinate.");
                }
        foreach (int count in new[] { 0, 1, 2, 8, 28, 31, 64, 120 })
            foreach (Size viewport in new[] { new Size(320, 240), new Size(900, 500), new Size(1600, 900), new Size(500, 1000) })
                foreach (bool horizontal in new[] { false, true })
                {
                    var sizes = Enumerable.Range(0, count).Select(i => new Size(90 + i % 5 * 42, 40 + i % 3 * 12)).ToArray();
                    var reserved = new[] { new Rect(-63, -20, 126, 40), new Rect(-63, -116, 126, 40) };
                    object[] args = { sizes, reserved, viewport.Width, viewport.Height, 12.0, horizontal };
                    var points = (Point[])Geometry.GetMethod("Radial", Static)!.Invoke(null, args)!;
                    var repeat = (Point[])Geometry.GetMethod("Radial", Static)!.Invoke(null, args)!;
                    Require(points.SequenceEqual(repeat), "Radial layout is deterministic.");
                    var bounds = reserved.ToList();
                    for (int i = 0; i < count; i++)
                    {
                        var box = new Rect(points[i].X - sizes[i].Width / 2, points[i].Y - sizes[i].Height / 2, sizes[i].Width, sizes[i].Height);
                        Require(!double.IsNaN(box.X) && !double.IsInfinity(box.X), "Radial coordinates are finite.");
                        var padded = box;
                        padded.Inflate(11.9, 11.9);
                        Require(bounds.All(other => !padded.IntersectsWith(other)), "Varied box widths and heights keep clearance.");
                        bounds.Add(box);
                    }
                }
    }

    private static void CheckUi()
    {
        var assembly = typeof(KillerScan.App).Assembly;
        typeof(Application).GetField("_resourceAssembly", Static)!.SetValue(null, assembly);
        var app = new TestApplication();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(System.IO.Path.Combine(directory.FullName, "KillerScan.csproj"))) directory = directory.Parent;
        Require(directory != null, "Resource source is available.");
        var xml = new XmlDocument();
        xml.Load(System.IO.Path.Combine(directory!.FullName, "App.xaml"));
        string dictionary = xml.DocumentElement!.FirstChild!.FirstChild!.OuterXml
            .Replace("clr-namespace:KillerScan.Controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/KillerScan;component/") };
        context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        context.XmlnsDictionary.Add("controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary, context);
        assembly.GetType("KillerScan.Services.DemoData", true)!.GetField("Enabled", Static)!.SetValue(null, true);
        var manager = assembly.GetType("KillerScan.Services.ThemeManager", true)!;
        var themeType = assembly.GetType("KillerScan.Services.Theme", true)!;
        var accentType = assembly.GetType("KillerScan.Services.Accent", true)!;
        void Theme(string theme, string accent)
        {
            manager.GetField("_current", Static)!.SetValue(null, Enum.Parse(themeType, theme));
            manager.GetMethod("LoadDict", Static)!.Invoke(null, new[] { Enum.Parse(themeType, theme), Enum.Parse(accentType, accent) });
        }
        var locales = assembly.GetType("KillerScan.Services.LocaleManager", true)!;
        var localeType = assembly.GetType("KillerScan.Services.Locale", true)!;
        locales.GetMethod("ApplyInternal", Static)!.Invoke(null, new[] { Enum.Parse(localeType, "EnUS") });
        Theme("Black", "Orange");
        using var workspace = new ScanWorkspace("192.0.2.0/24", demo: true);
        Call(workspace, "LoadSnapshot", "192.0.2.0/24", Enumerable.Range(1, 31).Select(i => new NetworkDevice
        {
            IpAddress = "192.0.2." + i, Hostname = i % 3 == 0 ? "synthetic-server-with-a-long-label-" + i : "device-" + i,
            DeviceType = i == 1 ? "Router" : i % 2 == 0 ? "Windows" : "Printer"
        }).ToArray());
        ((TextBlock)workspace.FindName("GatewayLabel")).Text = "192.0.2.1";
        ((TextBlock)workspace.FindName("LocalIpLabel")).Text = "192.0.2.2";
        ((TextBlock)workspace.FindName("DnsLabel")).Text = "192.0.2.3";
        workspace.SetView("topology");
        var options = (Dictionary<string, string>)workspace.GetType().GetField("_topologyOptions", Instance)!.GetValue(workspace)!;
        options["Pattern"] = "Radial";
        var canvas = (Canvas)workspace.FindName("TopologyCanvas");
        var scroll = (ScrollViewer)workspace.FindName("TopologyScrollViewer");
        var grid = (DataGrid)workspace.FindName("ResultsGrid");
        var root = new Border { Child = workspace };
        root.SetResourceReference(Border.BackgroundProperty, "BackgroundBrush");
        // Register the offscreen tree with Application so live palette invalidation
        // reaches its DynamicResource expressions. The window is never shown.
        var window = new Window { Content = root };
        Layout(root, 900, 580);
        Call(workspace, "RefreshTopology");
        Layout(root, 900, 580);
        CheckBoxes(canvas);
        var output = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KillerScan-topology-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        string baselineSvg = (string)Call(workspace, "BuildTopologySvg")!;
        var snapshotFormat = workspace.GetType().GetNestedType("SnapshotFormat", BindingFlags.NonPublic)!;
        var transparent = Enum.Parse(snapshotFormat, "PngTransparent");
        string baselineRaster = System.IO.Path.Combine(output, "export-100.png");
        Call(workspace, "WriteTopologyRaster", baselineRaster, transparent);
        Require(canvas.Children.OfType<Border>().Count(n => n.Tag is NetworkDevice) == 31, "Every synthetic device has one node.");
        Require(canvas.Children.OfType<Line>().Count() == 31, "Topology links retain every device.");
        double oldRadius = 28 * (Math.Sqrt(126 * 126 + 40 * 40) + 22) / (2 * Math.PI);
        Require(canvas.Width * canvas.Height < (2 * oldRadius + 170) * (2 * oldRadius + 84) * 0.65,
            "The 31-device radial canvas uses substantially less area than the single ring.");
        var nodes = canvas.Children.OfType<Border>().ToArray();
        var original = nodes.Select(Bounds).ToArray();
        Require(!Wheel(workspace, -120, ModifierKeys.None, new Point(100, 100)), "Ordinary wheel is left to the ScrollViewer.");
        Require(Zoom(workspace) == 1, "Ordinary wheel does not zoom.");
        Call(workspace, "SetTopologyZoom", 2.0, new Point());
        Layout(root, 900, 580);
        scroll.ScrollToHorizontalOffset(160);
        scroll.ScrollToVerticalOffset(110);
        Layout(root, 900, 580);
        Require(scroll.HorizontalOffset == 160 && scroll.VerticalOffset == 110, "Both scroll axes pan the zoomed map.");
        var pointer = new Point(100, 80);
        var before = new Point((scroll.HorizontalOffset + pointer.X) / Zoom(workspace), (scroll.VerticalOffset + pointer.Y) / Zoom(workspace));
        Require(Wheel(workspace, 120, ModifierKeys.Control, pointer), "Ctrl+wheel is consumed.");
        Layout(root, 900, 580);
        var after = new Point((scroll.HorizontalOffset + pointer.X) / Zoom(workspace), (scroll.VerticalOffset + pointer.Y) / Zoom(workspace));
        Require((before - after).Length < 0.1, "Real ScrollViewer zoom anchors both axes.");
        Wheel(workspace, 120, ModifierKeys.Control, pointer);
        Wheel(workspace, 120, ModifierKeys.Control, pointer);
        Layout(root, 900, 580);
        var rapid = new Point((scroll.HorizontalOffset + pointer.X) / Zoom(workspace), (scroll.VerticalOffset + pointer.Y) / Zoom(workspace));
        Require((before - rapid).Length < 0.1, "Rapid wheel events retain the same anchor before a render pass.");
        Require(nodes.SequenceEqual(canvas.Children.OfType<Border>()) && original.SequenceEqual(nodes.Select(Bounds)),
            "Zoom keeps existing nodes and world coordinates.");
        Wheel(workspace, 12000, ModifierKeys.Control, pointer);
        Layout(root, 900, 580);
        Require(Zoom(workspace) == 3, "Runtime zoom clamps at 300%.");
        Wheel(workspace, -12000, ModifierKeys.Control, pointer);
        Layout(root, 900, 580);
        Require(Zoom(workspace) == 0.25 && scroll.HorizontalOffset >= 0 && scroll.VerticalOffset >= 0, "Zoom out clamps offsets at edges.");
        Call(workspace, "SetTopologyZoom", 0.75, new Point());
        Layout(root, 900, 580);
        Require(baselineSvg == (string)Call(workspace, "BuildTopologySvg")!, "Zoom preserves full SVG export coordinates.");
        string zoomedRaster = System.IO.Path.Combine(output, "export-75.png");
        Call(workspace, "WriteTopologyRaster", zoomedRaster, transparent);
        Require(File.ReadAllBytes(baselineRaster).SequenceEqual(File.ReadAllBytes(zoomedRaster)), "Zoom preserves the full raster export.");
        Save(Render(root, 900, 580, 96), System.IO.Path.Combine(output, "Black-31-zoom75.png"));
        var deviceNode = canvas.Children.OfType<Border>().First(n => n.Tag is NetworkDevice);
        var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = deviceNode };
        deviceNode.RaiseEvent(click);
        Require(ReferenceEquals(grid.SelectedItem, deviceNode.Tag) && deviceNode.BorderThickness.Left == 2, "Selection works while zoomed.");
        deviceNode.ReleaseMouseCapture();
        var point = deviceNode.TranslatePoint(new Point(deviceNode.Width / 2, deviceNode.Height / 2), root);
        DependencyObject? hit = VisualTreeHelper.HitTest(root, point)?.VisualHit;
        while (hit != null && hit != deviceNode) hit = VisualTreeHelper.GetParent(hit);
        Require(hit == deviceNode, "Hit testing follows the scaled node: point=" + point + ", hit=" + hit?.GetType().Name +
            ", root=" + root.ActualWidth + "," + root.ActualHeight + ", offset=" + scroll.HorizontalOffset + "," + scroll.VerticalOffset);
        var dragStarts = (Dictionary<Border, Point>)workspace.GetType().GetField("_topologyDragStarts", Instance)!.GetValue(workspace)!;
        var initialBox = Bounds(deviceNode);
        dragStarts.Clear();
        dragStarts.Add(deviceNode, initialBox.TopLeft);
        var screenStart = deviceNode.TranslatePoint(new Point(), root);
        var worldStart = root.TranslatePoint(screenStart, canvas);
        var worldEnd = root.TranslatePoint(screenStart + new Vector(15, 9), canvas);
        Call(workspace, "MoveTopologyNodes", worldEnd - worldStart);
        Require(Math.Abs(Canvas.GetLeft(deviceNode) - initialBox.Left - 20) < 0.1 &&
            Math.Abs(Canvas.GetTop(deviceNode) - initialBox.Top - 12) < 0.1, "Dragging maps screen motion back through zoom.");
        var device = (NetworkDevice)deviceNode.Tag;
        var linksByIp = (Dictionary<string, Line>)workspace.GetType().GetField("_topologyLinks", Instance)!.GetValue(workspace)!;
        Require(linksByIp[device.IpAddress].X2 == Canvas.GetLeft(deviceNode) + deviceNode.Width / 2 &&
            linksByIp[device.IpAddress].Y2 == Canvas.GetTop(deviceNode) + deviceNode.Height / 2, "Dragging updates link endpoints.");
        Call(workspace, "MoveTopologyNodes", new Vector(100000, -100000));
        Require(Canvas.GetLeft(deviceNode) == canvas.Width - deviceNode.Width && Canvas.GetTop(deviceNode) == 0,
            "Dragging stays within canvas bounds at nondefault zoom.");
        Call(workspace, "MoveTopologyNodes", new Vector());
        dragStarts.Clear();
        ((Dictionary<string, Point>)workspace.GetType().GetField("_topologyPositions", Instance)!.GetValue(workspace)!).Clear();
        Call(workspace, "TopologyNode_RightClick", deviceNode,
            new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonDownEvent, Source = deviceNode });
        Require(grid.ContextMenu.PlacementTarget == deviceNode, "Device menu remains anchored to the selected box.");
        grid.ContextMenu.IsOpen = false;
        var settingsButton = (Button)workspace.FindName("TopologyOrderButton");
        var zoomMenu = settingsButton.ContextMenu.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "Ctrl+Wheel");
        var normal = zoomMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, 1.0));
        normal.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Layout(root, 900, 580);
        Require(Zoom(workspace) == 1 && normal.IsChecked, "Menu provides keyboard-accessible zoom recovery.");
        scroll.ScrollToHorizontalOffset(0);
        scroll.ScrollToVerticalOffset(0);
        int states = 0;
        foreach (string theme in Enum.GetNames(themeType))
            foreach (string accent in new[] { "Dark", "Light", "Black", "SE98" }.Contains(theme)
                ? Enum.GetNames(accentType) : new[] { "Orange" })
            {
                Theme(theme, accent);
                Require(Equals(canvas.Children.OfType<Border>().First().Background, app.FindResource("TopologyNodeBrush")),
                    theme + "/" + accent + ": nodes resolve the current palette.");
                foreach (double dpi in new[] { 96.0, 144, 192 })
                {
                    Layout(root, 900, 580);
                    CheckBoxes(canvas);
                    var image = Render(root, 900, 580, dpi);
                    Require(image.PixelWidth == (int)(900 * dpi / 96), "Rendering supports multiple pixel densities.");
                    if (dpi == 96 && accent == (theme == "SE98" ? "Blue" : "Orange")) Save(image, System.IO.Path.Combine(output, theme + "-31.png"));
                    states++;
                }
            }
        foreach (double scale in new[] { 0.7, 1.0, 1.5, 2.5 })
        {
            workspace.ApplyScale(scale);
            foreach (Size size in new[] { new Size(480, 360), new Size(1400, 900) })
                foreach (string orientation in new[] { "Vertical", "Horizontal" })
                {
                    options["Orientation"] = orientation;
                    Layout(root, size.Width, size.Height);
                    Call(workspace, "RefreshTopology");
                    Layout(root, size.Width, size.Height);
                    CheckBoxes(canvas);
                    Render(root, (int)size.Width, (int)size.Height, 96);
                }
        }
        workspace.ApplyScale(1);
        Call(workspace, "SetTopologyZoom", 2.0, new Point());
        Call(workspace, "TopologyReset_Click", workspace, new RoutedEventArgs());
        Layout(root, 900, 580);
        Require(Zoom(workspace) == 1 && scroll.HorizontalOffset == 0 && scroll.VerticalOffset == 0 && options["Pattern"] == "Hierarchy",
            "Existing reset restores layout, zoom and scroll position.");
        Console.WriteLine("Rendered " + states + " theme/accent/DPI states; synthetic evidence: " + output);
        window.Close();
        app.Shutdown();
    }

    private static void CheckBoxes(Canvas canvas)
    {
        var boxes = canvas.Children.OfType<Border>().Select(Bounds).ToArray();
        foreach (var box in boxes)
            Require(box.Left >= 0 && box.Top >= 0 && box.Right <= canvas.Width + 0.01 && box.Bottom <= canvas.Height + 0.01,
                "Node stays within the canvas.");
        for (int i = 0; i < boxes.Length; i++)
            for (int j = i + 1; j < boxes.Length; j++) Require(!boxes[i].IntersectsWith(boxes[j]), "Automatic nodes do not overlap.");
    }

    private static Rect Bounds(Border node) => new Rect(Canvas.GetLeft(node), Canvas.GetTop(node), node.Width, node.Height);
    private static double Zoom(ScanWorkspace workspace) => (double)workspace.GetType().GetField("_topologyZoom", Instance)!.GetValue(workspace)!;
    private static bool Wheel(ScanWorkspace workspace, int delta, ModifierKeys modifiers, Point pointer) =>
        (bool)Call(workspace, "HandleTopologyWheel", delta, modifiers, pointer)!;
    private static object? Call(object instance, string method, params object[] args) => instance.GetType().GetMethod(method, Instance)!.Invoke(instance, args);
    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
        root.UpdateLayout();
    }
    private static BitmapSource Render(FrameworkElement root, int width, int height, double dpi)
    {
        var image = new RenderTargetBitmap((int)(width * dpi / 96), (int)(height * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
        image.Render(root);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        Require(pixels.Where((value, index) => index % 4 == 3 && value > 0).Count() > image.PixelWidth * image.PixelHeight / 4,
            "Topology renders visible pixels.");
        return image;
    }
    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static void Require(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
