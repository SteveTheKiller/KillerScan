using System.Net;
using KillerScan.Engine;

internal static class MacCancellationTests
{
    internal static async Task Run()
    {
        var original = NetworkPlatform.Current;
        var originalCompleted = NetworkScanner.DeviceCompleted;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var platform = new BlockingPlatform(entered, release);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<List<NetworkDevice>>? scan = null;
        int completed = 0, found = 0;
        bool macStage = false;
        try
        {
            NetworkPlatform.Current = platform;
            NetworkScanner.DeviceCompleted = _ => Interlocked.Increment(ref completed);
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            scanner.StatusChanged += status => { if (status.Stage == ScanStage.ResolvingMacs) macStage = true; };
            scanner.DeviceFound += _ => Interlocked.Increment(ref found);
            scan = scanner.ScanSubnetAsync("127.0.0.1-20", cancel.Token, fullScan: false);
            Require(await Task.Run(() => entered.Wait(5000)) && macStage, "The scan reaches blocked native MAC resolution.");
            cancel.Cancel();
            Require(await Task.WhenAny(scan, Task.Delay(500)) == scan, "Stop must return while native MAC calls remain blocked.");
            try { await scan; throw new InvalidOperationException("Canceled MAC scan must not return results."); }
            catch (OperationCanceledException) { }
            int started = platform.Calls;
            Require(started is > 0 and <= 8 && platform.Active > 0, "At most eight native lookups own active work after cancellation.");
            using var secondCancel = new CancellationTokenSource();
            secondCancel.Cancel();
            try { await scanner.ScanSubnetAsync("127.0.0.1-20", secondCancel.Token, false); }
            catch (OperationCanceledException) { }
            await Task.Delay(100);
            Require(platform.Calls == started && completed == 0 && found == 0, "Canceled scans never start more native calls or publish late devices.");
            release.Set();
            for (int i = 0; i < 100 && platform.Active > 0; i++) await Task.Delay(10);
            Require(platform.Active == 0, "All owned native calls finish after the fixture releases them.");
            await Task.Delay(100);
            Require(platform.Calls == started && completed == 0 && found == 0, "Queued canceled lookups remain skipped after native capacity returns.");
            var retry = await scanner.ScanSubnetAsync("127.0.0.1", CancellationToken.None, false);
            Require(retry.Count == 1 && completed == 1 && found == 1, "A fresh scan works after canceled native calls finish.");
        }
        finally
        {
            cancel.Cancel(); release.Set();
            if (scan != null) try { await scan; } catch (OperationCanceledException) { }
            for (int i = 0; i < 100 && platform.Active > 0; i++) await Task.Delay(10);
            NetworkPlatform.Current = original; NetworkScanner.DeviceCompleted = originalCompleted;
        }
        Console.WriteLine("PASS: MAC cancellation returns promptly, bounds native work and skips queued calls.");
    }

    private sealed class BlockingPlatform(ManualResetEventSlim entered, ManualResetEventSlim release) : INetworkPlatform
    {
        private int _calls, _active;
        internal int Calls => Volatile.Read(ref _calls);
        internal int Active => Volatile.Read(ref _active);
        public IReadOnlyDictionary<string, string> ReadNeighborCache() => new Dictionary<string, string>();
        public string ResolveMac(IPAddress address)
        {
            Interlocked.Increment(ref _calls); Interlocked.Increment(ref _active); entered.Set();
            try { release.Wait(); return "00:00:0C:12:34:56"; }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void FlushDnsCache() { }
        public (string Interface, string NextHop)? BestRoute(IPAddress address) => ("Loopback", "");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
