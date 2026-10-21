using System;
using System.Linq;
using KillerScan.Engine;
using KillerScan.Services;

// Saved scans, history baselines and manual refreshes must describe what was actually run.
internal static class RunEvidenceTests
{
    internal static void Run()
    {
        bool demo = DemoData.Enabled;
        DemoData.Enabled = true;   // keeps every store in memory
        try
        {
            ProfileSaveKeepsDeepScan();
            HistoryBaselines();
        }
        finally { DemoData.Enabled = demo; }
        Console.WriteLine("PASS: saved scans, baselines and refreshes describe what was actually run.");
    }

    private static void ProfileSaveKeepsDeepScan()
    {
        ScanProfiles.Load();
        Require(ScanProfiles.Save(new ScanProfile { Name = "Office LAN", Target = "10.0.0.0/24", DeepScanAfter = true }), "Seed profile saves.");
        Require(ScanProfiles.SaveEdited("Office LAN", "10.0.1.0/24", new ScanOptions { Http = false }), "Same-name save succeeds.");
        var saved = ScanProfiles.Items.Single(item => item.Name.Equals("Office LAN", StringComparison.OrdinalIgnoreCase));
        Require(saved.DeepScanAfter, "Saving over a profile keeps its Deep Scan choice.");
        Require(saved.Target == "10.0.1.0/24" && !saved.Options.Http, "Saving over a profile updates its target and options.");
        Require(ScanProfiles.SaveEdited("OFFICE lan", "10.0.2.0/24", new ScanOptions()), "Case-variant save succeeds.");
        Require(ScanProfiles.Items.Count(item => item.Name.Equals("Office LAN", StringComparison.OrdinalIgnoreCase)) == 1
            && ScanProfiles.Items.Single(item => item.Name.Equals("Office LAN", StringComparison.OrdinalIgnoreCase)).DeepScanAfter,
            "A case-variant save replaces the same profile and keeps Deep Scan.");
        Require(ScanProfiles.SaveEdited("Printers", "10.0.0.50", new ScanOptions()), "New profile saves.");
        Require(!ScanProfiles.Items.Single(item => item.Name == "Printers").DeepScanAfter, "A new profile starts without Deep Scan.");
    }

    private static NetworkDevice Device(string ip, params int[] ports) =>
        new() { IpAddress = ip, MacAddress = "00:11:22:33:44:" + ip.Split('.').Last().PadLeft(2, '0'), OpenPorts = [.. ports] };

    private static void HistoryBaselines()
    {
        var defaults = new ScanOptions();
        ScanHistory.Load();
        ScanHistory.Record("10.0.0.1 +1", "10.0.0.1,10.0.0.2", [Device("10.0.0.1"), Device("10.0.0.2")], ScanCompletion.Complete, defaults, false);
        var other = ScanHistory.Record("10.0.0.1 +1", "10.0.0.1,10.0.0.3", [Device("10.0.0.1"), Device("10.0.0.3")], ScanCompletion.Complete, defaults, false);
        Require(other.Previous == null, "Different target sets with the same caption never become each other's baseline.");
        var same = ScanHistory.Record("10.0.0.2 +1", " 10.0.0.2 , 10.0.0.1", [Device("10.0.0.1"), Device("10.0.0.2")], ScanCompletion.Complete, defaults, false);
        Require(same.Previous?.Devices.Any(d => d.IpAddress == "10.0.0.2") == true && same.Removed.Count == 0,
            "The same targets in another order and spacing compare with each other.");

        ScanHistory.Load();
        ScanHistory.Record("192.0.2.0/24", "192.0.2.0/24", [Device("192.0.2.1", 22, 80), Device("192.0.2.2")], ScanCompletion.Complete, defaults, false);
        var canceled = ScanHistory.Record("192.0.2.0/24", "192.0.2.0/24", [Device("192.0.2.1", 22)], ScanCompletion.Canceled, defaults, false);
        Require(canceled.Previous != null && canceled.Removed.Count == 0 && canceled.Changed.Count == 0,
            "A canceled run cannot report devices or ports as removed.");
        var after = ScanHistory.Record("192.0.2.0/24", "192.0.2.0/24", [Device("192.0.2.1", 22, 80), Device("192.0.2.2")], ScanCompletion.Complete, defaults, false);
        Require(after.Previous?.Completion == ScanCompletion.Complete && after.Removed.Count == 0 && after.Changed.Count == 0,
            "A complete run compares with the last complete run, not a canceled one.");
        var noPorts = ScanHistory.Record("192.0.2.0/24", "192.0.2.0/24", [Device("192.0.2.1"), Device("192.0.2.2")], ScanCompletion.Complete, defaults with { Ports = false }, false);
        Require(noPorts.Previous == null, "A run with port checks disabled is not compared with one that checked ports.");
        var deep = ScanHistory.Record("192.0.2.0/24", "192.0.2.0/24", [Device("192.0.2.1", 22, 80, 443), Device("192.0.2.2")], ScanCompletion.Complete, defaults, true);
        Require(deep.Previous == null, "A deep run is not compared with a normal run.");
        var gone = ScanHistory.Record("192.0.2.0/24", "192.0.2.0/24", [Device("192.0.2.1", 22, 80)], ScanCompletion.Complete, defaults, false);
        Require(gone.Removed.Count == 1 && gone.Removed[0].IpAddress == "192.0.2.2", "Two compatible complete runs still report a removed device.");
        Require(ScanHistory.Compare(gone.Current).Previous == gone.Previous, "Viewing an entry later picks the same baseline.");

        ScanHistory.Load();
        ScanHistory.SeedDemo([
            new ScanHistoryEntry { Target = "10.0.0.1 +1", ScannedAt = DateTimeOffset.Now.AddDays(-2), Completion = ScanCompletion.Complete },
            new ScanHistoryEntry { Target = "10.9.0.0/24", ScannedAt = DateTimeOffset.Now.AddDays(-1), Completion = ScanCompletion.Complete,
                Devices = [new HistoricalDevice { Identity = "ip:10.9.0.5", IpAddress = "10.9.0.5" }] },
            new ScanHistoryEntry { Target = "10.9.0.0/24", ScannedAt = DateTimeOffset.Now.AddHours(-1), Completion = ScanCompletion.Complete }]);
        var lossy = ScanHistory.Record("10.0.0.1 +1", "10.0.0.1,10.0.0.2", [Device("10.0.0.1")], ScanCompletion.Complete, defaults, false);
        Require(lossy.Previous == null, "An old entry saved only as a \"first +N\" caption is never used as a baseline.");
        var legacy = ScanHistory.Compare(ScanHistory.Entries.Last(entry => entry.Target == "10.9.0.0/24"));
        Require(legacy.Previous?.Devices.Count == 1 && legacy.Removed.Count == 1,
            "Old single-target entries still compare with each other.");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
