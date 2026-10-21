using System;
using System.IO;
using System.Linq;
using KillerScan.Services;

// A history file that cannot be read must say so, keep its bytes, and load normally once
// the problem clears. It must never look like an empty archive.
internal static class HistoryLoadTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KillerScan-history-load-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.Now;
        try
        {
            var missing = new HistoryArchive(Path.Combine(directory, "missing.json"));
            missing.Load(now);
            Require(missing.LoadError == null && missing.Entries.Count == 0, "A missing file is an empty archive, not an error.");

            string malformedPath = Path.Combine(directory, "malformed.json");
            File.WriteAllText(malformedPath, "{broken");
            var malformed = new HistoryArchive(malformedPath);
            malformed.Load(now);
            Require(malformed.LoadError != null, "Malformed JSON is reported as unreadable history.");
            Require(File.ReadAllText(malformedPath) == "{broken", "Malformed history keeps its original bytes.");

            string path = Path.Combine(directory, "history.json");
            var writer = new HistoryArchive(path);
            writer.Load(now);
            writer.Append(new ScanHistoryEntry { Target = "192.0.2.0/24", ScannedAt = now }, now);
            byte[] saved = File.ReadAllBytes(path);

            var locked = new HistoryArchive(path);
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                locked.Load(now);
            Require(locked.LoadError != null && locked.Entries.Count == 0, "A locked file is reported as unavailable history.");
            Require(!locked.Append(new ScanHistoryEntry { Target = "session", ScannedAt = now }, now) || File.Exists(path),
                "Recording while unavailable never removes the archive.");

            var retry = new HistoryArchive(path);
            retry.Load(now);
            Require(retry.LoadError == null && retry.Entries.Any(entry => entry.Target == "192.0.2.0/24"),
                "History loads normally once the lock clears.");
            Require(File.ReadAllBytes(path).Length >= saved.Length, "The archive survives the failed load.");

            File.WriteAllText(malformedPath, "[]");
            malformed.Load(now);
            Require(malformed.LoadError == null, "Reloading after a repair clears the error.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: unreadable history is reported, preserved and reloads after the failure clears.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
