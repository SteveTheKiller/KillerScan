using System;
using System.Linq;
using System.Text;
using KillerScan.Terminal;

internal static class TerminalUnicodeTests
{
    internal static void Run()
    {
        foreach (var item in new[] {
            (Text: "abc", Width: 3), (Text: "e\u0301X", Width: 2),
            (Text: "\u4e2dX", Width: 3), (Text: "\U0001f680X", Width: 3),
            (Text: "\u2764\ufe0fX", Width: 3), (Text: "\U0001f469\u200d\U0001f4bbX", Width: 3),
            (Text: "\U0001f1fa\U0001f1f8X", Width: 3), (Text: "\U0001f44d\U0001f3fdX", Width: 3) })
        {
            var buffer = new TerminalBuffer(12, 3); var parser = new VtParser(buffer);
            byte[] bytes = Encoding.UTF8.GetBytes(item.Text);
            foreach (byte value in bytes) parser.Feed(new[] { value }, 1);
            Require(buffer.CursorCol == item.Width, "Wrong column for " + item.Text + ": " + buffer.CursorCol);
        }
        var boundary = new TerminalBuffer(4, 3); var feed = new VtParser(boundary);
        Write(feed, "abc\u4e2dX");
        Require(boundary.CursorRow == 1 && boundary.CursorCol == 3, "Wide text wraps before the last single column.");
        var combining = new TerminalBuffer(4, 3); var combiningFeed = new VtParser(combining);
        Write(combiningFeed, "abce\u0301X");
        Require(combining.CursorRow == 1 && combining.CursorCol == 1, "Combining text attaches before pending wrap.");
        Require(combining.LineAt(0)[3].Text == "e\u0301", "Mark remains in the preceding cluster.");
        var promoted = new TerminalBuffer(4, 3); var promotedFeed = new VtParser(promoted);
        Write(promotedFeed, "abc\u2764\ufe0fX");
        Require(promoted.CursorRow == 1 && promoted.CursorCol == 3 && promoted.LineAt(1)[0].Text == "\u2764\ufe0f", "Emoji promotion wraps its complete cluster.");
        var edits = new TerminalBuffer(8, 3); var editFeed = new VtParser(edits);
        Write(editFeed, "\u4e2dX\u001b[1;2HZ");
        Require(edits.LineAt(0)[0].Ch == 0 && edits.LineAt(0)[1].Ch == 'Z' && edits.LineAt(0)[2].Ch == 'X', "Overwriting a continuation clears its whole former cluster.");
        Write(editFeed, "\u001b[2J\u001b[H\u4e2dX\u001b[H\u001b[@");
        var current = edits.LineAt(edits.ScrollbackCount);
        Require(current[1].Text == "\u4e2d" && current[2].Ch == -1 && current[3].Ch == 'X', "Insertion shifts a complete wide character.");
        Write(editFeed, "\u001b[1;3H\u001b[X");
        Require(current[1].Ch == 0 && current[2].Ch == 0 && current[3].Ch == 'X', "Erase clears both halves without erasing neighbors.");
        var resize = new TerminalBuffer(8, 3); var resizeFeed = new VtParser(resize);
        Write(resizeFeed, "\U0001f469\u200d\U0001f4bb e\u0301\u4e2d");
        resize.Resize(4, 3); resize.Resize(8, 3);
        Require(resize.LineAt(0)[0].Text == "\U0001f469\u200d\U0001f4bb" && resize.LineAt(0)[3].Text == "e\u0301", "Resize preserves clusters and hidden columns.");
        var replacement = new TerminalBuffer(8, 3); replacement.ImportHistory(resize);
        Require(replacement.LineAt(0)[0].Text == resize.LineAt(0)[0].Text, "Shell replacement retains clusters.");
        var pending = new TerminalBuffer(4, 2); var pendingFeed = new VtParser(pending);
        Write(pendingFeed, "abcd"); pending.Resize(8, 2); Write(pendingFeed, "X");
        Require(pending.CursorRow == 0 && pending.CursorCol == 5 && pending.LineAt(0)[4].Ch == 'X', "Widening resolves pending wrap to the next column.");
        var repeat = new TerminalBuffer(8, 2); var repeatFeed = new VtParser(repeat);
        Write(repeatFeed, "\u4e2d\u001b[2b");
        Require(repeat.CursorCol == 6 && repeat.LineAt(0)[4].Text == "\u4e2d", "Repeat emits complete wide clusters.");
        var noWrap = new TerminalBuffer(4, 2); var noWrapFeed = new VtParser(noWrap);
        Write(noWrapFeed, "\u001b[?7labc\u2764\ufe0f");
        Require(noWrap.CursorRow == 0 && noWrap.CursorCol == 3 && noWrap.LineAt(0)[3].Text == "\u2764\ufe0f", "Disabled wrapping preserves an edge emoji cluster safely.");
        Console.WriteLine("PASS: UTF-8 parser preserves terminal widths for accents, CJK, supplementary and emoji clusters.");

        // Colon sub-parameters from bat, delta or neovim are consumed whole instead of leaking text.
        var colon = new TerminalBuffer(16, 2); var colonFeed = new VtParser(colon);
        Write(colonFeed, "\u001b[38:2::255:0:0mA\u001b[4:3mB\u001b[0mC");
        Require(colon.CursorCol == 3 && colon.LineAt(0)[0].Ch == 'A' && colon.LineAt(0)[1].Ch == 'B' && colon.LineAt(0)[2].Ch == 'C',
            "Colon SGR sequences print nothing of their own: col " + colon.CursorCol);
        Console.WriteLine("PASS: colon sub-parameter sequences are consumed without printing their tails.");

        // After narrowing, a row keeps its wider cells for horizontal scroll; erasing must reach them.
        var narrowed = new TerminalBuffer(8, 2); var narrowedFeed = new VtParser(narrowed);
        Write(narrowedFeed, "ABCDEFGH");
        narrowed.Resize(4, 2);
        Write(narrowedFeed, "\u001b[H\u001b[K");
        var row = narrowed.LineAt(narrowed.ScrollbackCount);
        Require(row.Length == 8 && row.All(cell => cell.Ch == 0), "Erase in line clears the cells beyond a narrowed pane.");
        var cleared = new TerminalBuffer(8, 2); var clearedFeed = new VtParser(cleared);
        Write(clearedFeed, "ABCDEFGH");
        cleared.Resize(4, 2);
        Write(clearedFeed, "\u001b[2J");
        Require(Enumerable.Range(cleared.ScrollbackCount, 2).All(r => cleared.LineAt(r).All(cell => cell.Ch == 0)),
            "Clearing the screen leaves no stale text beyond the visible width.");
        Console.WriteLine("PASS: erasing after a narrowing resize clears hidden columns too.");
    }
    private static void Write(VtParser parser, string text)
    { byte[] bytes = Encoding.UTF8.GetBytes(text); parser.Feed(bytes, bytes.Length); }
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
