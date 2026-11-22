using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerScan;
using KillerScan.Controls;
using KillerScan.Shell;
using KillerScan.Services;

/// <summary>
/// Landing page screenshots: the real main window with the demo scan, rendered off screen at a normal window size,
/// each shot with its own theme, accent, tab placement and view. Set KS_SITE_SHOTS to the output folder. Not a
/// regression check; it only renders.
/// </summary>
internal static class SiteShots
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    internal static void Run()
    {
        string dir = Environment.GetEnvironmentVariable("KS_SITE_SHOTS") is { Length: > 0 } d ? d
            : throw new InvalidOperationException("Set KS_SITE_SHOTS to the output folder.");
        Directory.CreateDirectory(dir);
        // The app's own demo mode (KillerScan.exe --demo): fabricated network, scripted terminal, demo Keep Alive.
        DemoData.Enabled = true;
        App.SetSetting("TabsAtBottom", "1");
        ThemeManager.Apply(Theme.TrashPolka);
        ThemeManager.ApplyAccent(Theme.TrashPolka, Accent.Red);

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            Width = 1240, Height = 800, ShowActivated = false, ShowInTaskbar = false
        };
        window.Show();
        Pump(800);
        var scan = (FrameworkElement)Field(window, "_scanWorkspace")!;
        if (scan.GetType().GetField("_active", Any)?.GetValue(scan) is { } active
            && active.GetType().GetProperty("Devices")?.GetValue(active) is System.Collections.ICollection { Count: 0 })
            Invoke(scan, "GenerateDemoScan");
        Pump(600);
        var root = (FrameworkElement)window.Content;

        void Shot(string name, Theme theme, Accent accent, bool tabsBottom, Action show, int settle = 900,
            double width = 1100, double height = 720, double scale = 1.0, bool sidebarRight = false)
        {
            window.Width = width; window.Height = height;
            ThemeManager.Apply(theme);
            ThemeManager.ApplyAccent(theme, accent);
            // The theme picker's own follow-up (Theme.cs ThemeRadio_Checked): without it 98SE keeps the
            // wordmark in its navy caption instead of the plain Win98 title.
            Invoke(window, "ApplyFlatChrome");
            Invoke(window, "SetTabPlacement", tabsBottom);
            Invoke(window, "SelectSidebarSide", sidebarRight, false);
            Invoke(window, "ApplyAppScale", scale, false);
            Pump(600);
            show();
            Pump(settle);
            Save(Render(root), Path.Combine(dir, name + ".png"));
            Console.WriteLine($"{name}: {theme} {accent}, {width}x{height} at {scale:P0}, tabs {(tabsBottom ? "bottom" : "top")}, sidebar {(sidebarRight ? "right" : "left")}");
        }

        void Devices() => Invoke(window, "ShowScanView", "devices");
        void SelectRow(int index)
        {
            var grid = Descendants(root).OfType<DataGrid>().FirstOrDefault(g => g.IsVisible);
            if (grid is { Items.Count: > 0 }) { grid.SelectedIndex = Math.Min(index, grid.Items.Count - 1); grid.ScrollIntoView(grid.SelectedItem); }
        }

        Shot("01-trash-polka-devices", Theme.TrashPolka, Accent.Red, true, () => { Devices(); Pump(300); SelectRow(4); },
            width: 1180, height: 760);
        Shot("02-cyanotic-topology", Theme.Cyanotic, Accent.Purple, false, () => Invoke(window, "ShowScanView", "topology"),
            width: 1000, height: 700, scale: 0.9, sidebarRight: true);
        Shot("03-trash-polka-keep-alive", Theme.TrashPolka, Accent.Orange, true, () => Invoke(window, "Watch_Click", window, new RoutedEventArgs()), 6000,
            width: 1120, height: 680);
        Shot("04-black-services", Theme.Black, Accent.Teal, false, () => Invoke(window, "ServicesButton_Click", window, new RoutedEventArgs()),
            width: 960, height: 640, scale: 1.1);
        Shot("05-dark-terminal", Theme.Dark, Accent.Green, true, () => Invoke(window, "NewTerminalView"), 1500,
            width: 800, height: 500, scale: 1.1, sidebarRight: true);
        Shot("06-light-devices", Theme.Light, Accent.Blue, false, () => { Devices(); Pump(300); SelectRow(9); },
            width: 1060, height: 660, scale: 0.9);
        Shot("07-98se-devices", Theme.SE98, Accent.Blue, true, () => { Devices(); Pump(300); SelectRow(2); },
            width: 980, height: 640);
        Shot("08-blood-history", Theme.Blood, Accent.Yellow, true, () =>
        {
            var list = (ListBox)window.FindName("HistoryList");
            if (list.Items.Count == 0) Invoke(window, "RefreshHistoryList");
            Pump(200);
            if (list.Items.Count > 0) { list.SelectedIndex = 0; Invoke(window, "ShowHistoryEntry"); }
            Pump(300);
            // All Devices fills the table; the Changes view of a demo run is only a row or two.
            if (Descendants(root).OfType<HistoryWorkspace>().FirstOrDefault() is { } history) Invoke(history, "SetView", true);
        }, width: 1040, height: 680, sidebarRight: true);
        Shot("09-ectoplasm-shortcuts", Theme.Ectoplasm, Accent.Magenta, false, () => { Devices(); Pump(300); Invoke(window, "ShowShortcuts"); },
            width: 1240, height: 800);
        ((FrameworkElement)window.FindName("ShortcutsOverlay")).Visibility = Visibility.Collapsed;
        // The HTML report of the same demo scan, opening in Trash Polka; a headless browser captures it afterwards.
        if (scan.GetType().GetField("_active", Any)?.GetValue(scan) is { } run
            && run.GetType().GetProperty("Devices")?.GetValue(run) is System.Collections.IEnumerable devices)
        {
            // The demo network is random per run, so the subnet line comes from the scan's own addresses.
            var list = devices.Cast<KillerScan.Engine.NetworkDevice>().ToList();
            string first = list.OrderBy(x => x.IpSortKey).First().IpAddress;
            string subnet = first.Substring(0, first.LastIndexOf('.')) + ".0/24";
            File.WriteAllText(Path.Combine(dir, "10-trash-polka-report.html"), ReportExport.BuildHtml(list, subnet, "trashpolka"));
            Console.WriteLine("10-trash-polka-report: HTML report in Trash Polka");
        }
        window.Close();
        Console.WriteLine("PASS: site shots saved to " + dir);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }

    private static BitmapSource Render(FrameworkElement root)
    {
        root.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        image.Freeze();
        return image;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        ThemeMotif.FinishComposes();
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, Any)?.GetValue(target);

    private static void Invoke(object target, string name, params object[] args)
    {
        var method = target.GetType().GetMethods(Any).First(m => m.Name == name && m.GetParameters().Length == args.Length);
        method.Invoke(target, args);
    }
}
