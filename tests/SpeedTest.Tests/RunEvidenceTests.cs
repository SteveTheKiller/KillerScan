using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
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
            ManualRefreshProvenance();
            var platform = NetworkPlatform.Current;
            try
            {
                NetworkPlatform.Current = new UnsupportedNetworkPlatform();
                RefreshHonorsAdapter();
            }
            finally { NetworkPlatform.Current = platform; }
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

    private sealed class RefreshScanner : NetworkScanner
    {
        public override Task<NetworkDevice> DeepProbeHostAsync(string ip, CancellationToken token,
            ScanOptions options, int portConcurrency = 256, bool flushLocalDnsCache = true) =>
            ip == "192.0.2.2"
                ? Task.FromException<NetworkDevice>(new InvalidOperationException("Controlled refresh failure."))
                : Task.FromResult(new NetworkDevice { IpAddress = ip, Hostname = "refreshed", OpenPorts = [443] });
    }

    private static void ManualRefreshProvenance()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        string directory = Path.Combine(Path.GetTempPath(), "KillerScan-refresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var workspace = new ScanWorkspace("192.0.2.0/24", false, new RefreshScanner());
            var scannedAt = new DateTimeOffset(2025, 3, 4, 5, 6, 0, TimeSpan.Zero);
            workspace.LoadSnapshot("192.0.2.0/24", [new NetworkDevice { IpAddress = "192.0.2.1", OpenPorts = [80] },
                new NetworkDevice { IpAddress = "192.0.2.2", OpenPorts = [22] }], scannedAt);
            var grid = (DataGrid)workspace.FindName("ResultsGrid");
            grid.SelectAll();
            ((MenuItem)workspace.FindName("RescanMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            PumpUntil(() => !workspace.IsScanning && workspace.Session.Devices.Any(d => d.Hostname == "refreshed"));
            string status = workspace.Status;
            string expected = string.Format((string)Application.Current.FindResource("Str_St_RescanPartial"), 1, 2, 1);
            Require(status == expected, "A selected rescan reports what actually refreshed: \"" + status + "\"");

            string output = Path.Combine(directory, "report.html");
            workspace.ExportDialog = dialog => { dialog.FileName = output; return true; };
            workspace.Export("html");
            string report = File.ReadAllText(output);
            Require(report.Contains("<b>2025-03-04 05:06</b>"), "The report keeps the original scan time.");
            Require(report.Contains((string)Application.Current.FindResource("Str_Rep_Refreshed")) &&
                report.Contains((string)Application.Current.FindResource("Str_Rep_RefreshPartial")),
                "The report says some devices were refreshed later and that the refresh was partial.");

            workspace.LoadSnapshot("192.0.2.0/24", [new NetworkDevice { IpAddress = "192.0.2.1" }], scannedAt);
            workspace.Export("html");
            Require(!File.ReadAllText(output).Contains((string)Application.Current.FindResource("Str_Rep_Refreshed")),
                "A new snapshot carries no refresh note from the previous one.");
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class BoundRefreshScanner : NetworkScanner
    {
        internal int Calls;
        internal ScanBinding? UsedBinding;
        public override Task<NetworkDevice> DeepProbeHostAsync(string ip, CancellationToken token,
            ScanOptions options, int portConcurrency = 256, bool flushLocalDnsCache = true)
        {
            Calls++;
            UsedBinding = Binding;
            return Task.FromResult(new NetworkDevice { IpAddress = ip, Hostname = "refreshed" });
        }
    }

    private static void RefreshHonorsAdapter()
    {
        foreach (bool selected in new[] { true, false })
        {
            var scanner = new BoundRefreshScanner();
            using var workspace = new ScanWorkspace("192.0.2.1", false, scanner);
            var original = new NetworkDevice { IpAddress = "192.0.2.1" };
            var time = new DateTimeOffset(2025, 1, 2, 3, 4, 0, TimeSpan.Zero);
            workspace.LoadSnapshot("192.0.2.1", [original], time);
            workspace.Session.Options = new ScanOptions { ReverseDns = false };
            var binding = new ScanBinding("Test adapter", System.Net.IPAddress.Parse("192.0.2.5"), 24);
            ScanBinding? connected = binding;
            workspace.FindAdapter = _ => connected;
            workspace.AdapterChoice = "Test adapter";
            // Disconnect after loading the choice, then edit the bar to a different target.
            connected = null;
            workspace.Targets = "10.0.0.1";
            int confirmations = 0;
            bool accept = false;
            workspace.ConfirmAdapter = _ => { confirmations++; return accept; };
            void Refresh()
            {
                if (selected)
                {
                    ((DataGrid)workspace.FindName("ResultsGrid")).SelectAll();
                    ((MenuItem)workspace.FindName("RescanMenuItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
                else workspace.DeepScan();
                PumpUntil(() => !workspace.IsScanning);
            }
            Refresh();
            Require(scanner.Calls == 0 && confirmations == 1 && workspace.Session.Devices.Single() == original
                && workspace.Session.Refresh == null && workspace.Session.ScannedAt == time,
                "Declining the disconnected adapter refuses refresh without changing results or provenance.");
            accept = true;
            Refresh();
            Require(scanner.Calls == 1 && scanner.UsedBinding == null && confirmations == 2,
                "Accepting fallback refreshes with automatic routing.");
            connected = binding;
            Refresh();
            Require(scanner.Calls == 2 && scanner.UsedBinding == binding && confirmations == 2,
                "Reconnect refresh resolves the current binding and validates result addresses, not editable targets.");
            connected = new ScanBinding("Test adapter", System.Net.IPAddress.Parse("198.51.100.5"), 24);
            var before = workspace.Session.Refresh;
            Refresh();
            Require(scanner.Calls == 2 && workspace.Session.Refresh == before,
                "An adapter moved off the result network refuses refresh before changing provenance.");
        }
        Console.WriteLine("PASS: 8 adapter refresh cases across selected rescan and Deep Scan All.");
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (complete() || elapsed.ElapsedMilliseconds > 3000) frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
