using System;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using KillerScan.Terminal;

internal static class TerminalAccessibilityTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var terminal = new TerminalControl();
                terminal.ShowScript("known transcript\r\nready");
                var peer = UIElementAutomationPeer.CreatePeerForElement(terminal);
                Require(peer != null, "Terminal must expose an automation peer.");
                var provider = peer!.GetPattern(PatternInterface.Text) as ITextProvider;
                Require(provider != null, "Terminal must expose TextPattern.");
                Require(provider!.DocumentRange.GetText(-1).Contains("known transcript"), "Document reads actual output.");
                Require(provider.GetSelection().Length == 1 && provider.GetSelection()[0].GetText(-1) == "", "No selection exposes the insertion caret.");
                var text = provider.DocumentRange.FindText("transcript", false, false);
                Require(text != null, "Document text can be searched.");
                text!.Select();
                Require(terminal.HasSelection && provider.GetSelection()[0].GetText(-1) == "transcript", "UIA selection updates the actual terminal selection.");
                terminal.ClearSelection();
                Require(provider.GetSelection()[0].GetText(-1) == "", "Clearing selection restores the caret range.");
                terminal.ShowScript("!");
                Require(provider.DocumentRange.GetText(-1).Contains("ready!"), "Document follows new output.");
                var caret = provider.GetSelection()[0];
                Require(caret.Move(TextUnit.Character, -1) == -1 && caret.GetText(-1) == "", "Caret movement stays degenerate.");
                caret.ExpandToEnclosingUnit(TextUnit.Character);
                Require(caret.GetText(-1) == "!", "Caret character range follows the insertion point.");
                var copy = text.Clone();
                Require(copy.Compare(text), "Clones retain both endpoints.");
                copy.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, -1);
                Require(copy.GetText(-1) == "transcrip", "Endpoint movement updates only the requested endpoint.");
                Require(provider.DocumentRange.GetText(5) == "known", "Bounded text reads honor their maximum length.");
                Require(provider.GetVisibleRanges().Length == 1, "Viewport exposes a text range.");
                using (var offsets = new TerminalControl())
                {
                    offsets.ShowScript("alpha  \r\n\r\n中beta\r\n   \r\nlast line");
                    var buffer = offsets.Buffer;
                    int Reference(int row, int col)
                    {
                        int total = 0;
                        for (int r = 0; r <= row && r < buffer.TotalLines; r++)
                        {
                            var cells = buffer.LineAt(r);
                            string line = string.Concat(System.Linq.Enumerable.Select(cells, c => c.Text)).TrimEnd();
                            if (r == row)
                                return total + Math.Min(string.Concat(System.Linq.Enumerable.Select(System.Linq.Enumerable.Take(cells, Math.Max(0, col)), c => c.Text)).Length, line.Length);
                            total += line.Length + Environment.NewLine.Length;
                        }
                        return total;
                    }
                    for (int row = -1; row <= buffer.TotalLines; row++)
                        for (int col = 0; col <= buffer.Cols + 1; col++)
                            Require(offsets.TextOffset(row, col) == Reference(row, col), "Cached text offsets match the full rebuild at " + row + "," + col);
                    int end = offsets.TextOffset(buffer.TotalLines, 0);
                    for (int at = 0; at <= end; at++)
                    {
                        var cell = offsets.CellFromOffset(at);
                        Require(offsets.TextOffset(cell.Row, cell.Col) <= at, "An offset maps to a cell at or before it: " + at);
                    }
                }
                terminal.ShowScript("\r\n\u4e2d e\u0301 \U0001f469\u200d\U0001f4bb");
                Require(terminal.GetText().Contains("\u4e2d e\u0301 \U0001f469\u200d\U0001f4bb"), "Transcript preserves complete Unicode clusters without continuation spaces.");
                provider.DocumentRange.FindText("\u4e2d e\u0301 \U0001f469\u200d\U0001f4bb", false, false)!.Select();
                Require(terminal.GetSelectionText() == "\u4e2d e\u0301 \U0001f469\u200d\U0001f4bb", "Copy text and UIA selection use the same cluster mapping.");
                using var mouse = new TerminalControl();
                mouse.ShowScript("\u4e2dX");
                mouse.SelectCells(0, 1, 0, 2);
                var mousePeer = UIElementAutomationPeer.CreatePeerForElement(mouse)!;
                var mouseProvider = (ITextProvider)mousePeer.GetPattern(PatternInterface.Text);
                Require(mouse.GetSelectionText() == "\u4e2d" && mouseProvider.GetSelection()[0].GetText(-1) == "\u4e2d",
                    "Mouse-origin continuation selection agrees with copied and accessible text.");
                mouse.SelectCells(0, 2, 0, 1);
                Require(mouseProvider.GetSelection()[0].GetText(-1) == "\u4e2d", "Reverse mouse selection uses the same cluster boundaries.");
                string? renderRoot = Environment.GetEnvironmentVariable("KILLERSCAN_RENDER_ROOT");
                if (!string.IsNullOrEmpty(renderRoot))
                {
                    using var preview = new TerminalControl();
                    preview.SetFontSize(20);
                    preview.Measure(new System.Windows.Size(1100, 260));
                    preview.Arrange(new System.Windows.Rect(0, 0, 1100, 260));
                    preview.ShowScript("ASCII  | X\r\nCafe\u0301   | X\r\n\u4e2d\u6587   | X\r\n\U0001f680     | X\r\n\u2764\ufe0f     | X\r\n\U0001f469\u200d\U0001f4bb     | X");
                    preview.UpdateLayout();
                    var image = new System.Windows.Media.Imaging.RenderTargetBitmap(1100, 260, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    image.Render(preview);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    System.IO.Directory.CreateDirectory(renderRoot);
                    using var stream = System.IO.File.Create(System.IO.Path.Combine(renderRoot, "terminal-unicode.png"));
                    encoder.Save(stream);
                }
                Console.WriteLine("PASS: terminal TextPattern reads live output, searches and follows selection/caret.");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure != null) throw failure;
    }
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
