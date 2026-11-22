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
/// Trash Polka paints its ink on every surface it reaches: each workspace view, the active tab joined to the page,
/// the About and shortcut cards, and the dialogs. Each surface is rendered twice, with the ink on and with the
/// ThemeMotif resource switched off, and the two renders must differ across the surface; a view that covers the
/// pane with a flat fill renders the same both ways. The bottom tab must also run into the page with no seam: the
/// page rows just above the join and the tab rows just below it average within 10 levels of luma, the KillerShell
/// seam rule. Set KS_SURFACE_SHOTS to a folder to keep every render.
/// </summary>
internal static class TrashPolkaSurfaceTests
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const int Width = 1200, Height = 800;
    private static string? _shots;
    private static readonly List<string> Failures = [];

    internal static void Run()
    {
        _shots = Environment.GetEnvironmentVariable("KS_SURFACE_SHOTS");
        if (_shots is { Length: > 0 }) Directory.CreateDirectory(_shots); else _shots = null;
        App.SetSetting("TabsAtBottom", "1");
        ThemeManager.Apply(Theme.TrashPolka);
        Require(Application.Current.TryFindResource("ThemeMotif") is true, "Trash Polka turns the motif on.");

        var window = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            Width = Width, Height = Height, ShowActivated = false, ShowInTaskbar = false
        };
        window.Show();
        Pump(800);
        var scan = (FrameworkElement)Field(window, "_scanWorkspace")!;
        if (scan.GetType().GetField("_active", Any)?.GetValue(scan) is { } active
            && active.GetType().GetProperty("Devices")?.GetValue(active) is System.Collections.ICollection { Count: 0 })
            Invoke(scan, "GenerateDemoScan");
        Pump(400);

        var root = (FrameworkElement)window.Content;
        var pane = (FrameworkElement)window.FindName("DevicesPane");
        var tabs = (System.Collections.IDictionary)Field(window, "_viewTabs")!;
        var views = new (string Name, string Tab, Action Show)[]
        {
            ("devices", "scan", () => Invoke(window, "ShowScanView", "devices")),
            ("services", "services", () => Invoke(window, "ServicesButton_Click", window, new RoutedEventArgs())),
            ("topology", "topology", () => Invoke(window, "ShowScanView", "topology")),
            ("watch", "watch", () => Invoke(window, "Watch_Click", window, new RoutedEventArgs())),
            ("terminal", "terminal", () => Invoke(window, "NewTerminalView")),
        };
        foreach (var view in views)
        {
            view.Show();
            Pump(700);
            var tab = (FrameworkElement)tabs[view.Tab]!;
            CheckPane(window, root, pane, tab, view.Name);
        }

        // History: the first saved run from the demo scan, shown in its own workspace.
        var list = (ListBox)window.FindName("HistoryList");
        if (list.Items.Count == 0) Invoke(window, "RefreshHistoryList");
        Pump(200);
        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
            Invoke(window, "ShowHistoryEntry");
            Pump(700);
            CheckPane(window, root, pane, null, "history");
        }
        else Failures.Add("history: the demo scan left no saved run to show.");

        CheckOverlay(window, root, "AboutOverlay", "ShowAboutOverlay", "about");
        CheckOverlay(window, root, "ShortcutsOverlay", "ShowShortcuts", "shortcuts");
        CheckDialog(new InputDialog("Heading", "Detail text for the dialog.", "Field", "Value", "OK", "Cancel"), "input-dialog");
        CheckDialog(new ConfirmDialog("Heading", "Detail text for the dialog.", "OK"), "confirm-dialog");
        CheckDialog(new TerminalFontDialog("Consolas", 13), "terminal-font-dialog");
        CheckDialog(new FileDialog(), "file-dialog");
        window.Close();

        if (Failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, Failures));
        Console.WriteLine("PASS: Trash Polka ink on six views, the joined bottom tab, About, shortcuts and four dialogs.");
    }

    private static void CheckPane(Window window, FrameworkElement root, FrameworkElement pane, FrameworkElement? tab, string name)
    {
        var (on, off) = RenderBoth(root, name);
        var bounds = pane.TransformToAncestor(root).TransformBounds(new Rect(pane.RenderSize));
        var inner = Inset(bounds, 16);
        var (mean, changed) = Difference(on, off, inner);
        Console.WriteLine($"{name}: pane mean diff {mean:F2}, {changed:P0} of pixels changed.");
        if (mean < 1.0 || changed < 0.25) Failures.Add($"{name}: the pane renders flat, mean diff {mean:F2}, {changed:P0} changed.");
        if (tab is null) return;
        var tabBounds = tab.TransformToAncestor(root).TransformBounds(new Rect(tab.RenderSize));
        // The ink is faint in places, so the tab only has to carry some; the seam rule below checks it matches the page.
        var (tabMean, tabChanged) = Difference(on, off, Inset(tabBounds, 6));
        Console.WriteLine($"{name}: tab mean diff {tabMean:F2}, {tabChanged:P0} changed.");
        if (tabMean < 0.3) Failures.Add($"{name}: the active tab renders flat against an inked page.");
        // The join: the tab's top edge (it overlaps the pane by two DIPs) against the page above it. A table row's
        // stripe can end on the join in both renders, so the plain render's step is taken off and only the ink's
        // own step is left: a flat tab under an inked page leaves the whole ink brightness as a step.
        int seam = (int)Math.Round(tabBounds.Top);
        int left = (int)Math.Ceiling(tabBounds.Left) + 8, right = (int)Math.Floor(tabBounds.Right) - 8;
        double Step(BitmapSource image) =>
            (RowLuma(image, seam - 3, left, right) + RowLuma(image, seam - 2, left, right)) / 2
            - (RowLuma(image, seam + 2, left, right) + RowLuma(image, seam + 3, left, right)) / 2;
        double inked = Step(on), plain = Step(off), inkStep = inked - plain;
        var profile = Enumerable.Range(seam - 5, 11).Select(y => $"{y - seam:+0;-0;0}:{RowLuma(on, y, left, right) - RowLuma(off, y, left, right):F1}");
        Console.WriteLine($"{name}: seam step inked {inked:F2} plain {plain:F2} ink {inkStep:F2}; ink rows {string.Join(" ", profile)}");
        if (Math.Abs(inkStep) >= 10) Failures.Add($"{name}: seam between tab and page, the ink steps {inkStep:F1} luma at the join.");
    }

    private static void CheckOverlay(Window window, FrameworkElement root, string overlayName, string show, string name)
    {
        var overlay = (FrameworkElement)window.FindName(overlayName);
        Invoke(window, show);
        Pump(900);
        var card = Descendants(overlay).OfType<Border>().FirstOrDefault(b => ThemeMotif.GetPart(b) == MotifPart.Split);
        if (card is null) { Failures.Add($"{name}: no inked card under {overlayName}."); return; }
        var (on, off) = RenderBoth(root, name);
        var bounds = card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize));
        // About's info panel stays plain, as KillerPDF's does, so its card is measured across the header band.
        if (name == "about") bounds = new Rect(bounds.X, bounds.Y, bounds.Width, Math.Min(bounds.Height, 90));
        var (mean, changed) = Difference(on, off, Inset(bounds, 8));
        Console.WriteLine($"{name}: card mean diff {mean:F2}, {changed:P0} changed.");
        if (mean < 1.0 || changed < 0.25) Failures.Add($"{name}: the card renders flat, mean diff {mean:F2}.");
        overlay.Visibility = Visibility.Collapsed;
        Pump(200);
    }

    private static void CheckDialog(Window dialog, string name)
    {
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -20000; dialog.Top = -20000;
        dialog.ShowActivated = false; dialog.ShowInTaskbar = false;
        // Display-mode glyph metrics fail on a desktop with no display (DirectWrite NotSupported); the ink is the same
        // either way, so the dialog lays its text out in Ideal mode here.
        TextOptions.SetTextFormattingMode(dialog, TextFormattingMode.Ideal);
        dialog.Show();
        Pump(700);
        // A layered dialog parked off screen can skip its layout pass, so its content is laid out at the dialog's width.
        var root = (FrameworkElement)dialog.Content;
        if (root.ActualWidth <= 0)
        {
            double width = double.IsNaN(dialog.Width) ? 420 : dialog.Width;
            root.Measure(new Size(width, double.PositiveInfinity));
            root.Arrange(new Rect(new Size(width, root.DesiredSize.Height)));
            Pump(300);
        }
        var (on, off) = RenderBoth(root, name);
        var (mean, changed) = Difference(on, off, Inset(new Rect(root.RenderSize), 4));
        Console.WriteLine($"{name}: mean diff {mean:F2}, {changed:P0} changed.");
        if (mean < 0.5 || changed < 0.25) Failures.Add($"{name}: the dialog renders flat, mean diff {mean:F2}.");
        dialog.Close();
    }

    // The same surface with the ink and without it: the app-level override switches the motif off for every
    // element, Retune repaints them, and the terminal's own fill is redrawn.
    private static (BitmapSource On, BitmapSource Off) RenderBoth(FrameworkElement root, string name)
    {
        var on = Render(root);
        Application.Current.Resources["ThemeMotif"] = false;
        ThemeMotif.Retune();
        Invalidate(root);
        Pump(300);
        var off = Render(root);
        Application.Current.Resources.Remove("ThemeMotif");
        ThemeMotif.Retune();
        Invalidate(root);
        Pump(500);
        if (_shots != null)
        {
            Save(on, Path.Combine(_shots, name + "-ink.png"));
            Save(off, Path.Combine(_shots, name + "-plain.png"));
        }
        return (on, off);
    }

    private static void Invalidate(DependencyObject node)
    {
        if (node is UIElement element) element.InvalidateVisual();
        int count = VisualTreeHelper.GetChildrenCount(node);
        for (int i = 0; i < count; i++) Invalidate(VisualTreeHelper.GetChild(node, i));
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
        int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
        var image = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        image.Freeze();
        return image;
    }

    private static byte[] Pixels(BitmapSource image)
    {
        var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(bytes, image.PixelWidth * 4, 0);
        return bytes;
    }

    // Mean absolute channel difference and the share of pixels that moved more than two levels, inside the rect.
    private static (double Mean, double Changed) Difference(BitmapSource a, BitmapSource b, Rect area)
    {
        var pa = Pixels(a); var pb = Pixels(b);
        int x0 = Math.Max(0, (int)area.Left), y0 = Math.Max(0, (int)area.Top);
        int x1 = Math.Min(a.PixelWidth, (int)area.Right), y1 = Math.Min(a.PixelHeight, (int)area.Bottom);
        long total = 0, moved = 0, count = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                int i = (y * a.PixelWidth + x) * 4;
                int d = Math.Abs(pa[i] - pb[i]) + Math.Abs(pa[i + 1] - pb[i + 1]) + Math.Abs(pa[i + 2] - pb[i + 2]);
                total += d; count++;
                if (d > 6) moved++;
            }
        return count == 0 ? (0, 0) : (total / (3.0 * count), moved / (double)count);
    }

    private static double RowLuma(BitmapSource image, int y, int left, int right)
    {
        if (y < 0 || y >= image.PixelHeight) return double.NaN;
        var row = new byte[image.PixelWidth * 4];
        image.CopyPixels(new Int32Rect(0, y, image.PixelWidth, 1), row, row.Length, 0);
        double sum = 0; int n = 0;
        for (int x = Math.Max(0, left); x < Math.Min(image.PixelWidth, right); x++, n++)
            sum += 0.0722 * row[x * 4] + 0.7152 * row[x * 4 + 1] + 0.2126 * row[x * 4 + 2];
        return n == 0 ? double.NaN : sum / n;
    }

    private static Rect Inset(Rect r, double by) =>
        r.Width > by * 2 && r.Height > by * 2 ? new Rect(r.X + by, r.Y + by, r.Width - by * 2, r.Height - by * 2) : r;

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

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
