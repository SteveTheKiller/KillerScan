using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

internal static class HistoryRetentionTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _checks;
    private static object? Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Instance)!.Invoke(value, args);
    private static object? Property(object value, string name) => value.GetType().GetProperty(name, Instance)!.GetValue(value);
    private static void Require(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }

    internal static void Run()
    {
        var assembly = typeof(KillerScan.App).Assembly;
        var archiveType = assembly.GetType("KillerScan.Services.HistoryArchive", true)!;
        var policyType = assembly.GetType("KillerScan.Services.HistoryRetention", true)!;
        var modeType = assembly.GetType("KillerScan.Services.HistoryRetentionMode", true)!;
        var entryType = assembly.GetType("KillerScan.Services.ScanHistoryEntry", true)!;
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        string temp = Path.GetFullPath(Path.GetTempPath());
        string scratch = Path.Combine(temp, "KillerScan-retention-tests-" + Guid.NewGuid().ToString("N"));
        Require(Path.GetFullPath(scratch).StartsWith(temp, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(scratch).StartsWith("KillerScan-retention-tests-", StringComparison.Ordinal), "Scratch path is under the temporary directory.");
        Directory.CreateDirectory(scratch);
        try
        {
            object Archive(string name) => Activator.CreateInstance(archiveType, Instance, null,
                new object[] { Path.Combine(scratch, name) }, null)!;
            object Policy(string mode, int count = 100, int days = 30) =>
                Activator.CreateInstance(policyType, Enum.Parse(modeType, mode), count, days)!;
            object Entry(string target, DateTimeOffset stamp)
            {
                var value = Activator.CreateInstance(entryType)!;
                entryType.GetProperty("Target")!.SetValue(value, target);
                entryType.GetProperty("ScannedAt")!.SetValue(value, stamp);
                return value;
            }
            IList Entries(object archive) => (IList)Property(archive, "Entries")!;
            string Mode(object archive) => Property(Property(archive, "Policy")!, "Mode")!.ToString()!;
            bool Apply(object archive, object policy, Func<int, bool> confirm) =>
                (bool)Call(archive, "Apply", policy, now, confirm)!;
            void Append(object archive, object entry, DateTimeOffset clock) => Call(archive, "Append", entry, clock);
            void Load(object archive, DateTimeOffset clock) => Call(archive, "Load", clock);
            void Fails(Action action, string message)
            {
                bool failed = false;
                try { action(); } catch (TargetInvocationException ex) when (ex.InnerException is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException) { failed = true; }
                Require(failed, message);
            }

            var fresh = Archive("new.json");
            Load(fresh, now);
            Require(Mode(fresh) == "Count" && (int)Property(Property(fresh, "Policy")!, "Count")! == 100 &&
                !File.Exists(Path.Combine(scratch, "new.json")), "New installations default to 100 without writing on load.");
            for (int i = 0; i < 101; i++) Append(fresh, Entry("new-" + i, now.AddMinutes(i)), now.AddMinutes(i));
            Require(Entries(fresh).Count == 100 && (string)Property(Entries(fresh)[0]!, "Target")! == "new-1",
                "The new default retains the latest 100 future scans.");
            var freshReload = Archive("new.json"); Load(freshReload, now);
            Require(Entries(freshReload).Count == 100 && Mode(freshReload) == "Count", "Default policy and history survive reload.");

            var legacy = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
            for (int i = 0; i < 150; i++) legacy.Add(Entry("legacy-" + i, now.AddDays(i - 150)));
            var deviceType = assembly.GetType("KillerScan.Services.HistoricalDevice", true)!;
            var device = Activator.CreateInstance(deviceType)!;
            foreach (var pair in new[] { ("Identity", "mac:00:11:22:33:44:55"), ("IpAddress", "192.0.2.40"),
                ("Hostname", "saved, \"quoted\"\nname"), ("MacAddress", "00:11:22:33:44:55"),
                ("Vendor", "Fixture vendor"), ("DeviceType", "Server") })
                deviceType.GetProperty(pair.Item1)!.SetValue(device, pair.Item2);
            deviceType.GetProperty("OpenPorts")!.SetValue(device, new List<int> { 22, 8443 });
            ((IList)Property(legacy[149]!, "Devices")!).Add(device);
            string path = Path.Combine(scratch, "legacy.json");
            File.WriteAllText(path, JsonSerializer.Serialize(legacy, legacy.GetType()));
            byte[] original = File.ReadAllBytes(path);
            var archive = Archive("legacy.json"); Load(archive, now);
            Require(Entries(archive).Count == 150 && Mode(archive) == "SaveAll", "Legacy archives upgrade to Save all without pruning.");
            Require(File.ReadAllBytes(path).SequenceEqual(original), "Upgrade loading leaves the existing file byte-for-byte unchanged.");
            Append(archive, Entry("after-upgrade", now), now);
            Require(Entries(archive).Count == 151, "Recording after migration preserves all existing entries.");
            var upgraded = Archive("legacy.json"); Load(upgraded, now);
            Require(Entries(upgraded).Count == 151 && Mode(upgraded) == "SaveAll", "Migrated policy persists on the next scan and reloads.");
            var savedDevice = ((IList)Property(Entries(upgraded)[149]!, "Devices")!)[0]!;
            Require((string)Property(savedDevice, "Hostname")! == (string)Property(device, "Hostname")! &&
                (string)Property(savedDevice, "Identity")! == (string)Property(device, "Identity")! &&
                ((IEnumerable<int>)Property(savedDevice, "OpenPorts")!).SequenceEqual(new[] { 22, 8443 }),
                "Migration and reload preserve saved device identities, escaped names and ports.");

            original = File.ReadAllBytes(path);
            int removed = -1;
            Require(!Apply(archive, Policy("Count", 10), count => { removed = count; return false; }) && removed == 141,
                "A lower count reports the exact removal count and honors refusal.");
            Require(Entries(archive).Count == 151 && Mode(archive) == "SaveAll" && File.ReadAllBytes(path).SequenceEqual(original),
                "Cancel leaves both stored policy and history unchanged.");
            Require(!Apply(archive, Policy("Time"), count => { removed = count; return false; }) && removed == 120,
                "Time-policy changes also require confirmation of their exact removal count.");
            Require(File.ReadAllBytes(path).SequenceEqual(original) && Entries(archive).Count == 151,
                "Canceling time retention performs no write.");
            Fails(() => Apply(archive, Policy("Count", 0), _ => throw new InvalidOperationException("No confirmation for invalid input.")),
                "Invalid policy bounds are rejected before confirmation or persistence.");
            Require(File.ReadAllBytes(path).SequenceEqual(original), "Invalid policy leaves the archive intact.");
            Require(Apply(archive, Policy("Count", 10), count =>
            {
                Require(count == 141 && Entries(archive).Count == 151 && File.ReadAllBytes(path).SequenceEqual(original),
                    "The removal count is presented before deletion and before any settings write.");
                return true;
            }), "An explicitly accepted count policy commits.");
            Require(Entries(archive).Count == 10 && Mode(archive) == "Count", "Accepted count and history commit together.");
            var countReload = Archive("legacy.json"); Load(countReload, now);
            Require(Entries(countReload).Count == 10 && (int)Property(Property(countReload, "Policy")!, "Count")! == 10,
                "Count configuration and pruned history reload together.");
            for (int i = 0; i < 5; i++) Append(countReload, Entry("future-" + i, now.AddMinutes(i + 1)), now.AddMinutes(i + 1));
            Require(Entries(countReload).Count == 10 && (string)Property(Entries(countReload)[9]!, "Target")! == "future-4",
                "Future scans continue to enforce the chosen count.");

            var timed = Archive("time.json");
            Require(Apply(timed, Policy("SaveAll"), _ => false), "Save all applies without a deletion prompt when nothing is removed.");
            foreach (var stamp in new[] { now.AddDays(-5), now.AddDays(-1), now, now.AddHours(12) })
                Append(timed, Entry(stamp.ToString("O"), stamp), now);
            Require(Apply(timed, Policy("Time", days: 1), count => { Require(count == 1, "Time removal excludes its exact boundary and future scans."); return true; }),
                "Confirmed time policy applies.");
            Require(Entries(timed).Count == 3, "Time policy retains boundary, recent and future entries.");
            var timeReload = Archive("time.json"); Load(timeReload, now);
            Require(Entries(timeReload).Count == 3 && Mode(timeReload) == "Time" &&
                (int)Property(Property(timeReload, "Policy")!, "Days")! == 1, "Time duration and entries reload.");
            var expiredReload = Archive("time.json"); Load(expiredReload, now.AddDays(2));
            Require(Entries(expiredReload).Count == 0 && Mode(expiredReload) == "Time", "The explicitly selected time policy expires old scans on a future startup.");
            Append(expiredReload, Entry("new-later", now.AddDays(3)), now.AddDays(3));
            Append(expiredReload, Entry("already-old", now), now.AddDays(3));
            Require(Entries(expiredReload).Count == 1 && (string)Property(Entries(expiredReload)[0]!, "Target")! == "new-later",
                "Future recording also enforces the selected time policy.");
            Require(Apply(expiredReload, Policy("SaveAll"), _ => throw new InvalidOperationException("Save all removes nothing.")),
                "Switching to Save all requires no deletion confirmation.");
            for (int i = 0; i < 105; i++) Append(expiredReload, Entry("unlimited-" + i, now.AddDays(-100)), now);
            Require(Entries(expiredReload).Count == 106, "Save all preserves old scans and has no count ceiling.");
            var allReload = Archive("time.json"); Load(allReload, now.AddYears(1));
            Require(Entries(allReload).Count == 106 && Mode(allReload) == "SaveAll", "Save all persists and never expires scans on reload.");

            string malformed = Path.Combine(scratch, "malformed.json");
            File.WriteAllText(malformed, "{broken");
            var unreadable = Archive("malformed.json"); Load(unreadable, now);
            Fails(() => Apply(unreadable, Policy("Count"), _ => true), "An unreadable archive cannot be overwritten by settings.");
            Append(unreadable, Entry("session-only", now), now);
            Require(File.ReadAllText(malformed) == "{broken", "An unreadable archive also survives future recording intact.");
            string futurePath = Path.Combine(scratch, "future-version.json");
            File.WriteAllText(futurePath, "{\"Version\":99,\"Entries\":[],\"Policy\":{\"Mode\":0,\"Count\":100,\"Days\":30}}");
            var futureVersion = Archive("future-version.json"); Load(futureVersion, now);
            byte[] futureBytes = File.ReadAllBytes(futurePath);
            Fails(() => Apply(futureVersion, Policy("Count"), _ => true), "Unknown archive versions remain protected.");
            Require(File.ReadAllBytes(futurePath).SequenceEqual(futureBytes), "An unknown future archive is never overwritten.");
            File.WriteAllText(Path.Combine(scratch, "blocked"), "fixture");
            var failedWrite = Archive(Path.Combine("blocked", "history.json")); Load(failedWrite, now);
            Fails(() => Apply(failedWrite, Policy("Time", days: 7), _ => true), "A failed persistence operation is reported.");
            Require(Mode(failedWrite) == "Count" && Entries(failedWrite).Count == 0, "Failed saving does not change policy or entries.");

            var race = Archive("race.json");
            Apply(race, Policy("SaveAll"), _ => false);
            for (int i = 0; i < 4; i++) Append(race, Entry("race-" + i, now.AddMinutes(i)), now);
            var confirmations = new List<int>();
            Require(!Apply(race, Policy("Count", 2), count =>
            {
                confirmations.Add(count);
                if (confirmations.Count == 1) { Append(race, Entry("finished-during-confirmation", now.AddMinutes(5)), now); return true; }
                return false;
            }), "A new scan during confirmation invalidates the original removal plan.");
            Require(confirmations.SequenceEqual(new[] { 2, 3 }) && Entries(race).Count == 5 && Mode(race) == "SaveAll",
                "The revised removal count is confirmed again; cancel preserves every scan.");
            Require(!Directory.EnumerateFiles(scratch, "*.tmp", SearchOption.AllDirectories).Any(), "Atomic archive writes leave no temporary files.");
        }
        finally { Directory.Delete(scratch, true); }
        Console.WriteLine("PASS: " + _checks + " scratch-only retention, migration, persistence, confirmation and future-pruning assertions.");
    }
}
