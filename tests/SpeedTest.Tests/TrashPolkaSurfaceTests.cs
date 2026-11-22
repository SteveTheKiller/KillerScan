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
        ThemeManager.ApplyAccent(Theme.TrashPolka, Accent.Teal);
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
        // Both tab rows, at 100% and at a fractional app size: the active tab's ink has to run into the page above it and
        // below it however big the app is.
        foreach (var (bottom, scale) in new[] { (true, 1.0), (false, 1.0), (true, 1.24), (false, 1.24) })
        {
            Invoke(window, "SetTabPlacement", bottom);
            Invoke(window, "ApplyAppScale", scale, false);
            Pump(400);
            string place = (bottom ? "bottom" : "top") + (scale == 1.0 ? "" : "-" + (int)Math.Round(scale * 100));
            foreach (var view in views)
            {
                view.Show();
                Pump(700);
                var tab = (FrameworkElement)tabs[view.Tab]!;
                CheckPane(window, root, pane, tab, view.Name + "-" + place);
            }
        }
        Invoke(window, "ApplyAppScale", 1.0, false);
        Pump(400);

        // The tables and the terminal are working screens: their ink sits at screen strength (ScreenOpacity, about half
        // the pane's), well under the topology page's, so every row stays readable. The same stretch of the same pinned
        // piece is compared, so only the strength differs. A terminal whose own ink went missing shows the page's full
        // ink under it and fails here too.
        if (InkMeans.TryGetValue("topology-top", out double page))
            foreach (var screen in new[] { "devices-top", "services-top", "devices-bottom", "services-bottom",
                         "terminal-top", "terminal-bottom", "terminal-top-124", "terminal-bottom-124" })
                if (InkMeans.TryGetValue(screen, out double ink))
                {
                    Console.WriteLine($"{screen}: screen ink {ink:F2} against the topology page's {page:F2}, ratio {ink / page:F2}.");
                    if (ink > page * 0.75) Failures.Add($"{screen}: the screen carries the page's full ink ({ink:F2} against {page:F2}), so its rows are hard to read.");
                }

        CheckWatchCards(window, root, views[3].Show, views[2].Show);
        CheckSelectedRow(window, root, scan);

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
        Console.WriteLine("PASS: Trash Polka ink on five views with both tab rows at 100% and 124%, screen-strength tables and terminal, Keep Alive cards, selected rows in every accent, History, About, shortcuts and four dialogs.");
    }

    private static void CheckPane(Window window, FrameworkElement root, FrameworkElement pane, FrameworkElement? tab, string name)
    {
        var (on, off) = RenderBoth(root, name);
        var bounds = pane.TransformToAncestor(root).TransformBounds(new Rect(pane.RenderSize));
        var inner = Inset(bounds, 16);
        var (mean, changed) = Difference(on, off, inner);
        Console.WriteLine($"{name}: pane mean diff {mean:F2}, {changed:P0} of pixels changed.");
        if (mean < 1.0 || changed < 0.25) Failures.Add($"{name}: the pane renders flat, mean diff {mean:F2}, {changed:P0} changed.");
        // A fixed stretch in the lower middle of the pane, clear of headers and toolbars, for the strength comparison.
        InkMeans[name] = Difference(on, off, new Rect(bounds.X + bounds.Width * 0.3, bounds.Y + bounds.Height * 0.45,
            bounds.Width * 0.4, bounds.Height * 0.3)).Mean;
        if (tab is null) return;
        var tabBounds = tab.TransformToAncestor(root).TransformBounds(new Rect(tab.RenderSize));
        bool top = tabBounds.Bottom < bounds.Top + bounds.Height / 2;
        // Keep Alive cards are dark bars of their own over the page, so the join is only measured where the page runs.
        Cards = !name.StartsWith("watch") ? [] : Descendants(root).OfType<Border>()
            .Where(b => b.IsVisible && b.Width == 264 && ThemeMotif.GetPart(b) == MotifPart.Popup)
            .Select(b => b.TransformToAncestor(root).TransformBounds(new Rect(b.RenderSize))).ToList();
        if (top)
        {
            CheckTopJoin(on, off, tab, tabBounds, name);
            return;
        }
        CheckContinuity(on, off, tabBounds, (int)Math.Round(tabBounds.Top), pageAbove: true, name);
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

    private static readonly Dictionary<string, double> InkMeans = [];
    private static List<Rect> Cards = [];

    // A tab above a table joins its column header, which has no ink, so a tab without ink there is right; any tab that carries
    // ink has to run it into the page under it.
    private static void CheckTopJoin(BitmapSource on, BitmapSource off, FrameworkElement tab, Rect tabBounds, string name)
    {
        var (tabMean, _) = Difference(on, off, Inset(tabBounds, 6));
        Console.WriteLine($"{name}: tab mean diff {tabMean:F2}.");
        if (tabMean < 0.3 && (string)tab.Tag is "scan" or "services") { Console.WriteLine($"{name}: tab without ink on the column header."); return; }
        if (tabMean < 0.3) Failures.Add($"{name}: the active tab renders flat against an inked page.");
        CheckContinuity(on, off, tabBounds, (int)Math.Round(tabBounds.Bottom), pageAbove: false, name);
    }

    /// <summary>
    /// The ink has to be ONE image through the join. Only the ink is compared (the inked render minus the plain one), so
    /// stripes and text cancel out. The change from two rows inside the tab to two rows inside the page, column by
    /// column, has to look like any other four-row step inside the page; a tab painting its own copy or a shifted one
    /// breaks every hex and dot at the join, and that step is far larger.
    /// </summary>
    private static void CheckContinuity(BitmapSource on, BitmapSource off, Rect tabBounds, int seam, bool pageAbove, string name)
    {
        int left = (int)Math.Ceiling(tabBounds.Left) + 8, right = (int)Math.Floor(tabBounds.Right) - 8;
        int into = pageAbove ? -1 : 1;
        var a = Pixels(on); var b = Pixels(off);
        // Two rows either side: the Keep Alive cards start three rows under a top tab, and their border is a layout edge,
        // not the page.
        double join = ColumnStep(a, b, on.PixelWidth, seam - into * 2, seam + into * 2, left, right);
        double baseline = 0; int n = 0;
        for (int k = 10; k <= 34; k += 2, n++)
            baseline += ColumnStep(a, b, on.PixelWidth, seam + into * k, seam + into * (k + 4), left, right);
        baseline /= n;
        if (double.IsNaN(join) || double.IsNaN(baseline)) { Console.WriteLine($"{name}: the tab sits over a Keep Alive card, no page at the join."); return; }
        Console.WriteLine($"{name}: ink step across the join {join:F2}, inside the page {baseline:F2}.");
        if (join > baseline * 1.8 + 2) Failures.Add($"{name}: the ink breaks at the join between tab and page ({join:F2} against {baseline:F2} inside the page).");
    }

    private static double ColumnStep(byte[] on, byte[] off, int width, int y1, int y2, int left, int right)
    {
        double Ink(int x, int y)
        {
            int i = (y * width + x) * 4;
            return 0.0722 * (on[i] - off[i]) + 0.7152 * (on[i + 1] - off[i + 1]) + 0.2126 * (on[i + 2] - off[i + 2]);
        }
        double Plain(int x, int y)
        {
            int i = (y * width + x) * 4;
            return 0.0722 * off[i] + 0.7152 * off[i + 1] + 0.2126 * off[i + 2];
        }
        int height = on.Length / 4 / width;
        if (y1 < 0 || y2 < 0 || y1 >= height || y2 >= height) return double.NaN;
        double sum = 0; int count = 0;
        // A column whose plain render already steps here crosses an edge of the layout (a Keep Alive card's top, a
        // node), where a different face rightly starts; only columns where the page simply continues are compared.
        for (int x = Math.Max(0, left); x < Math.Min(width, right); x++)
        {
            if (Math.Abs(Plain(x, y1) - Plain(x, y2)) > 6) continue;
            if (Cards.Any(c => x >= c.Left - 1 && x <= c.Right + 1 && Math.Max(y1, y2) >= c.Top - 1 && Math.Min(y1, y2) <= c.Bottom + 1)) continue;
            sum += Math.Abs(Ink(x, y1) - Ink(x, y2));
            count++;
        }
        return count == 0 ? double.NaN : sum / count;
    }

    // The Keep Alive cards are dark bars over the page, like KillerPDF's annotation bars: the menu face carrying the popup
    // piece. Inside a card the typical pixel is well darker than the Topology page's at the same spot, and the face is
    // inked, not a flat fill.
    private static void CheckWatchCards(Window window, FrameworkElement root, Action showWatch, Action showTopology)
    {
        Invoke(window, "SetTabPlacement", false);
        showWatch();
        Pump(900);
        var card = Descendants(root).OfType<Border>().FirstOrDefault(b => b.IsVisible && b.Width == 264 && ThemeMotif.GetPart(b) == MotifPart.Popup);
        if (card is null) { Failures.Add("watch cards: no Keep Alive card on screen."); return; }
        var area = Inset(card.TransformToAncestor(root).TransformBounds(new Rect(card.RenderSize)), 10);
        var (watchOn, _) = RenderBoth(root, "watch-cards");
        double inCard = MedianLuma(watchOn, area);
        bool inked = card.Background is DrawingBrush;
        showTopology();
        Pump(900);
        var (pageOn, _) = RenderBoth(root, "watch-cards-page");
        double page = MedianLuma(pageOn, area);
        Console.WriteLine($"watch cards: card median luma {inCard:F1} against the page's {page:F1} at the same spot, inked {inked}.");
        if (inCard > page * 0.6) Failures.Add($"watch cards: the cards are not darker than the page ({inCard:F1} against {page:F1}).");
        if (!inked) Failures.Add("watch cards: the card face is a flat fill without the ink.");
    }

    private static double MedianLuma(BitmapSource image, Rect area)
    {
        var p = Pixels(image);
        var values = new List<double>();
        for (int y = Math.Max(0, (int)area.Top); y < Math.Min(image.PixelHeight, (int)area.Bottom); y++)
            for (int x = Math.Max(0, (int)area.Left); x < Math.Min(image.PixelWidth, (int)area.Right); x++)
            {
                int i = (y * image.PixelWidth + x) * 4;
                values.Add(0.0722 * p[i] + 0.7152 * p[i + 1] + 0.2126 * p[i + 2]);
            }
        values.Sort();
        return values.Count == 0 ? double.NaN : values[values.Count / 2];
    }

    // A selected Devices row: every cell's text against the row behind it, read off the render, at 4.5:1 or better.
    private static void CheckSelectedRow(Window window, FrameworkElement root, FrameworkElement scan)
    {
        Invoke(window, "SetTabPlacement", false);
        Invoke(window, "ShowScanView", "devices");
        Pump(500);
        var grid = (DataGrid)scan.FindName("ResultsGrid");
        if (grid.Items.Count < 2) { Failures.Add("selected row: the demo scan left fewer than two rows."); return; }
        grid.SelectedIndex = 1;
        Pump(500);
        foreach (Accent accent in Enum.GetValues(typeof(Accent)))
        {
            ThemeManager.ApplyAccent(Theme.TrashPolka, accent);
            Pump(400);
            grid.SelectedIndex = 1;
            Pump(200);
            if (grid.ItemContainerGenerator.ContainerFromIndex(1) is not DataGridRow row) { Failures.Add($"selected row {accent}: no row container."); continue; }
            CheckSelectedCells(root, row, accent.ToString());
        }
        ThemeManager.ApplyAccent(Theme.TrashPolka, Accent.Teal);
        grid.SelectedIndex = -1;
        Pump(300);
    }

    private static void CheckSelectedCells(FrameworkElement root, DataGridRow row, string accent)
    {
        var image = Render(root);
        if (_shots != null) Save(image, Path.Combine(_shots, "selected-row-" + accent + ".png"));
        var pixels = Pixels(image);
        int column = 0;
        foreach (var cell in Descendants(row).OfType<DataGridCell>())
        {
            column++;
            var r = cell.TransformToAncestor(root).TransformBounds(new Rect(cell.RenderSize));
            if (r.Width < 12 || r.Height < 8) continue;
            var colors = new List<(int R, int G, int B)>();
            for (int y = (int)r.Top + 1; y < (int)r.Bottom - 1; y++)
                for (int x = (int)r.Left + 1; x < (int)r.Right - 1; x++)
                {
                    int i = (y * image.PixelWidth + x) * 4;
                    colors.Add((pixels[i + 2], pixels[i + 1], pixels[i]));
                }
            var bg = colors.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
            int Dist((int R, int G, int B) c) => Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B);
            var text = colors.Where(c => Dist(c) > 40).OrderByDescending(Dist).ToList();
            if (text.Count < 15) continue;
            var core = text.Take(Math.Max(5, text.Count / 5)).ToList();
            var ink = ((int)core.Average(c => c.R), (int)core.Average(c => c.G), (int)core.Average(c => c.B));
            double ratio = Contrast(ink, bg);
            Console.WriteLine($"selected row {accent}: column {column} text #{ink.Item1:X2}{ink.Item2:X2}{ink.Item3:X2} on #{bg.R:X2}{bg.G:X2}{bg.B:X2}, {ratio:F2}:1.");
            if (ratio < 4.5) Failures.Add($"selected row {accent}: column {column} text #{ink.Item1:X2}{ink.Item2:X2}{ink.Item3:X2} on #{bg.R:X2}{bg.G:X2}{bg.B:X2} is {ratio:F2}:1, under 4.5:1.");
        }
    }

    private static double Contrast((int R, int G, int B) a, (int R, int G, int B) b)
    {
        static double Channel(int v) { double c = v / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double Luminance((int R, int G, int B) c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
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
