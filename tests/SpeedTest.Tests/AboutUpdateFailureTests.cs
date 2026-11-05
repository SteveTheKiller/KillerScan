using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using KillerScan.Features.About;

/// <summary>
/// A downloaded update that fails its checksum or signature is refused, and the releases page is
/// no remedy for that, so About says so instead of treating it like a network blip. A helper that
/// could not start offers the releases page, except after a declined UAC prompt, which is the
/// user's own answer. Every language carries both messages.
/// </summary>
internal static class AboutUpdateFailureTests
{
    internal static void Run()
    {
        Require(!AboutController.OffersManualDownload(new Win32Exception(1223)),
            "A declined UAC prompt does not offer the releases page.");
        Require(AboutController.OffersManualDownload(new IOException("The update helper did not start.")),
            "A helper that could not start offers the releases page.");

        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Strings", "en-US.xaml"))) dir = Path.GetDirectoryName(dir);
        Require(dir != null, "The string tables are found.");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var tables = Directory.GetFiles(Path.Combine(dir!, "Strings"), "*.xaml");
        Require(tables.Length == 20, "All 20 string tables are checked.");
        foreach (var table in tables)
        {
            var strings = XDocument.Load(table).Descendants()
                .Where(e => e.Attribute(x + "Key") != null)
                .ToDictionary(e => (string)e.Attribute(x + "Key")!, e => e.Value);
            string name = Path.GetFileName(table);
            Require(strings.TryGetValue("Str_About_UpdateNotVerified", out var refused) && refused.Trim().Length > 0,
                name + " says a refused update was not installed.");
            Require(strings.TryGetValue("Str_About_UpdateHandoffFailed", out var failed) && failed.Contains("{0}"),
                name + " says the update could not start and shows the reason.");
        }
        Console.WriteLine("PASS: About reports a refused update and a failed hand-off in every language.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
