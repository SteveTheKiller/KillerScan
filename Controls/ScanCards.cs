using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using KillerFamily.Controls;
using KillerScan.Services;

namespace KillerScan.Controls;

/// <summary>
/// KillerScan's family tile cards, ported from KillerShot's ShotCards (the KillerPDF 1.9 card rules, KillerUI CardBuilder.TileGroups):
/// 1. Three tiles across, every tile an MDL2 glyph over a short caption that wraps inside the tile.
/// 2. Tiles run on in order, related ones side by side, so every row is full but the last (Steve 2026-11-19:
///    no blank cells in the middle of a card). A card's tile count is a multiple of three where it can be.
/// 3. Most used first, destructive last; Delete on a card for one thing sits in the header.
/// 4. A tile that only sometimes applies stays in its place, dimmed, so the rows never shift.
/// 5. No text rows: everything is a tile, or a flyout tile for a list such as Move to.
/// 6. A big card is customizable (Editable, the kit's CardEdit); a small card stays fixed (Grid).
/// Both lay the tiles out with the kit's CardBuilder.TileGroups.
/// </summary>
internal static class ScanCards
{
    /// <summary>The card width for three tiles across (the menu adds its 22px halo on each side).</summary>
    public const double Width = 276;

    /// <summary>Tiles across a card row.</summary>
    public const int Columns = 3;

    /// <summary>The tag on a card header's Delete button, for the self-tests.</summary>
    public const string HeaderDeleteTag = "carddelete";

    private static readonly DependencyProperty DeleteButtonProperty =
        DependencyProperty.RegisterAttached("DeleteButton", typeof(Button), typeof(ScanCards));

    private static readonly DependencyProperty WatchedProperty =
        DependencyProperty.RegisterAttached("Watched", typeof(bool), typeof(ScanCards), new PropertyMetadata(false));

    private static string Loc(string key) => LocaleManager.Loc(key);

    // Flyout tiles whose rows are only built when the tile is clicked (Recent loads thumbnails).
    private static readonly ConditionalWeakTable<CardTile, Action<ItemsControl>> LazyFills = new();

    /// <summary>A new card at the three-across width.</summary>
    public static ContextMenu Menu() => CardBuilder.Menu(Width);

    /// <summary>Gives a menu made elsewhere (XAML, or a control's own ContextMenu) the card look and width.</summary>
    public static ContextMenu Prepare(ContextMenu menu)
    {
        menu.Items.Clear();
        menu.SetResourceReference(FrameworkElement.StyleProperty, "CardMenu");
        menu.MinWidth = menu.MaxWidth = Width + 44;
        return menu;
    }

    /// <summary>
    /// The card's header: its title, an optional dim line under it, and, on a card for one thing, that
    /// thing's Delete as an icon button at the right end (the KillerPDF mark card's WithHeaderDelete).
    /// </summary>
    public static MenuItem Header(ContextMenu card, string title, string? meta = null,
        string? deleteName = null, string? deleteKeys = null, Action? delete = null, bool deleteEnabled = true)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var head = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        head.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        text.Children.Add(head);
        if (!string.IsNullOrEmpty(meta))
        {
            var sub = new TextBlock { Text = meta, FontSize = 11, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "DimTextBrush");
            text.Children.Add(sub);
        }
        var dock = new DockPanel { LastChildFill = true };
        if (delete != null && deleteName != null)
        {
            var button = new Button
            {
                Content = "\uE74D", Tag = HeaderDeleteTag, IsEnabled = deleteEnabled,
                ToolTip = CardBuilder.Tip(deleteName, deleteKeys),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                // The toolbar style carries no icon font, so the glyph names the font the tiles draw with.
                FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 14,
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "ToolbarButton");
            AutomationProperties.SetName(button, deleteName);
            if (!string.IsNullOrEmpty(deleteKeys)) AutomationProperties.SetAcceleratorKey(button, deleteKeys);
            ToolTipService.SetShowOnDisabled(button, true);
            var run = delete;
            button.Click += (_, _) => { card.IsOpen = false; run(); };
            DockPanel.SetDock(button, Dock.Right);
            dock.Children.Add(button);
            card.SetValue(DeleteButtonProperty, button);
            if (!(bool)card.GetValue(WatchedProperty))
            {
                // A card opened from the keyboard starts on its first tile, never on Delete: Enter there would delete.
                card.SetValue(WatchedProperty, true);
                card.Opened += (_, _) => card.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (ReferenceEquals(Keyboard.FocusedElement, card.GetValue(DeleteButtonProperty))) FirstTile(card)?.Focus();
                }));
                // A card opened by the Apps key can keep focus on the card itself, and the first arrow would then
                // step onto Delete, the first control in it. That first step goes to the first tile instead.
                card.PreviewKeyDown += (_, e) =>
                {
                    if (!ReferenceEquals(Keyboard.FocusedElement, card)) return;
                    if (e.Key is not (Key.Down or Key.Up or Key.Left or Key.Right or Key.Tab or Key.Home)) return;
                    if (FirstTile(card) is { } first) { first.Focus(); e.Handled = true; }
                };
            }
        }
        dock.Children.Add(text);
        var slot = CardBuilder.Slot(dock, new Thickness(4, 2, 4, 6));
        AutomationProperties.SetName(slot, title);
        return slot;
    }

    /// <summary>
    /// A fixed tile grid for a small card: the tiles fill rows of three in order. Empty groups are skipped.
    /// Put it on the card after the header; it is the card's only other item.
    /// </summary>
    public static MenuItem Grid(ContextMenu card, params IEnumerable<CardTile>[] groups) => Build(card, null, groups);

    /// <summary>
    /// A customizable tile grid for a big card (the kit's CardEdit): a pencil in the corner opens edit mode, and
    /// tiles move between the card and its Hidden drawer. Locked tiles always show; spares start in the drawer
    /// (both named by tile Id, or by glyph for a tile without one), so the default card is full rows. The layout is kept as the "Card.{name}.Hidden" setting.
    /// </summary>
    public static MenuItem Editable(ContextMenu card, string name, string[] locked, string[] spares, params IEnumerable<CardTile>[] groups)
    {
        var edit = new CardEdit(() => App.GetSetting($"Card.{name}.Hidden") is { Length: > 0 } saved ? saved : null,
            value => App.SetSetting($"Card.{name}.Hidden", value ?? ""))
        {
            Locked = t => locked.Contains(t.Id ?? t.Glyph),
            DefaultHidden = t => spares.Contains(t.Id ?? t.Glyph),
            EditTip = Loc("Str_Card_Customize"), DoneTip = Loc("Str_Card_EditDone"), ResetTip = Loc("Str_Card_EditReset"),
            ResetLabel = Loc("Str_Card_EditResetShort"), HiddenLabel = Loc("Str_Card_Hidden"),
            HiddenHint = Loc("Str_Card_HiddenHint"), LockedTip = Loc("Str_Card_AlwaysShown"),
            MovedText = Loc("Str_Card_Moved"),
        };
        return Build(card, edit, groups);
    }

    // The card's tiles by caption (unique on a card, unlike a mirrored pair's glyph): a customizable card rebuilds
    // its tile buttons whenever a tile moves, so a button's tile is found from its accessible name.
    private static readonly DependencyProperty TilesProperty =
        DependencyProperty.RegisterAttached("Tiles", typeof(Dictionary<string, CardTile>), typeof(ScanCards));

    // The rows a lazy flyout holds once it has been filled.
    private static readonly ConditionalWeakTable<CardTile, IList<object>> Filled = new();

    private static MenuItem Build(ContextMenu card, CardEdit? edit, IEnumerable<CardTile>[] groups)
    {
        // The tiles run on as one list, so every row is full but the last: a group never leaves blank cells
        // in the middle of a card, and a tile moved to the drawer closes up the rest.
        var tiles = groups.SelectMany(g => g).ToList();
        var byName = new Dictionary<string, CardTile>();
        foreach (var tile in tiles) { string key = Trimmed(tile.Caption); if (!byName.ContainsKey(key)) byName[key] = tile; }
        card.SetValue(TilesProperty, byName);
        if (!(bool)card.GetValue(FlyoutWatchedProperty))
        {
            card.SetValue(FlyoutWatchedProperty, true);
            card.Opened += (_, _) => NameTiles(card);
            // Runs after the kit's own Click on the tile, which has opened the flyout with its placeholder row.
            card.AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler((_, e) =>
            {
                if (e.OriginalSource is not Button button || TileFor(button) is not { } tile) return;
                if (!LazyFills.TryGetValue(tile, out var fill) || tile.Flyout is not { } rows) return;
                if ((rows.FirstOrDefault() as FrameworkElement)?.Parent is not ItemsControl host) return;
                fill(host);
                Filled.Remove(tile); Filled.Add(tile, host.Items.Cast<object>().ToList());
                // The kit's flyout picked its focus row when it opened, before the fill replaced the rows, so
                // a keyboard open would have left focus on the card. The first real row takes it here.
                if (InputManager.Current.MostRecentInputDevice is KeyboardDevice)
                    host.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                        host.Items.OfType<MenuItem>().FirstOrDefault(i => i.IsEnabled)?.Focus()));
            }));
        }
        var strip = CardBuilder.TileGroups(card, Loc("Str_Card_Quick"), tiles.Count > 0 ? [tiles] : [], edit, Columns);
        var slot = CardBuilder.Slot(strip, new Thickness(0));
        AutomationProperties.SetName(slot, Loc("Str_Card_Quick"));
        // The grid is not on the card yet, so its tiles are named from this build's own list.
        foreach (var button in Descendants(strip).OfType<Button>())
            if (byName.TryGetValue(Trimmed(AutomationProperties.GetName(button)), out var named) && named.Id is { } id) button.Uid = id;
        return slot;
    }

    // A tile with an Id carries it as its button's Uid, the command name the self-tests read.
    private static void NameTiles(ItemsControl card)
    {
        foreach (var button in Buttons(card))
            if (TileFor(button) is { Id: { } id }) button.Uid = id;
    }

    private static string Trimmed(string caption) => caption.TrimEnd('.', '…').TrimEnd();

    private static readonly DependencyProperty FlyoutWatchedProperty =
        DependencyProperty.RegisterAttached("FlyoutWatched", typeof(bool), typeof(ScanCards), new PropertyMetadata(false));

    private static CardTile? TileFor(Button button)
    {
        if (AutomationProperties.GetName(button) is not { Length: > 0 } name) return null;
        DependencyObject? at = button;
        while (at != null && at is not ContextMenu) at = LogicalTreeHelper.GetParent(at) ?? VisualTreeHelper.GetParent(at);
        return at?.GetValue(TilesProperty) is Dictionary<string, CardTile> tiles && tiles.TryGetValue(Trimmed(name), out var tile) ? tile : null;
    }

    /// <summary>The rows a flyout tile opens, or null for a tile that acts.</summary>
    public static IList<object>? GetFlyoutRows(Button tile) =>
        TileFor(tile) is { Flyout: { } rows } t ? (Filled.TryGetValue(t, out var filled) ? filled : rows) : null;

    /// <summary>A tile that opens a list beside itself. The rows are taken out of whatever held them.</summary>
    public static CardTile Flyout(string glyph, string caption, string? keys, IEnumerable<object> rows, bool enabled = true)
    {
        var list = rows.ToList();
        return new CardTile(glyph, caption, keys, null, false, enabled && list.Count > 0) { Flyout = list };
    }

    /// <summary>A flyout tile whose rows are built when it is clicked, not when the card opens; until then
    /// it holds one dimmed placeholder row.</summary>
    public static CardTile LazyFlyout(string glyph, string caption, string placeholder, Action<ItemsControl> fill, bool enabled = true, string? id = null)
    {
        var tile = new CardTile(glyph, caption, null, null, false, enabled)
            { Id = id, Flyout = new List<object> { new MenuItem { Header = placeholder, IsEnabled = false } } };
        LazyFills.Add(tile, fill);
        return tile;
    }

    /// <summary>The rows of a MenuItem built as a submenu, ready for a flyout tile.</summary>
    public static IEnumerable<object> RowsOf(ItemsControl source)
    {
        var rows = source.Items.Cast<object>().ToList();
        source.Items.Clear();
        return rows;
    }

    /// <summary>Every tile and header button on a card, in order.</summary>
    public static IEnumerable<Button> Buttons(ItemsControl card) => card.Items.OfType<MenuItem>().SelectMany(ButtonsIn);

    /// <summary>The tiles or header buttons of one card item, in order.</summary>
    public static IEnumerable<Button> ButtonsIn(MenuItem item) => Descendants(item.Header as DependencyObject).OfType<Button>();

    /// <summary>The first tile that can be used.</summary>
    public static Button? FirstTile(ItemsControl card) =>
        Buttons(card).FirstOrDefault(b => !Equals(b.Tag, HeaderDeleteTag) && b.IsEnabled);

    /// <summary>Whether any tile or header button on the card can be used.</summary>
    public static bool AnyEnabled(ItemsControl card) => Buttons(card).Any(b => b.IsEnabled);

    // The card's own controls: a customizable card's hidden measuring copy of its grid, its drawer (a popup)
    // and its edit pencil (a button with no caption) are not tiles.
    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root == null || root is Popup || root is UIElement { Visibility: not Visibility.Visible }) yield break;
        if (root is Button { Tag: null }) yield break;
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Descendants(child)) yield return d;
    }
}
