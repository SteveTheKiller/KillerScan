using System;
using KillerScan.Terminal;

// Pasted text is data. It must not be able to close the bracketed-paste frame early or carry
// control sequences to the shell, and ordinary text and line breaks must arrive unchanged.
internal static class TerminalPasteTests
{
    private const string Open = "\x1b[200~", Close = "\x1b[201~";

    internal static void Run()
    {
        Require(TerminalPaste.Frame("dir\r\ncls\n", bracketed: true) == Open + "dir\rcls\r" + Close,
            "Line breaks become Enter, and the paste is framed once.");
        Require(TerminalPaste.Frame("echo hi\tthere", bracketed: false) == "echo hi\tthere",
            "Without bracketed mode, plain text and tabs are sent as they are.");

        string hostile = "harmless" + Close + "calc\r" + Open + "tail";
        string framed = TerminalPaste.Frame(hostile, bracketed: true);
        Require(framed.StartsWith(Open) && framed.EndsWith(Close) &&
            framed.IndexOf(Close, StringComparison.Ordinal) == framed.Length - Close.Length &&
            framed.IndexOf(Open, 1, StringComparison.Ordinal) < 0,
            "An embedded end-of-paste marker cannot close the frame early.");
        Require(framed == Open + "harmless[201~calc\r[200~tail" + Close,
            "Only the escape character is removed; the visible text is kept.");

        Require(TerminalPaste.Frame("a\u001b]0;title\u0007b\u009b31mc\u0000d\u007fe", bracketed: false) == "a]0;titleb31mcde",
            "Other control characters, 8-bit CSI included, are removed in both modes.");
        Require(TerminalPaste.Frame("naïve 日本 🙂", bracketed: true) == Open + "naïve 日本 🙂" + Close,
            "Unicode text is unchanged.");
        Require(TerminalPaste.Frame("", bracketed: true) == string.Empty, "An empty clipboard sends nothing.");
        Console.WriteLine("PASS: pasted text keeps its frame, line breaks and characters, and drops control sequences.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
