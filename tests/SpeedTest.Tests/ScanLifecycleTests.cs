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
        public override async Task<List<NetworkDevice>> ScanSubnetAsync(string target, CancellationToken token, bool fullScan = true)
        {
            var devices = await Results.Dequeue()();
            foreach (var device in devices) ReportDeviceFound(device);
            return devices;
        }
    }

    internal static void Run()
    {
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
        Console.WriteLine("PASS: populated, empty, canceled and failed scan history through the workspace command.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
