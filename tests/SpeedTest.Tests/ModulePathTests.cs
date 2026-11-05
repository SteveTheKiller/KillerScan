using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KillerScan.Shell;

/// <summary>
/// The bundled modules live in a version-stamped folder. A per-user update hands the reopened app
/// the PSModulePath the old copy had, which already names the old version's folder, so the new
/// copy must replace that entry rather than append after it: PowerShell takes the first match.
/// </summary>
internal static class ModulePathTests
{
    internal static void Run()
    {
        string parent = Path.Combine(@"C:\Users\someone\AppData\Local", "KillerScan", "Modules");
        string current = Path.Combine(parent, "1.9.1.0");
        string older = Path.Combine(parent, "1.9.0.0");
        string oldest = Path.Combine(parent, "1.8.3.0");
        const string user = @"C:\Users\someone\Documents\WindowsPowerShell\Modules;C:\Program Files\WindowsPowerShell\Modules";

        Require(MainWindow.ComposeModulePath(user + ";" + older, current, parent) == user + ";" + current,
            "An older bundled module folder is replaced by the current one.");
        Require(MainWindow.ComposeModulePath(user + ";" + oldest + ";" + older.ToUpperInvariant() + "\\;", current, parent) == user + ";" + current,
            "Every older bundled folder goes, in any case and with a trailing separator.");
        Require(MainWindow.ComposeModulePath(user + ";" + current, current, parent) == user + ";" + current,
            "The current folder is not added twice.");
        Require(MainWindow.ComposeModulePath("", current, parent) == current, "An empty path becomes the current folder.");
        Require(MainWindow.ComposeModulePath(user + @";C:\Other\KillerScan\Modules\1.0.0.0", current, parent)
                == user + @";C:\Other\KillerScan\Modules\1.0.0.0;" + current,
            "A folder outside this profile's bundled modules is the user's and stays.");
        Console.WriteLine("PASS a reopened update replaces the previous version's bundled module folder on PSModulePath.");

        // Two KillerScan windows opening their first terminal at once unpack into the same folder.
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-modules-" + Guid.NewGuid().ToString("N"));
        try
        {
            var results = Enumerable.Range(0, 4).Select(_ => Task.Run(() => MainWindow.UnpackBundled("KillerScripts", root))).ToArray();
            Task.WaitAll(results);
            string dir = Path.Combine(root, "KillerScripts");
            Require(File.Exists(Path.Combine(dir, ".unpacked")) && Directory.GetFiles(dir, "*.psd1").Length > 0
                    && Directory.GetFiles(dir, "*.tmp").Length == 0,
                "Concurrent unpacks finish with a complete module and no temporary files.");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
        Console.WriteLine("PASS concurrent first terminals both get the bundled modules.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
