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

        // A small gzip body that expands past the ceiling is refused instead of filling memory.
        byte[] Gzip(byte[] plain)
        {
            using var packed = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(packed, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
                gz.Write(plain, 0, plain.Length);
            return packed.ToArray();
        }
        byte[] bomb = Gzip(new byte[VendorDbUpdater.MaxTextBytes + 1]);
        Require(bomb.Length < 1024 * 1024, "The expansion fixture should be small on the wire.");
        bool refused = false;
        try { VendorDbUpdater.Decode(bomb); } catch (InvalidDataException) { refused = true; }
        Require(refused, "A gzip body that expands past the vendor database ceiling must be refused.");
        refused = false;
        try { VendorDbUpdater.Decode(new byte[VendorDbUpdater.MaxDownloadBytes + 1]); } catch (InvalidDataException) { refused = true; }
        Require(refused, "A download past the vendor database ceiling must be refused.");
        Require(VendorDbUpdater.Decode(Gzip(Encoding.UTF8.GetBytes("00:00:0C\tCisco\n"))) == "00:00:0C\tCisco\n"
                && VendorDbUpdater.Decode(Encoding.UTF8.GetBytes("plain\n")) == "plain\n",
            "Ordinary gzip and plain lists still decode.");
        Console.WriteLine("PASS: vendor downloads stop at their size ceilings, compressed or not.");

        // Device-sent text is bounded in length but otherwise kept as sent.
        var device = new NetworkDevice
        {
            HttpTitle = new string('t', 50_000), SnmpDescr = new string('s', 50_000), Hostname = new string('h', 5_000),
            SshBanner = "SSH-2.0-x\u001b]0;title\u0007", HttpServer = new string('a', 1023) + "😀"
        };
        Require(device.HttpTitle.Length == 1024 && device.SnmpDescr.Length == 1024 && device.Hostname.Length == 255,
            "Device text is capped: " + device.HttpTitle.Length + ", " + device.SnmpDescr.Length + ", " + device.Hostname.Length);
        Require(device.SshBanner == "SSH-2.0-x\u001b]0;title\u0007", "Short device text keeps its original characters.");
        Require(device.HttpServer.Length == 1023 && !char.IsHighSurrogate(device.HttpServer[device.HttpServer.Length - 1]), "A cap never splits a surrogate pair.");
        Console.WriteLine("PASS: device-sent text is length-bounded without rewriting what the device said.");

        // Only the block sizes the lookup understands (24, 28 and 36 bits) become keys. A /25 must not
        // turn into a /24 key that overwrites the real MA-L owner, and a /2 must not write an empty key.
        var (count, parsed) = VendorDbUpdater.ParseManuf(
            "00:00:0C\tCisco\tCisco Systems\n" +
            "00:50:C2:00:00:00/28\tIeee\tIEEE Registration\n" +
            "70:B3:D5:00:00:00/36\tSmall\tSmall Block\n" +
            "00:00:0C:80:00:00/25\tSquat\tSquatter\n" +
            "00:00:00:00:00:00/2\tEmpty\tEmpty Key\n");
        Require(count == 3 && parsed.ToString() == "00000C\tCisco Systems\n0050C20\tIEEE Registration\n70B3D5000\tSmall Block\n",
            "manuf masks other than 24, 28 and 36 are skipped: " + count + " " + parsed.ToString().Replace("\n", " | "));
        Console.WriteLine("PASS: manuf blocks outside 24, 28 and 36 bits are skipped instead of truncated.");
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
