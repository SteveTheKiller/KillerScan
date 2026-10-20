using System.Text;
using KillerScan.Engine;

internal static class VendorSnapshotTests
{
    internal static async Task Run()
    {
        using (var start = new ManualResetEventSlim())
        {
            var firstReaders = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                start.Wait();
                Require(OuiLookup.GetVendor("00:00:0C:11:22:33").Length > 0 && OuiLookup.Count > 10000,
                    "Concurrent first readers receive an initialized bundled database.");
            })).ToArray();
            start.Set();
            var initialized = Task.WhenAll(firstReaders);
            Require(await Task.WhenAny(initialized, Task.Delay(10000)) == initialized, "Concurrent first load completes within ten seconds.");
            await initialized;
        }
        string directory = Path.Combine(Path.GetTempPath(), "KillerScan-vendor-snapshot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string PathFor(string variant)
            {
                string path = Path.Combine(directory, variant + ".txt");
                var text = new StringBuilder();
                for (int i = 0; i < 65536; i++) text.Append((0xA00000 + i).ToString("X6")).Append('\t').Append(variant).Append('\n');
                text.Append("00000C\t").Append(variant).Append("-L\n1234567\t").Append(variant).Append("-M\n123456789\t").Append(variant).Append("-S\n");
                File.WriteAllText(path, text.ToString());
                return path;
            }
            string a = PathFor("A"), b = PathFor("B");
            OuiLookup.ReloadFrom(a);
            string[] macs = ["00:00:0C:11:22:33", "12:34:56:70:00:00", "12:34:56:78:90:00"];
            string[] suffixes = ["-L", "-M", "-S"];
            using var stop = new CancellationTokenSource();
            int missing = 0, reads = 0;
            using var started = new ManualResetEventSlim();
            var reader = Task.Run(() =>
            {
                started.Set();
                while (!stop.IsCancellationRequested)
                    for (int i = 0; i < macs.Length; i++)
                    {
                        string vendor = OuiLookup.GetVendor(macs[i]);
                        if (vendor != "A" + suffixes[i] && vendor != "B" + suffixes[i]) Interlocked.Increment(ref missing);
                        Interlocked.Increment(ref reads);
                    }
            });
            try
            {
                Require(started.Wait(5000), "A concurrent vendor reader starts.");
                for (int i = 0; i < 6; i++) OuiLookup.ReloadFrom(i % 2 == 0 ? b : a);
            }
            finally { stop.Cancel(); await reader; }
            Require(reads > 0 && missing == 0, "Concurrent vendor reads must see a complete old or new MA-L/MA-M/MA-S snapshot, never an empty or corrupted table. Missing=" + missing);
            int count = OuiLookup.Count;
            DateTime? refreshed = OuiLookup.LastRefreshed;
            Require(count == 65539 && OuiLookup.UsingExternal && refreshed == File.GetLastWriteTime(a),
                "The published table and its external provenance describe the same complete snapshot.");
            using (var locked = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None))
                Require(!OuiLookup.ReloadFrom(a), "A failed reload reports that the prior snapshot remains active.");
            Require(OuiLookup.Count == count && OuiLookup.LastRefreshed == refreshed && OuiLookup.UsingExternal &&
                OuiLookup.GetVendor(macs[0]) == "A-L" && OuiLookup.GetVendor(macs[1]) == "A-M" && OuiLookup.GetVendor(macs[2]) == "A-S",
                "Read failure preserves all table sizes, provenance and prefix results from the prior snapshot.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: concurrent vendor readers see only complete published snapshots.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
