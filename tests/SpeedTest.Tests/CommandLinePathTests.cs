using System;
using System.IO;
using System.Threading.Tasks;
using KillerScan.Services;

internal static class CommandLinePathTests
{
    public static Task Run()
    {
        string install = Path.Combine(Path.GetTempPath(), "KillerScan-PathTest");
        string previous = Environment.CurrentDirectory;
        Directory.CreateDirectory(install);
        try
        {
            // The uninstaller can run from the install folder. A relative entry that happens to
            // resolve there belongs to the user, not to KillerScan.
            Environment.CurrentDirectory = install;
            Require(CommandLinePath.WithoutEntry(@".;C:\Tools", install) == @".;C:\Tools",
                "Removing the install folder keeps relative PATH entries.");
            Require(CommandLinePath.WithEntry(".", install) == ".;" + install,
                "A relative entry does not count as the install folder when adding it.");

            Require(CommandLinePath.WithEntry(@"%SystemRoot%\system32", install) == @"%SystemRoot%\system32;" + install,
                "Adding keeps unexpanded entries as written.");
            Require(CommandLinePath.WithEntry(@"C:\Tools;" + install.ToUpperInvariant() + @"\", install) == @"C:\Tools;" + install.ToUpperInvariant() + @"\",
                "Adding is skipped when another spelling of the folder is present.");
            Require(CommandLinePath.WithoutEntry(@"%SystemRoot%;""" + install + @""";C:\Tools", install) == @"%SystemRoot%;C:\Tools",
                "Removing takes out quoted spellings and keeps other entries in order.");
            Require(CommandLinePath.WithoutEntry(@"C:\Tools", install) == @"C:\Tools",
                "Removing leaves PATH untouched when the folder is absent.");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(install);
        }
        ConsoleAttach();
        return Task.CompletedTask;
    }

    // `killerscan /scan /json > devices.json` must still attach the console for its errors, and
    // must keep the file handle it was given for the data.
    private static void ConsoleAttach()
    {
        const int Out = -11, Err = -12;
        IntPtr file = new IntPtr(0x100), pipe = new IntPtr(0x200), console = new IntPtr(0x300);
        foreach (var (outRedirected, errRedirected) in new[] { (true, false), (false, true), (false, false), (true, true) })
        {
            var handles = new System.Collections.Generic.Dictionary<int, IntPtr> { [Out] = outRedirected ? file : console, [Err] = errRedirected ? pipe : console };
            bool attached = false;
            KillerScan.Features.Cli.CliRunner.AttachKeepingRedirects(
                id => id == Out ? outRedirected : errRedirected,
                id => handles[id],
                (id, handle) => handles[id] = handle,
                () => { attached = true; handles[Out] = console; handles[Err] = console; return true; });
            Require(attached == !(outRedirected && errRedirected),
                "The console is attached whenever a stream still needs it: out " + outRedirected + ", err " + errRedirected);
            Require(handles[Out] == (outRedirected ? file : console) && handles[Err] == (errRedirected ? pipe : console),
                "Redirected handles survive the attach: out " + outRedirected + ", err " + errRedirected);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
