using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KillerScan.Controls;
using KillerScan.Engine;
using KillerScan.Services;

internal static class ScanLifecycleTests
{
    private sealed class ControlledScanner : NetworkScanner
    {
        internal readonly Queue<Func<Task<List<NetworkDevice>>>> Results = new();
        internal Func<string, Task<NetworkDevice>>? DeepResult;
        internal Func<string, CancellationToken, Task<NetworkDevice>>? PendingDeep;
        public override async Task<List<NetworkDevice>> ScanSubnetAsync(string target, CancellationToken token, bool fullScan = true)
        {
            var devices = await Results.Dequeue()();
            foreach (var device in devices) ReportDeviceFound(device);
            return devices;
        }
        public override Task<NetworkDevice> DeepProbeHostAsync(string ip, CancellationToken token,
            int portConcurrency = 256, bool flushLocalDnsCache = true) => PendingDeep?.Invoke(ip, token) ?? DeepResult!(ip);
        public override Task<List<NetworkDevice>> ScanSubnetAsync(string target, CancellationToken token,
            ScanOptions options, bool fullScan = true) => ScanSubnetAsync(target, token, fullScan);
        public override Task<NetworkDevice> DeepProbeHostAsync(string ip, CancellationToken token,
            ScanOptions options, int portConcurrency = 256, bool flushLocalDnsCache = true) =>
            DeepProbeHostAsync(ip, token, portConcurrency, flushLocalDnsCache);
    }

    internal static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        DemoData.Enabled = true;
        ScanHistory.Load(); DevicePreferences.Load();
        var scanner = new ControlledScanner();
        scanner.Results.Enqueue(() => Task.FromResult(new List<NetworkDevice>
            { new() { IpAddress = "192.0.2.1", MacAddress = "00:11:22:33:44:55", OpenPorts = [80] } }));
        scanner.Results.Enqueue(() => Task.FromResult(new List<NetworkDevice>()));
        scanner.Results.Enqueue(() => Task.FromCanceled<List<NetworkDevice>>(new CancellationToken(true)));
        scanner.Results.Enqueue(() => Task.FromException<List<NetworkDevice>>(new InvalidOperationException("Controlled scan failure.")));
        using var workspace = new ScanWorkspace("192.0.2.1", false, scanner);
        int recorded = 0;
        workspace.HistoryRecorded += (_, _) => recorded++;
        workspace.Scan();
        Require(ScanHistory.Entries.Count == 1 && recorded == 1, "A populated successful scan is saved once.");
        workspace.Scan();
        Require(ScanHistory.Entries.Count == 2 && recorded == 2, "A successful empty scan is saved once.");
        var comparison = ScanHistory.Compare(ScanHistory.Entries.Last());
        Require(comparison.Current.Devices.Count == 0 && comparison.Removed.Count == 1,
            "An empty successful result records disappearance of the previous device.");
        workspace.Scan(); workspace.Scan();
        Require(ScanHistory.Entries.Count == 2 && recorded == 2, "Canceled and failed empty scans never masquerade as completed history.");
        Require(!workspace.IsScanning, "The scan lifetime ends after success, cancellation and failure.");
        ScanHistory.Load(); DevicePreferences.Load();
        var emptyScanner = new ControlledScanner();
        emptyScanner.Results.Enqueue(() => Task.FromResult(new List<NetworkDevice>()));
        using var emptyWorkspace = new ScanWorkspace("192.0.2.2", false, emptyScanner);
        emptyWorkspace.Scan();
        Require(ScanHistory.Entries.Count == 1 && !DevicePreferences.HasTrustedDevices,
            "A first empty scan is recorded without creating a trusted-device baseline.");
        CheckDeepProfile();
        ExportSnapshotTests.Run();
        ExportActionTests.Run();
        WatchActionTests.Run();
        Console.WriteLine("PASS: populated, empty, canceled and failed scan history through the workspace command.");
    }

    private static void CheckDeepProfile()
    {
        var originalPlatform = NetworkPlatform.Current;
        NetworkPlatform.Current = new UnsupportedNetworkPlatform();
        try
        {
            ScanHistory.Load(); DevicePreferences.Load();
            ScanHistory.Record("192.0.2.1", [new NetworkDevice { IpAddress = "192.0.2.1", OpenPorts = [22] }]);
            var scanner = new ControlledScanner();
            scanner.Results.Enqueue(() => Task.FromResult(new List<NetworkDevice>
                { new() { IpAddress = "192.0.2.1", OpenPorts = [80] } }));
            scanner.DeepResult = ip => Task.FromResult(new NetworkDevice { IpAddress = ip, OpenPorts = [80, 8443] });
            using var workspace = new ScanWorkspace("192.0.2.1", false, scanner);
            int recorded = 0;
            workspace.HistoryRecorded += (_, _) => recorded++;
            workspace.RunProfile(new ScanProfile { Name = "Deep fixture", Target = "192.0.2.1", DeepScanAfter = true });
            Require(ScanHistory.Entries.Count == 2 && recorded == 1 && ScanHistory.Entries.Last().Devices.Single().OpenPorts.SequenceEqual(new[] { 80, 8443 }),
                "A profile's one completed history snapshot includes its automatic deep-only port.");
            Require(ScanHistory.Entries.First().Devices.Single().OpenPorts.SequenceEqual(new[] { 22 }),
                "Automatic deep completion never changes the previous snapshot.");
            scanner.Results.Enqueue(() => Task.FromResult(new List<NetworkDevice>
                { new() { IpAddress = "192.0.2.1", OpenPorts = [80] } }));
            scanner.DeepResult = _ => Task.FromCanceled<NetworkDevice>(new CancellationToken(true));
            workspace.RunProfile(new ScanProfile { Target = "192.0.2.1", DeepScanAfter = true });
            Require(ScanHistory.Entries.Count == 3 && recorded == 2 && ScanHistory.Entries.Last().Completion == ScanCompletion.Canceled,
                "A canceled automatic deep phase saves one explicitly canceled snapshot.");
            var history = new HistoryWorkspace();
            history.ShowEntry(ScanHistory.Entries.Last());
            Require(((System.Windows.Documents.Run)history.FindName("HistoryTarget")).Text.Contains("Scan canceled"),
                "Canceled history exposes its status in the selected snapshot's heading.");
            scanner.Results.Enqueue(() => Task.FromResult(new List<NetworkDevice>
                { new() { IpAddress = "192.0.2.1", OpenPorts = [80] } }));
            scanner.DeepResult = _ => Task.FromException<NetworkDevice>(new InvalidOperationException("Controlled deep failure."));
            workspace.RunProfile(new ScanProfile { Target = "192.0.2.1", DeepScanAfter = true });
            Require(ScanHistory.Entries.Count == 4 && recorded == 3 && ScanHistory.Entries.Last().Completion == ScanCompletion.Partial,
                "A failed deep phase saves one explicitly partial snapshot.");
            history.ShowEntry(ScanHistory.Entries.Last());
            Require(((System.Windows.Documents.Run)history.FindName("HistoryTarget")).Text.Contains("Partial scan"),
                "Partial history exposes its status in the selected snapshot's heading.");
            Require(ScanHistory.Entries.Last().ToString().Contains("Partial scan"), "The history row's accessible name includes the partial status.");
            scanner.DeepResult = ip => Task.FromResult(new NetworkDevice { IpAddress = ip, OpenPorts = [443] });
            workspace.DeepScan();
            Require(ScanHistory.Entries.Count == 4 && ScanHistory.Entries.Last().Devices.Single().OpenPorts.SequenceEqual(new[] { 80 }),
                "A manual deep rescan keeps existing history snapshots immutable and does not add an automatic-profile entry.");
            var grid = (System.Windows.Controls.DataGrid)workspace.FindName("ResultsGrid");
            grid.SelectedItem = workspace.Session.Devices.Single();
            ((System.Windows.Controls.MenuItem)workspace.FindName("RescanMenuItem")).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            Require(ScanHistory.Entries.Count == 4 && ScanHistory.Entries.Last().Devices.Single().OpenPorts.SequenceEqual(new[] { 80 }),
                "A selected manual rescan also leaves saved snapshots immutable.");
            var pending = new TaskCompletionSource<NetworkDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
            scanner.PendingDeep = (_, token) => { token.Register(() => pending.TrySetCanceled()); return pending.Task; };
            scanner.Results.Enqueue(() => Task.FromResult(new List<NetworkDevice>
                { new() { IpAddress = "192.0.2.1", OpenPorts = [80] } }));
            workspace.RunProfile(new ScanProfile { Target = "192.0.2.1", DeepScanAfter = true });
            Require(workspace.IsScanning && ScanHistory.Entries.Count == 4, "An automatic profile remains active and unsaved while its deep phase is pending.");
            var settings = (System.Windows.Controls.Button)workspace.FindName("ScanOptionsButton");
            Require(!settings.IsEnabled && !settings.ContextMenu.IsOpen,
                "Scan settings stay locked throughout an automatic deep scan.");
            ExportSnapshotTests.CheckPartial(workspace);
            workspace.RunProfile(new ScanProfile { Target = "192.0.2.1", DeepScanAfter = true });
            workspace.Stop();
            PumpUntil(() => !workspace.IsScanning);
            Require(settings.IsEnabled, "Stopping a deep scan makes scan settings available again.");
            Require(ScanHistory.Entries.Count == 5 && recorded == 4 && ScanHistory.Entries.Last().Completion == ScanCompletion.Canceled,
                "Stop cancels the active deep phase, rejects a rapid rerun, and records one canceled run.");
            pending = new TaskCompletionSource<NetworkDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
            scanner.Results.Enqueue(() => Task.FromResult(new List<NetworkDevice>
                { new() { IpAddress = "192.0.2.1", OpenPorts = [80] } }));
            workspace.RunProfile(new ScanProfile { Target = "192.0.2.1", DeepScanAfter = true });
            workspace.Dispose();
            PumpUntil(() => !workspace.IsScanning);
            Require(ScanHistory.Entries.Count == 6 && recorded == 4 && ScanHistory.Entries.Last().Completion == ScanCompletion.Canceled,
                "Closing during an automatic deep phase preserves the canceled snapshot without late workspace notifications.");
        }
        finally { NetworkPlatform.Current = originalPlatform; }
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(5) };
        timer.Tick += (_, _) => { if (complete() || elapsed.ElapsedMilliseconds > 2000) frame.Continue = false; };
        timer.Start();
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Require(complete(), "The controlled scan lifetime finishes within two seconds.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
