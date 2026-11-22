using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerFamily.Controls;

// A customizable card is rebuilt on every open. Each build takes the last build's handlers off the card, so
// opening it again and again never piles up handlers or keeps old tile trees alive. While editing, the keyboard
// reaches everything the mouse does: Tab walks the card's tiles, Reset, the drawer's tiles and the X, Enter moves
// a tile and the keyboard follows it to the other side, Reset puts the default back, and the X hands focus to the
// pencil. In the drawer the keyboard is a drawn cursor while focus stays on the card, so the card never closes.
internal static class CardEditTests
{
    private const string EditTip = "Customize this card";
    private const string DoneTip = "Done";
    private const string ResetTip = "Reset to default";

    internal static void Run()
    {
        string? saved = null;
        var edit = new CardEdit(() => saved, value => saved = value) { DefaultHidden = t => t.Id == "spare", EditTip = EditTip, DoneTip = DoneTip, ResetTip = ResetTip };
        var card = CardBuilder.Menu();

        // Twenty more opens leave the card with exactly the handlers one open gave it, and the first build's tree goes.
        var first = BuildAway(card, edit);
        var counts = HandlerCounts(card);
        for (int i = 0; i < 20; i++) Build(card, edit);
        var after = HandlerCounts(card);
        Require(counts.SequenceEqual(after), $"Reopening the card keeps its handler count: {string.Join(",", counts)} then {string.Join(",", after)}.");
        // WPF's layout queue holds every new element until a layout pass runs, so one runs before collecting.
        Pump();
        for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        Require(!first.IsAlive, "A card's earlier build is released once the card is rebuilt.");
        Console.WriteLine("PASS: rebuilding a customizable card never piles up handlers or old tiles.");

        var host = new Border { Width = 640, Height = 420 };
        var window = new Window
        {
            Content = host, Width = 800, Height = 600, Left = -20000, Top = -20000,
            ShowInTaskbar = false, WindowStyle = WindowStyle.None
        };
        // Active, as the app's own window is when its card opens: a popup only takes keyboard focus for an active window.
        window.Show();
        window.Activate();
        try
        {
            var strip = Build(card, edit);
            card.PlacementTarget = host;
            card.Placement = PlacementMode.Bottom;
            card.IsOpen = true;
            Pump();
            Require(card.IsOpen, "The card opens.");

            var pencil = Descendants(strip).OfType<Button>().First(b => AutomationProperties.GetName(b) == EditTip);
            // A popup only takes keyboard focus on the input desktop, so this runs in a signed-in session, never a hidden one.
            Require(pencil.Focus(), "The pencil takes keyboard focus.");
            Press(Key.Enter);
            Require(CardBuilder.IsEditing(card), "Enter on the pencil opens editing.");
            Require(Spot(card) == "Copy" && OnCard(card), $"Editing starts on the card's first tile, not {Spot(card)}.");

            // Enter hides the tile, the keyboard follows it into the drawer, and Enter there brings it back. The card
            // stays open and keeps keyboard focus the whole time: focus in the drawer's own popup would shut the menu.
            Press(Key.Enter);
            Require(saved == "v2:paste|delete|find/copy|spare", $"Enter sends Copy to the drawer: {saved}.");
            Require(Spot(card) == "Copy" && CardBuilder.KeyboardCursor(card) is not null, $"The keyboard follows Copy into the drawer, not {Spot(card)}.");
            Require(Open(card), "The keyboard in the drawer keeps the card open, editing and focused.");
            var tile = Face(CardBuilder.KeyboardCursor(card)!);
            var accent = ((SolidColorBrush)Application.Current.FindResource("PrimaryBrush")).Color;
            string tileShot = Shoot(tile, "drawer-tile");
            var edge = LeftEdge(tile, accent);
            Require(edge.Hits >= 8, $"The drawer tile under the keyboard takes the accent edge: {edge.Hits} accent pixels down {edge.Where} ({tileShot}).");
            Press(Key.Enter);
            Require(saved == "v2:paste|delete|find|copy/spare", $"Enter in the drawer brings Copy back to the end of the card: {saved}.");
            Require(Focused() == "Copy" && OnCard(card) && CardBuilder.KeyboardCursor(card) is null, "Focus follows Copy back onto the card.");

            // Tab walks the card's tiles, Reset, the drawer's tiles and the X, then wraps to the first tile.
            var walked = new List<string?>();
            for (int i = 0; i < 7; i++) { Press(Key.Tab); walked.Add(Spot(card)); Require(Open(card), $"Tab to {Spot(card)} keeps the card open."); }
            Require(walked.SequenceEqual(new[] { ResetTip, "Spare", DoneTip, "Paste", "Delete", "Find", "Copy" }),
                $"Tab walks the whole card and drawer: {string.Join(", ", walked)}.");

            // The X shows its red face under the keyboard, and Reset its underline.
            Press(Key.Tab); Press(Key.Tab); Press(Key.Tab);
            Require(Spot(card) == DoneTip, "Tab reaches the X.");
            var done = (Button)CardBuilder.KeyboardCursor(card)!;
            string shot = Shoot(done, "x-keyboard");
            var red = ((SolidColorBrush)Application.Current.FindResource("CaptionCloseHoverBrush")).Color;
            var at = Pixel(done, new Point(3, 11));
            Require(Near(at, red), $"The X under the keyboard is drawn in the close hover red: {at} against {red} ({shot}).");
            for (int i = 0; i < 5; i++) Press(Key.Tab);
            Require(Spot(card) == ResetTip, $"Tab reaches Reset, not {Spot(card)}.");
            Require(!Near(Pixel(done, new Point(3, 11)), red), "The X drops its red face once the keyboard leaves it.");
            var reset = (Button)CardBuilder.KeyboardCursor(card)!;
            Require(reset.Content is TextBlock { TextDecorations: { Count: > 0 } }, "Reset under the keyboard is underlined.");
            Shoot(reset, "reset-keyboard");

            // Reset from the keyboard puts the default back and lands on the first tile.
            Press(Key.Enter);
            Require(saved is null, $"Enter on Reset restores the default layout: {saved}.");
            Require(Focused() == "Copy" && OnCard(card) && Open(card), $"After Reset focus is on the first tile, not {Spot(card)}.");
            Require(reset.Content is TextBlock { TextDecorations: null or { Count: 0 } }, "Reset drops its underline once the keyboard leaves it.");

            // The X from the keyboard ends editing and gives focus back to the pencil.
            for (int i = 0; i < 10 && Spot(card) != DoneTip; i++) Press(Key.Tab);
            Require(Spot(card) == DoneTip, "Tab reaches the X again.");
            Press(Key.Enter);
            Require(!CardBuilder.IsEditing(card), "Enter on the X ends editing.");
            Require(ReferenceEquals(Keyboard.FocusedElement, pencil), "The X hands focus back to the pencil.");
            Require(card.IsOpen, "Ending editing from the keyboard leaves the card open.");
        }
        finally
        {
            card.IsOpen = false;
            card.StaysOpen = false;
            window.Close();
        }
        Console.WriteLine("PASS: a customizable card's drawer, Reset and X all work from the keyboard.");
    }

    private static Border Build(ContextMenu card, CardEdit edit)
    {
        card.Items.Clear();
        var tiles = new[]
        {
            new CardTile("", "Copy", "Ctrl+C", () => { }) { Id = "copy" },
            new CardTile("", "Paste", "Ctrl+V", () => { }) { Id = "paste" },
            new CardTile("", "Delete", "Del", () => { }) { Id = "delete" },
            new CardTile("", "Find", "Ctrl+F", () => { }) { Id = "find" },
            new CardTile("", "Spare", null, () => { }) { Id = "spare" },
        };
        var strip = CardBuilder.TileGroups(card, "Test card", new[] { tiles.Take(3), tiles.Skip(3) }, edit);
        card.Items.Add(CardBuilder.Slot(strip, new Thickness(0)));
        return strip;
    }

    // Kept out of the caller's frame, so nothing on the stack holds the first build alive.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference BuildAway(ContextMenu card, CardEdit edit) => new(Build(card, edit));

    private static int[] HandlerCounts(ContextMenu card)
    {
        var store = typeof(UIElement).GetProperty("EventHandlersStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(card);
        if (store is null) return [0, 0, 0, 0, 0, 0];
        var get = store.GetType().GetMethod("GetRoutedEventHandlers", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
        return new RoutedEvent[]
        {
            ContextMenu.ClosedEvent, UIElement.PreviewMouseDownEvent, UIElement.PreviewMouseMoveEvent,
            UIElement.PreviewMouseUpEvent, UIElement.PreviewMouseWheelEvent, UIElement.PreviewKeyDownEvent,
        }.Select(e => (get.Invoke(store, new object[] { e }) as RoutedEventHandlerInfo[])?.Length ?? 0).ToArray();
    }

    // A key press the way the keyboard delivers it to the focused element: preview first, then the key itself.
    private static void Press(Key key)
    {
        var target = (UIElement)Keyboard.FocusedElement;
        var source = PresentationSource.FromVisual(target) ?? throw new InvalidOperationException("The focused element is on screen.");
        var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(preview);
        if (!preview.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent });
        Pump(60);
    }

    private static string? Focused() => Keyboard.FocusedElement is DependencyObject d ? AutomationProperties.GetName(d) : null;

    // Where the keyboard is: the drawer spot under the cursor, else the focused element.
    private static string? Spot(ContextMenu card) => CardBuilder.KeyboardCursor(card) is { } el ? AutomationProperties.GetName(Face(el)) : Focused();

    private static Button Face(UIElement el) => Descendants(el).OfType<Button>().First();

    // Open, editing, and holding keyboard focus, which is what keeps WPF's menu code from shutting it.
    private static bool Open(ContextMenu card) => card.IsOpen && CardBuilder.IsEditing(card) && card.IsKeyboardFocusWithin;

    // On the card itself, as opposed to the drawer, which is a popup of its own.
    private static bool OnCard(ContextMenu card) => Keyboard.FocusedElement is Visual v && card.IsAncestorOf(v);

    private static Visual Top(Visual v)
    {
        while (VisualTreeHelper.GetParent(v) is Visual up) v = up;
        return v;
    }

    private static RenderTargetBitmap Render(Visual top)
    {
        var size = top is UIElement u ? u.RenderSize : new Size(400, 400);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(size.Width)), Math.Max(1, (int)Math.Ceiling(size.Height)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(top);
        return bitmap;
    }

    // The surface the element sits on, saved as a PNG for a look by eye.
    private static string Shoot(FrameworkElement el, string name)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(Top(el))));
        string path = Path.Combine(Path.GetTempPath(), "ks-cardedit-" + name + ".png");
        using (var file = File.Create(path)) encoder.Save(file);
        Console.WriteLine("SHOT: " + path);
        return path;
    }

    private static Color Pixel(FrameworkElement el, Point inside)
    {
        var top = Top(el);
        var p = el.TransformToAncestor(top).Transform(inside);
        var bitmap = Render(top);
        var px = new byte[4];
        bitmap.CopyPixels(new Int32Rect((int)p.X, (int)p.Y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    // Accent pixels down the element's left edge, a pixel either side, over the part of it the surface shows.
    private static (int Hits, string Where) LeftEdge(FrameworkElement el, Color color)
    {
        var top = Top(el);
        var r = el.TransformToAncestor(top).TransformBounds(new Rect(el.RenderSize));
        var bitmap = Render(top);
        int x0 = Math.Max(0, (int)Math.Round(r.Left) - 1), x1 = Math.Min(bitmap.PixelWidth - 1, (int)Math.Round(r.Left) + 1);
        int y0 = Math.Max(0, (int)r.Top + 6), y1 = Math.Min(bitmap.PixelHeight - 1, (int)r.Bottom - 6);
        int hits = 0;
        var px = new byte[4];
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
                if (Near(Color.FromArgb(px[3], px[2], px[1], px[0]), color)) { hits++; break; }
            }
        return (hits, $"x {x0}-{x1}, y {y0}-{y1} of {r}");
    }

    private static bool Near(Color a, Color b) =>
        Math.Abs(a.R - b.R) <= 10 && Math.Abs(a.G - b.G) <= 10 && Math.Abs(a.B - b.B) <= 10;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        int n = root is Visual ? VisualTreeHelper.GetChildrenCount(root) : 0;
        if (n == 0) foreach (var c in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>()) foreach (var d in Descendants(c)) yield return d;
        else for (int i = 0; i < n; i++) foreach (var d in Descendants(VisualTreeHelper.GetChild(root, i))) yield return d;
    }

    private static void Pump(int ms = 200)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
