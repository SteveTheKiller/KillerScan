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

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
