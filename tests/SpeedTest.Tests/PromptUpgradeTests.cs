using System;
using System.IO;
using System.Text;
using KillerScan.Services;
using KillerScan.Shell;

/// <summary>
/// The user's KillerPrompt.ps1 is theirs to edit and is never overwritten. A copy they never
/// edited is not a customization, though: it is an old shipped prompt, and it has to take the
/// current one or a prompt security fix never reaches it.
/// </summary>
internal static class PromptUpgradeTests
{
    internal static void Run()
    {
        string user = MainWindow.PromptScriptPath;
        Require(user.StartsWith(AppDataFolder.Path, StringComparison.OrdinalIgnoreCase), "The prompt lives in the app's data folder.");
        string folder = Path.GetDirectoryName(user)!;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);

        MainWindow.EnsurePromptScript();
        string shipped = File.ReadAllText(user);
        Require(shipped.Length > 0, "A first run unpacks the prompt.");
        Require(MainWindow.ShippedPromptHashes.Contains(MainWindow.PromptHash(shipped)),
            "The prompt this build ships is listed, so the next version can upgrade an unedited copy of it.");

        const string oldShipped = "# an older shipped prompt\r\nfunction prompt { 'old> ' }\r\n";
        MainWindow.ShippedPromptHashes.Add(MainWindow.PromptHash(oldShipped));
        try
        {
            File.WriteAllText(user, oldShipped.Replace("\r\n", "\n"), new UTF8Encoding(true));
            MainWindow.EnsurePromptScript();
            Require(File.ReadAllText(user) == shipped, "An unedited older prompt, whatever its line endings, takes the current version.");

            string edited = oldShipped + "# mine\r\n";
            File.WriteAllText(user, edited, new UTF8Encoding(true));
            MainWindow.EnsurePromptScript();
            Require(File.ReadAllText(user) == edited, "An edited prompt is never overwritten.");
        }
        finally { MainWindow.ShippedPromptHashes.Remove(MainWindow.PromptHash(oldShipped)); }
        Console.WriteLine("PASS: an unedited shipped prompt takes the current version and an edited one is kept.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
