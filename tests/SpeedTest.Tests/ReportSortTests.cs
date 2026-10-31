using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using KillerScan.Engine;
using KillerScan.Features.Cli;
using KillerScan.Services;

internal static class ReportSortTests
{
    public static Task Run()
    {
        CliThemesHavePalettes();
        string html = ReportExport.BuildHtml([new NetworkDevice { IpAddress = "192.0.2.10", Hostname = "printer" }], "192.0.2.0/24", "dark");

        var headers = Regex.Matches(html, "<th(?: [^>]*)?>(.*?)</th>", RegexOptions.Singleline);
        if (headers.Count != 6) throw new InvalidOperationException("Expected six sortable headers, found " + headers.Count);
        foreach (Match header in headers)
        {
            if (!Regex.IsMatch(header.Groups[1].Value, "^<button type='button' class='sortbtn'>[^<]+<span class='arrow' aria-hidden='true'></span></button>$"))
                throw new InvalidOperationException("Header is not a keyboard-operable sort button: " + header.Value);
        }
        if (!html.Contains(".sortbtn:focus-visible{"))
            throw new InvalidOperationException("Sort buttons need a visible focus style.");
        if (!html.Contains("setAttribute('aria-sort',") || !html.Contains("removeAttribute('aria-sort')"))
            throw new InvalidOperationException("Sorting must expose the active direction through aria-sort.");
        return Task.CompletedTask;
    }

    private static void CliThemesHavePalettes()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-cli-themes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int count = 0;
        try
        {
            foreach (string theme in new[] { "98se", "dark", "light", "black", "blood", "greed", "cyanotic", "ectoplasm",
                "decay", "malaise", "sepulchre", "delirium", "mourning", "98SE" })
            {
                string path = Path.Combine(root, "report.html");
                using var process = Process.Start(new ProcessStartInfo(typeof(CliRunner).Assembly.Location,
                    "scan /demo /html /quiet /theme " + theme + " /export \"" + path + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000)) { process.Kill(); throw new InvalidOperationException("Theme export timed out."); }
                output.GetAwaiter().GetResult();
                string diagnostic = error.GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new InvalidOperationException("Theme export failed: " + diagnostic);
                string html = File.ReadAllText(path);
                var initial = Regex.Match(html, "<html[^>]*class='theme-([^']+)'", RegexOptions.CultureInvariant);
                string expected = theme.Equals("98se", StringComparison.OrdinalIgnoreCase) ? "se98" : theme;
                if (!initial.Success || initial.Groups[1].Value != expected || !html.Contains("html.theme-" + expected + "{")
                    || !html.Contains("setTheme(saved||'" + expected + "')"))
                    throw new InvalidOperationException("CLI theme " + theme + " must initialize a matching emitted palette.");
                count++;
            }
            Console.WriteLine("PASS: " + count + " compiled CLI theme exports initialize emitted palettes, including 98se and 98SE.");
        }
        finally { Directory.Delete(root, true); }
    }
}
