using System.Text;
using KillerScan.Engine;

internal static class VendorDatabaseTests
{
    internal static Task Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KillerScan-vendors-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "oui.txt");
            File.WriteAllText(path, "00000C\tOriginal vendor\n", new UTF8Encoding(false));
            byte[] original = File.ReadAllBytes(path);
            bool failed = false;
            try
            {
                VendorDbUpdater.PublishDatabase(path, "00000C\tReplacement vendor\n", (stream, bytes) =>
                {
                    stream.Write(bytes, 0, 4);
                    throw new IOException("Controlled partial vendor write.");
                });
            }
            catch (IOException) { failed = true; }
            Require(failed && File.ReadAllBytes(path).SequenceEqual(original),
                "A partial vendor database write must leave the prior complete file byte-identical.");
            failed = false;
            try { VendorDbUpdater.PublishDatabase(path, "00000C\tReplacement vendor\n", (stream, bytes) => stream.Write(bytes, 0, 4)); }
            catch (IOException) { failed = true; }
            Require(failed && File.ReadAllBytes(path).SequenceEqual(original), "A silently truncated sibling is rejected before publication.");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                failed = false;
                try { VendorDbUpdater.PublishDatabase(path, "00000C\tReplacement vendor\n"); }
                catch (IOException) { failed = true; }
                Require(failed && File.ReadAllBytes(path).SequenceEqual(original), "A denied rename preserves the prior vendor database.");
            }
            string leftover = path + ".interrupted.tmp";
            File.WriteAllText(leftover, "incomplete earlier update");
            VendorDbUpdater.PublishDatabase(path, "00000C\tReplacement vendor\n");
            Require(File.ReadAllText(path) == "00000C\tReplacement vendor\n", "Successful replacement publishes the entire new database.");
            Require(File.ReadAllText(leftover) == "incomplete earlier update", "A leftover staging file is never mistaken for the database.");
            string first = Path.Combine(directory, "new", "oui.txt");
            VendorDbUpdater.PublishDatabase(first, "00000C\tFirst vendor\n");
            Require(File.ReadAllText(first) == "00000C\tFirst vendor\n", "First creation publishes complete bytes into a new directory.");
            Require(Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories).SequenceEqual(new[] { leftover }),
                "Only the intentionally preexisting staging file remains.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: vendor publication preserves the prior database after partial output.");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
