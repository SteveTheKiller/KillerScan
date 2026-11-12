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
            ContextMenu = KillerFamily.Controls.CardBuilder.Menu();
            ContextMenuOpening += (_, _) => FillContextMenu();
        }

        /// <summary>Fills the card for the pointer and for the Menu key, which opens it directly.</summary>
        private void FillContextMenu()
        {
            var menu = ContextMenu;
            {
                string L(string key) => TryFindResource(key) as string ?? key;
                menu.Items.Clear();
                menu.Items.Add(KillerFamily.Controls.CardBuilder.Header(L("Str_Workspace_Terminal")));
                menu.Items.Add(KillerFamily.Controls.CardBuilder.QuickStrip(L("Str_Card_Actions"), new[]
                {
                    new KillerFamily.Controls.CardAction("", L("Str_Ctx_Copy"), L("Str_Term_Copy"), "Ctrl+Shift+C",
                        () => { CopySelection(); ClearSelection(); }, _hasSelection),
                    new KillerFamily.Controls.CardAction("", L("Str_Ctx_Paste"), L("Str_Term_Paste"), "Ctrl+Shift+V", Paste),
                    new KillerFamily.Controls.CardAction("", L("Str_Ctx_SelectAll"), L("Str_Term_SelectAll"), "Ctrl+Shift+A", SelectAll),
                    new KillerFamily.Controls.CardAction("", L("Str_Card_CopyAll"), L("Str_Term_CopyAll"), "Ctrl+Shift+Y", CopyAll),
                    new KillerFamily.Controls.CardAction("", L("Str_Card_Clear"), L("Str_Term_Clear"), "Ctrl+Shift+L", ClearScreen),
                }));
                menu.Items.Add(Entry("Str_Term_Font", null, 0xE8D2, ChooseFont));
            }
        }

        private static MenuItem Entry(string key, string? gesture, int glyph, Action action)
        {
            var item = new MenuItem { InputGestureText = gesture ?? string.Empty, Icon = MenuGlyph.Create(glyph) };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, key);
            item.Click += (_, _) => action();
            return item;
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
