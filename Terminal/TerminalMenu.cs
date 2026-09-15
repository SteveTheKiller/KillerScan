using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KillerScan.Terminal
{
    /// <summary>
    /// The terminal's right-click menu. Everything on it is something a terminal is expected to
    /// offer, and every entry has a keyboard equivalent already: the menu is for the people who
    /// reach for the mouse, not a second way to implement anything.
    /// </summary>
    internal sealed partial class TerminalControl
    {
        private MenuItem? _copyItem;

        private void BuildContextMenu()
        {
            var menu = new ContextMenu();

            _copyItem = Entry("Str_Term_Copy", "Ctrl+Shift+C", () => { CopySelection(); ClearSelection(); });
            menu.Items.Add(_copyItem);
            menu.Items.Add(Entry("Str_Term_Paste", "Ctrl+Shift+V", Paste));
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry("Str_Term_SelectAll", "Ctrl+Shift+A", SelectAll));
            // No chord: every Ctrl+Shift letter that would suit it is either taken or worth
            // leaving to the shell, and this is a menu-shaped action rather than a typing one.
            menu.Items.Add(Entry("Str_Term_CopyAll", null, CopyAll));
            menu.Items.Add(new Separator());
            menu.Items.Add(Entry("Str_Term_Clear", null, ClearScreen));
            menu.Items.Add(Entry("Str_Term_Font", null, ChooseFont));

            // Copy is only meaningful with a selection, and Paste only with text on the clipboard,
            // so both are settled as the menu opens rather than left permanently enabled.
            menu.Opened += (_, _) =>
            {
                _copyItem?.IsEnabled = _hasSelection;
            };
            ContextMenu = menu;
        }

        private static MenuItem Entry(string key, string? gesture, Action action)
        {
            var item = new MenuItem { InputGestureText = gesture ?? string.Empty };
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
