using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using KillerScan.Terminal;

internal static class OscParserTests
{
    internal static void Run()
    {
        foreach (string terminator in new[] { "\a", "\u001b\\" })
        foreach (string title in new[] { "fixture-title", "caf\u00e9 \u65e5\u672c \U0001f680" })
        {
            byte[] bytes = Encoding.UTF8.GetBytes("\u001b]2;" + title + terminator + "visible\u001b[31m!");
            for (int split = 0; split <= bytes.Length; split++)
            {
                var handler = new Handler();
                var parser = new VtParser(handler);
                byte[] first = bytes.Take(split).ToArray(), second = bytes.Skip(split).ToArray();
                parser.Feed(first, first.Length); parser.Feed(second, second.Length);
                Require(handler.Osc.Count == 1 && handler.Osc[0] == "2:" + title,
                    "OSC title must dispatch once with its complete UTF-8 payload at split " + split);
                Require(handler.Text.ToString() == "visible!" && handler.Csi == 1 && handler.Esc == 0,
                    "OSC termination returns to normal text and CSI without leaking payload or ST.");
            }
        }
        var buffer = new TerminalBuffer(40, 5);
        int titles = 0, prompts = 0;
        buffer.TitleChanged += _ => titles++;
        buffer.PromptReady += () => prompts++;
        var live = new VtParser(buffer);
        byte[] transcript = Encoding.UTF8.GetBytes("\u001b]2;device console\u001b\\\u001b]133;B;" + buffer.PromptToken + "\u001b\\ready");
        foreach (byte value in transcript) live.Feed(new[] { value }, 1);
        Require(buffer.Title == "device console" && titles == 1 && prompts == 1,
            "ST dispatch reaches the terminal title and prompt integration exactly once.");
        var bounded = new Handler();
        var limitParser = new VtParser(bounded);
        byte[] longTitle = Encoding.ASCII.GetBytes("\u001b]2;" + new string('x', 5000) + "\u001b\\ok");
        limitParser.Feed(longTitle, longTitle.Length);
        Require(bounded.Osc.Single().Length == 4098 && bounded.Text.ToString() == "ok", "OSC payload storage stays bounded and resumes printing.");
        Console.WriteLine("PASS: BEL and ST terminate OSC titles at every byte split.");
    }

    private sealed class Handler : IVtHandler
    {
        internal readonly List<string> Osc = new();
        internal readonly StringBuilder Text = new();
        internal int Csi, Esc;
        public void Print(int codepoint) => Text.Append(char.ConvertFromUtf32(codepoint));
        public void Execute(byte control) { }
        public void CsiDispatch(char final, int[] pars, char prefix, char intermediate) => Csi++;
        public void EscDispatch(char final, char intermediate) => Esc++;
        public void OscDispatch(int command, string data) => Osc.Add(command + ":" + data);
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
