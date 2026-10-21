using System.Text;

namespace KillerScan.Terminal
{
    /// <summary>
    /// What a paste sends to the shell. Clipboard text is data: line breaks become Enter, tab is
    /// kept, and every other control character is dropped, including ESC and the 8-bit C1 range.
    /// With bracketed paste on, the result is wrapped once in ESC[200~ ... ESC[201~, and because
    /// ESC never survives, text on the clipboard cannot end the frame early and run the rest as
    /// typed input. The visible characters of such a sequence are kept, so nothing pasted
    /// silently disappears.
    /// </summary>
    internal static class TerminalPaste
    {
        private const string Open = "\x1b[200~", Close = "\x1b[201~";

        internal static string Frame(string clipboard, bool bracketed)
        {
            if (string.IsNullOrEmpty(clipboard)) return string.Empty;
            string text = clipboard.Replace("\r\n", "\r").Replace('\n', '\r');
            var clean = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                bool control = c < 0x20 || c == 0x7f || (c >= 0x80 && c <= 0x9f);
                if (!control || c == '\r' || c == '\t') clean.Append(c);
            }
            if (clean.Length == 0) return string.Empty;
            return bracketed ? Open + clean + Close : clean.ToString();
        }
    }
}
