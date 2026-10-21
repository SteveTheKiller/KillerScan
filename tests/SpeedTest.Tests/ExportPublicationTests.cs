using System;
using System.IO;
using System.Linq;
using System.Text;
using KillerScan.Services;

// An export that fails partway must leave the file it was replacing exactly as it was,
// and must not leave staging files beside it.
internal static class ExportPublicationTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KillerScan-export-publication-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "report.csv");
            byte[] original = Encoding.UTF8.GetBytes("previous report\r\n");
            File.WriteAllBytes(path, original);

            Fails(() => ExportFile.Write(path, stream =>
            {
                stream.Write(new byte[] { 1, 2, 3 }, 0, 3);
                throw new IOException("Encoder failed partway.");
            }));
            Require(File.ReadAllBytes(path).SequenceEqual(original), "A failed write keeps the previous export byte for byte.");
            Require(Directory.GetFiles(directory).Length == 1, "A failed write leaves no staging files behind.");

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Fails(() => ExportFile.WriteText(path, "new report", new UTF8Encoding(false)));
            Require(File.ReadAllBytes(path).SequenceEqual(original), "A locked target keeps the previous export.");
            Require(Directory.GetFiles(directory).Length == 1, "A refused replacement leaves no staging files behind.");

            ExportFile.WriteText(path, "new report", new UTF8Encoding(false));
            Require(File.ReadAllText(path) == "new report", "A successful write replaces the export.");

            string fresh = Path.Combine(directory, "new.csv");
            ExportFile.WriteText(fresh, "first", Encoding.UTF8);
            Require(File.ReadAllBytes(fresh).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }) && File.ReadAllText(fresh) == "first",
                "A new export is created with the requested encoding.");
            Require(Directory.GetFiles(directory).Length == 2, "Successful writes leave only the exports.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: exports publish complete files and keep the previous file on failure.");
    }

    private static void Fails(Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("The write was expected to fail.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
