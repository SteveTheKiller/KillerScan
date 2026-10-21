using System.Net;
using KillerScan.Engine;

// Library callers get the same input validation as the GUI and CLI: bad targets and
// unusable concurrency are refused up front, before any cache flush or network work.
internal static class ApiContractTests
{
    internal static async Task Run()
    {
        var original = NetworkPlatform.Current;
        var platform = new CountingPlatform();
        try
        {
            NetworkPlatform.Current = platform;
            var scanner = new NetworkScanner(new IsolatedDiscovery());
            foreach (string target in new[] { "", "   ", "not-an-address", "10.0.0.0/8" })
            {
                await RequireRefused<ArgumentException>(() => scanner.ScanSubnetAsync(target, CancellationToken.None),
                    "ScanSubnetAsync refuses \"" + target + "\"");
            }
            await RequireRefused<ArgumentException>(() => scanner.DeepProbeHostAsync("not-an-address", CancellationToken.None),
                "DeepProbeHostAsync refuses a malformed address");
            foreach (int concurrency in new[] { 0, -1 })
            {
                await RequireRefused<ArgumentOutOfRangeException>(
                    () => scanner.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, concurrency),
                    "DeepProbeHostAsync refuses port concurrency " + concurrency);
            }
            Require(platform.Calls == 0, "Refused input caused " + platform.Calls + " platform call(s) such as a DNS cache flush.");

            var none = new ScanOptions().Without(string.Join(",", ScanOptions.Features));
            var devices = await scanner.ScanSubnetAsync("127.0.0.1", CancellationToken.None, none);
            Require(devices != null, "A valid target still scans.");

            // Two scanners side by side keep their own saved types and completion hooks.
            var siteA = new NetworkScanner(new IsolatedDiscovery()) { ManualTypes = _ => "Printer", Completed = d => d.Hostname = "site-a" };
            var siteB = new NetworkScanner(new IsolatedDiscovery()) { ManualTypes = _ => "Camera", Completed = d => d.Hostname = "site-b" };
            var a = await siteA.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, none);
            var b = await siteB.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, none);
            Require(a.DeviceType == "Printer" && a.Hostname == "site-a" && b.DeviceType == "Camera" && b.Hostname == "site-b",
                "Each scanner applies its own manual types and completion hook.");
        }
        finally { NetworkPlatform.Current = original; }
        Console.WriteLine("PASS: public scan APIs refuse bad targets and concurrency before side effects.");
    }

    private static async Task RequireRefused<T>(Func<Task> call, string message) where T : Exception
    {
        try { await call(); }
        catch (T) { return; }
        catch (Exception ex) { throw new InvalidOperationException(message + ": threw " + ex.GetType().Name + " instead of " + typeof(T).Name); }
        throw new InvalidOperationException(message + ": it completed instead.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CountingPlatform : INetworkPlatform
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public IReadOnlyDictionary<string, string> ReadNeighborCache() { Interlocked.Increment(ref _calls); return new Dictionary<string, string>(); }
        public string ResolveMac(IPAddress address) { Interlocked.Increment(ref _calls); return ""; }
        public void FlushDnsCache() => Interlocked.Increment(ref _calls);
        public (string Interface, string NextHop)? BestRoute(IPAddress address) { Interlocked.Increment(ref _calls); return null; }
    }
}
