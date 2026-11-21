using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KillerScan.Engine;
using KillerScan.Features.Cli;
using KillerScan.Services;

/// <summary>
/// killerscan history lists saved runs, shows or exports one, and compares it with its baseline, all without
/// changing a history file, even one whose Time retention would drop an expired run when the app loads it.
/// </summary>
internal static class CliHistoryTests
{
    private static readonly string[] Flags = ["/json", "/csv", "/html", "/table", "/compare", "/no-header", "/quiet", "/demo"];

    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-cli-history-" + Guid.NewGuid().ToString("N"));
        string previous = LocationService.Root;
        try
        {
            LocationService.Root = root;
            var catalog = new LocationCatalog(root);
            catalog.Load("Unassigned");
            if (!catalog.Create("Office", out var office) || !catalog.Create("Lab", out var lab))
                throw new Exception("The test Locations could not be created.");
            var now = DateTimeOffset.Now;
            var expired = now.AddDays(-40);
            var older = now.AddDays(-2);
            var newest = now.AddDays(-1);
            var labRun = now.AddHours(-30);
            string officeFile = Path.Combine(catalog.DirectoryFor(office!), "history.json");
            string labFile = Path.Combine(catalog.DirectoryFor(lab!), "history.json");
            Seed(officeFile, timePolicy: true,
                Run(expired, [Device("192.168.1.9", "00:00:00:00:00:09")]),
                Run(older, [Device("192.168.1.10", "aa:aa:aa:aa:aa:aa", 22), Device("192.168.1.11", "bb:bb:bb:bb:bb:bb")]),
                Run(newest, [Device("192.168.1.10", "aa:aa:aa:aa:aa:aa", 22, 80), Device("192.168.1.12", "cc:cc:cc:cc:cc:cc")], name: "Front office"));
            Seed(labFile, timePolicy: false, Run(labRun, [Device("10.0.0.5", "dd:dd:dd:dd:dd:dd")], target: "10.0.0.0/24"));
            byte[] officeHash = Hash(officeFile), labHash = Hash(labFile);

            // Every Location, newest first, with the expired run left out the way the app shows it.
            var list = Call("/json");
            using (var doc = JsonDocument.Parse(list.Out))
            {
                var rows = doc.RootElement.EnumerateArray().ToList();
                var when = rows.Select(row => DateTimeOffset.Parse(row.GetProperty("scannedAt").GetString()!, CultureInfo.InvariantCulture)).ToList();
                if (list.Code != 0 || !when.SequenceEqual([newest, labRun, older]))
                    throw new Exception("history /json lists every Location's runs newest first without the expired one: " + list.Out + list.Err);
                var top = rows[0];
                if (top.GetProperty("location").GetString() != "Office" || top.GetProperty("name").GetString() != "Front office"
                    || top.GetProperty("devices").GetInt32() != 2 || top.GetProperty("completion").GetString() != "Complete"
                    || top.GetProperty("target").GetString() != "192.168.1.0/24" || top.GetProperty("number").GetInt32() != 1)
                    throw new Exception("history /json describes each run: " + top);
            }
            var text = Call();
            if (text.Code != 0 || !text.Out.Contains("Front office") || !text.Out.Contains("Lab") || text.Out.Split('\n').Length != 5)
                throw new Exception("history lists a header, a rule and one row per run:\n" + text.Out);
            var scoped = Call("/location", "office", "/json");
            using (var doc = JsonDocument.Parse(scoped.Out))
                if (scoped.Code != 0 || doc.RootElement.GetArrayLength() != 2)
                    throw new Exception("history /location keeps to that Location: " + scoped.Out + scoped.Err);

            // One run by its number or by the scannedAt that /json gave, in each output format.
            var shown = Call("1", "/location", "Office", "/json");
            using (var doc = JsonDocument.Parse(shown.Out))
            {
                var devices = doc.RootElement.GetProperty("devices").EnumerateArray().ToList();
                if (shown.Code != 0 || devices.Count != 2 || devices[0].GetProperty("ipAddress").GetString() != "192.168.1.10"
                    || !devices[0].GetProperty("openPorts").EnumerateArray().Select(port => port.GetInt32()).SequenceEqual([22, 80]))
                    throw new Exception("history <n> /json carries the run's devices: " + shown.Out + shown.Err);
            }
            string key = older.ToString("o", CultureInfo.InvariantCulture);
            var csv = Call(key, "/csv");
            if (csv.Code != 0 || !csv.Out.StartsWith(ReportExport.CsvHeader, StringComparison.Ordinal) || !csv.Out.Contains("192.168.1.11"))
                throw new Exception("history <scannedAt> /csv writes the run's devices: " + csv.Out + csv.Err);
            string export = Path.Combine(root, "out", "run.html");
            var html = Call(key, "/export", export, "/theme", "98se", "/quiet");
            string expected = ReportExport.BuildSnapshotHtml(
                // Built the way History's own HTML export builds its rows (HistoryWorkspace.VisibleDevices).
                [new NetworkDevice { IpAddress = "192.168.1.10", MacAddress = "aa:aa:aa:aa:aa:aa", Hostname = "", Vendor = "", DeviceType = "", OpenPorts = [22] },
                 new NetworkDevice { IpAddress = "192.168.1.11", MacAddress = "bb:bb:bb:bb:bb:bb", Hostname = "", Vendor = "", DeviceType = "" }],
                "192.168.1.0/24", "se98", older, ScanCompletion.Complete, location: "Office");
            string written = html.Code == 0 ? File.ReadAllText(export) : "";
            if (html.Code != 0 || html.Out.Length != 0 || written != expected)
            {
                int at = Enumerable.Range(0, Math.Min(written.Length, expected.Length)).FirstOrDefault(i => written[i] != expected[i]);
                throw new Exception("history /export .html writes the run's own report, dated when it ran: exit " + html.Code + " " + html.Err
                    + " out " + html.Out.Length + " lengths " + written.Length + "/" + expected.Length + " first difference near: "
                    + written.Substring(Math.Max(0, at - 80), Math.Min(200, Math.Max(0, written.Length - Math.Max(0, at - 80)))));
            }

            // The newest Office run against its baseline: one device added, one gone, one with a new port.
            var compare = Call("1", "/location", "Office", "/compare", "/json");
            using (var doc = JsonDocument.Parse(compare.Out))
            {
                var root2 = doc.RootElement;
                string Ips(string name) => string.Join(",", root2.GetProperty(name).EnumerateArray().Select(d => d.GetProperty("ipAddress").GetString()));
                if (compare.Code != 0 || DateTimeOffset.Parse(root2.GetProperty("baseline").GetProperty("scannedAt").GetString()!, CultureInfo.InvariantCulture) != older
                    || Ips("added") != "192.168.1.12" || Ips("removed") != "192.168.1.11" || Ips("changed") != "192.168.1.10"
                    || !root2.GetProperty("changed")[0].GetProperty("before").GetProperty("openPorts").EnumerateArray().Select(p => p.GetInt32()).SequenceEqual([22]))
                    throw new Exception("history /compare /json names what changed since the baseline: " + compare.Out + compare.Err);
            }
            var first = Call(key, "/compare", "/json");
            using (var doc = JsonDocument.Parse(first.Out))
                if (first.Code != 0 || doc.RootElement.GetProperty("baseline").ValueKind != JsonValueKind.Null
                    || doc.RootElement.GetProperty("added").GetArrayLength() != 0)
                    throw new Exception("A run with no baseline says so instead of calling every device new: " + first.Out);
            var compareText = Call("1", "/location", "Office", "/compare");
            if (compareText.Code != 0 || !compareText.Out.Contains("ADDED") || !compareText.Out.Contains("REMOVED") || !compareText.Out.Contains("CHANGED"))
                throw new Exception("history /compare prints its changes:\n" + compareText.Out);

            // Mistakes are usage errors, and a run that is not there is not found.
            if (Call("9").Code != 3) throw new Exception("history 9 reports a run that does not exist with exit 3.");
            if (Call("2026-01-01T00:00:00.0000000+00:00").Code != 3) throw new Exception("An unknown scannedAt is not found, exit 3.");
            if (Call("abc").Code != 2 || Call("1", "2").Code != 2 || Call("/demo").Code != 2 || Call("/location", "Nowhere").Code != 2
                || Call("/compare").Code != 2 || Call("1", "/csv", "/json").Code != 2 || Call("1", "/theme", "plaid").Code != 2)
                throw new Exception("history rejects a bad run, extra runs, /demo, an unknown Location, /compare with no run and conflicting formats.");

            if (!Hash(officeFile).SequenceEqual(officeHash) || !Hash(labFile).SequenceEqual(labHash))
                throw new Exception("history changed a history file; it only reads them.");
            Console.WriteLine("PASS: history lists, shows, exports and compares saved runs without changing them.");
        }
        finally
        {
            LocationService.Root = previous;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private static HistoricalDevice Device(string ip, string mac, params int[] ports) => new()
    {
        Identity = "mac:" + mac.ToUpperInvariant(), IpAddress = ip, MacAddress = mac, OpenPorts = [.. ports]
    };

    private static ScanHistoryEntry Run(DateTimeOffset when, List<HistoricalDevice> devices, string target = "192.168.1.0/24", string name = "") => new()
    {
        ScannedAt = when, Target = target, TargetKey = ScanHistoryEntry.KeyFor(target), Options = new ScanOptions(), Deep = false,
        Completion = ScanCompletion.Complete, Devices = devices, Name = name
    };

    // Written straight to disk: the expired run has to be in the file for the read-only check to mean anything.
    private static void Seed(string path, bool timePolicy, params ScanHistoryEntry[] runs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var file = new JsonObject
        {
            ["Version"] = 1,
            ["Policy"] = JsonSerializer.SerializeToNode(timePolicy ? new HistoryRetention(HistoryRetentionMode.Time, Days: 30) : new HistoryRetention(HistoryRetentionMode.SaveAll)),
            ["Entries"] = JsonSerializer.SerializeToNode(runs.ToList())
        };
        File.WriteAllText(path, file.ToJsonString());
        var check = new HistoryStore(path);
        check.LoadReadOnly();
        if (check.LoadError != null || check.Entries.Count != runs.Count(run => !timePolicy || run.ScannedAt > DateTimeOffset.Now.AddDays(-30)))
            throw new Exception("The seeded history does not read back: " + check.LoadError);
    }

    private static byte[] Hash(string path)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(File.ReadAllBytes(path));
    }

    private static (int Code, string Out, string Err) Call(params string[] words)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < words.Length; i++)
        {
            if (!words[i].StartsWith("/", StringComparison.Ordinal)) { positionals.Add(words[i]); continue; }
            options[words[i]] = Flags.Contains(words[i]) ? "" : words[++i];
        }
        var @out = new StringWriter();
        var err = new StringWriter();
        int code = CliRunner.RunHistory(positionals, options, @out, err);
        return (code, @out.ToString().Trim(), err.ToString().Trim());
    }
}
