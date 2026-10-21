using System.Diagnostics;
using System.Net;
using KillerScan.Engine;

// A name lookup that never answers must not hold a scan, and stalled lookups must not pile up.
internal static class DnsBudgetTests
{
    internal static async Task Run()
    {
        var original = NetworkScanner.HostnameResolverOverride;
        var stall = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        try
        {
            NetworkScanner.HostnameResolverOverride = _ => { Interlocked.Increment(ref started); return stall.Task; };
            var options = new ScanOptions().Without(string.Join(",", ScanOptions.Features)).WithFeature("dns", true);
            var scanner = new NetworkScanner(new IsolatedDiscovery());

            var watch = Stopwatch.StartNew();
            var device = await scanner.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, options, flushLocalDnsCache: false).WaitAsyncCompat(TimeSpan.FromSeconds(10));
            Require(device.Hostname.Length == 0 && watch.Elapsed < TimeSpan.FromSeconds(6),
                $"A stalled lookup gives up within its budget, not {watch.Elapsed.TotalSeconds:0.0} s.");

            var many = Enumerable.Range(0, 200).Select(_ => scanner.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, options, 4, false));
            await Task.WhenAll(many).WaitAsyncCompat(TimeSpan.FromSeconds(30));
            Require(Volatile.Read(ref started) <= NetworkScanner.MaxPendingHostnameLookups,
                $"Stalled lookups are bounded at {NetworkScanner.MaxPendingHostnameLookups}; {started} were started.");

            stall.SetResult("late.example");
            await Task.Delay(200);
            int before = Volatile.Read(ref started);
            NetworkScanner.HostnameResolverOverride = _ => { Interlocked.Increment(ref started); return Task.FromResult("fresh.example"); };
            var after = await scanner.DeepProbeHostAsync("127.0.0.1", CancellationToken.None, options, flushLocalDnsCache: false);
            Require(after.Hostname == "fresh.example" && Volatile.Read(ref started) == before + 1,
                "Once stalled lookups finish, their slots are free for new lookups.");
        }
        finally
        {
            stall.TrySetResult("");
            NetworkScanner.HostnameResolverOverride = original;
        }
        Console.WriteLine("PASS: name lookups have a time budget and a bound on stalled lookups.");
    }

    private static async Task<T> WaitAsyncCompat<T>(this Task<T> task, TimeSpan limit)
    {
        if (await Task.WhenAny(task, Task.Delay(limit)) != task) throw new TimeoutException("The operation did not finish within " + limit.TotalSeconds + " s.");
        return await task;
    }

    private static async Task WaitAsyncCompat(this Task task, TimeSpan limit)
    {
        if (await Task.WhenAny(task, Task.Delay(limit)) != task) throw new TimeoutException("The operation did not finish within " + limit.TotalSeconds + " s.");
        await task;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
