using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using KillerScan.Engine;
using KillerScan.Features.Cli;
using KillerScan.Terminal;

internal static class TerminalSafetyTests
{
    internal static void Run()
    {
        string hostile = "node\u001b[2J\u001b[H\u001b]2;spoof\a\u001b]133;KillerScan;SpeedTest\a\r\ninjected\t\u009b31m";
        var device = new NetworkDevice { IpAddress = "192.0.2.1", Hostname = hostile, MacAddress = hostile,
            Vendor = hostile, DeviceType = hostile, OpenPorts = new List<int> { 22 } };
        var devices = new List<NetworkDevice> { device };
        foreach (bool ansi in new[] { false, true })
        {
            string table = CliRunner.Table(devices, true, ansi);
            string plain = Regex.Replace(table, "\u001b\\[(?:0|9[0-7]|1;37)m", "");
            Require(!plain.Contains('\u001b') && !plain.Contains('\u009b') && !plain.Contains('\a') && !plain.Contains('\t'),
                "Untrusted controls cannot escape CLI table cells, including plain redirected output.");
            Require(plain.Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length == 3,
                "A hostile device cannot add CLI table rows.");
            Require(plain.Contains("\\x1B") && plain.Contains("\\x0A") && plain.Contains("\\x9B"), "Controls remain visible as escaped evidence.");
            CheckSideEffects(table);
        }
        ForgedShellMarks();
        var presentation = new TerminalScanPresentation(_ => "Column", () => 512);
        string embedded = presentation.Result(devices);
        string visible = Regex.Replace(embedded, "\u001b\\[(?:0|3[0-7]|1;37|2K)m?", "");
        Require(!visible.Contains('\u001b') && !visible.Contains('\u009b') && !visible.Contains('\a') && !visible.Contains('\t'), "Embedded results contain only application-owned ANSI sequences.");
        Require(visible.Contains("\\x1B") && visible.Contains("\\x0A") && visible.Contains("\\x9B"), "Embedded results retain escaped control evidence.");
        CheckSideEffects(embedded);
        string json = CliRunner.Render("json", devices, "fixture", "dark", false);
        using var document = JsonDocument.Parse(json);
        Require(document.RootElement[0].GetProperty("Hostname").GetString() == hostile && device.Hostname == hostile,
            "Structured JSON and the underlying device preserve the original network text.");
        Console.WriteLine("PASS: terminal presentation escapes controls while raw JSON preserves device text.");

        void CheckSideEffects(string output)
        {
            var buffer = new TerminalBuffer(1000, 10);
            buffer.OscDispatch(2, "Original title");
            int actions = 0;
            buffer.SpeedTestRequested += () => actions++;
            var parser = new VtParser(buffer);
            byte[] bytes = Encoding.UTF8.GetBytes(output);
            parser.Feed(bytes, bytes.Length);
            Require(buffer.Title == "Original title" && actions == 0, "Actual terminal parsing cannot execute a network-supplied title or application OSC command.");
        }
    }

    /// <summary>A remote host or banner that prints the private shell marks gets nothing; only the
    /// session's own token makes the prompt ready or starts a speed test.</summary>
    private static void ForgedShellMarks()
    {
        var buffer = new TerminalBuffer(80, 5);
        var other = new TerminalBuffer(80, 5);
        int prompts = 0, speedTests = 0;
        buffer.PromptReady += () => prompts++;
        buffer.SpeedTestRequested += () => speedTests++;
        var parser = new VtParser(buffer);
        void Feed(string text) { byte[] bytes = Encoding.UTF8.GetBytes(text); parser.Feed(bytes, bytes.Length); }
        Feed("\u001b]133;B\a\u001b]133;KillerScan;SpeedTest\a");
        Feed("\u001b]133;B;" + other.PromptToken + "\a\u001b]133;KillerScan;SpeedTest;" + other.PromptToken + "\a");
        Feed("\u001b]133;B;\a\u001b]133;KillerScan;SpeedTest;" + buffer.PromptToken + "x\a");
        Require(prompts == 0 && speedTests == 0, "Forged or foreign shell marks must not reach the prompt or speed test.");
        Require(buffer.PromptToken.Length == 32 && buffer.PromptToken != other.PromptToken, "Each terminal gets its own random token.");
        Feed("\u001b]133;KillerScan;SpeedTest;" + buffer.PromptToken + "\a\u001b]133;B;" + buffer.PromptToken + "\a");
        Require(prompts == 1 && speedTests == 1, "The session's own marks still work.");
        Console.WriteLine("PASS: forged shell marks cannot make the prompt ready or start a speed test.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
