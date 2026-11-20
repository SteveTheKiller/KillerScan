using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerScan.Controls;

namespace KillerScan.Terminal
{
    /// <summary>
    /// The terminal's right-click menu. Everything on it is something a terminal is expected to
    /// offer, and every entry has a keyboard equivalent already: the menu is for the people who
    /// reach for the mouse, not a second way to implement anything.
    /// </summary>
    internal sealed partial class TerminalControl
    {
        /// <summary>
        /// The terminal card: the five clipboard and screen verbs as quick buttons, with Font as
        /// the one list row. Rebuilt as it opens, so the captions follow the current language and
        /// Copy is settled from whether anything is selected.
        /// </summary>
        private void BuildContextMenu()
        {
            ContextMenu = KillerScan.Controls.ScanCards.Menu();
            ContextMenuOpening += (_, _) => FillContextMenu();
        }

        /// <summary>Fills the card for the pointer and for the Menu key, which opens it directly.</summary>
        private void FillContextMenu()
        {
            string L(string key) => TryFindResource(key) as string ?? key;
            var card = KillerScan.Controls.ScanCards.Prepare(ContextMenu);
            card.Items.Add(KillerScan.Controls.ScanCards.Header(card, L("Str_Workspace_Terminal")));
            KillerFamily.Controls.CardTile Tile(string id, string glyph, string key, string? keys, Action run, bool enabled = true, string? shown = null) =>
                new(glyph, L(key), keys, run, false, enabled) { Id = id, Shown = shown == null ? null : L(shown) };
            card.Items.Add(KillerScan.Controls.ScanCards.Grid(card,
                new[]
                {
                    Tile("Copy", "", "Str_Term_Copy", "Ctrl+Shift+C", () => { CopySelection(); ClearSelection(); }, _hasSelection, "Str_Ctx_Copy"),
                    Tile("Paste", "", "Str_Term_Paste", "Ctrl+Shift+V", Paste, true, "Str_Ctx_Paste"),
                    Tile("SelectAll", "", "Str_Term_SelectAll", "Ctrl+Shift+A", SelectAll, true, "Str_Ctx_SelectAll"),
                },
                new[]
                {
                    Tile("CopyAll", "", "Str_Term_CopyAll", "Ctrl+Shift+Y", CopyAll, true, "Str_Card_CopyAll"),
                    Tile("Clear", "", "Str_Term_Clear", "Ctrl+Shift+L", ClearScreen, true, "Str_Card_Clear"),
                    Tile("Font", "", "Str_Term_Font", null, ChooseFont),
                }));
        }

        /// <summary>The whole session on the clipboard, scrollback included.</summary>
        private void CopyAll()
        {
            try { Clipboard.SetText(GetText()); }
            catch (System.Runtime.InteropServices.COMException) { }
        }

        /// <summary>
        /// Moves visible output into session history and clears the viewport without
        /// sending anything to the program on the other end: a full-screen program would redraw
        /// over it anyway, and a shell mid-command should not receive stray input.
        /// </summary>
        private void ClearScreen()
        {
            _buf.ClearAll();
            _scroll = 0;
            ClearSelection();
            InvalidateVisual();
        }
    }
}
