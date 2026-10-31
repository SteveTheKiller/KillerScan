using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KillerScan.Services;

internal static class ProfilePersistenceTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-profile-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "preferences.json");
            File.WriteAllText(path, "{\"committed\":\"original\"}");
            byte[] original = File.ReadAllBytes(path);
            var store = new JsonPreferenceFile<Dictionary<string, string>>(path, () => new(), value => value);
            store.Load();
            var elapsed = Stopwatch.StartNew();
            // A file handle is visible across Windows sessions, unlike a Local named mutex.
            using (var holder = new FileStream(path + ".Preferences.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (store.Change(value => value["pending"] = "retained"))
                    throw new InvalidOperationException("A writer must refuse publication while another session owns the profile lease.");
                if (!original.SequenceEqual(File.ReadAllBytes(path)) || store.Value["pending"] != "retained" ||
                    store.Error == null || !store.Error.Contains("busy"))
                    throw new InvalidOperationException("Contention must preserve committed bytes, pending edits and the busy error.");
            }
            if (elapsed.Elapsed < TimeSpan.FromSeconds(1.7) || elapsed.Elapsed > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("Preference lease contention must keep its two-second deadline.");
            if (!store.Save()) throw new InvalidOperationException("A pending save must recover when the other session releases its lease.");
            store.Load();
            if (store.Value.Count != 2 || store.Value["committed"] != "original" || store.Value["pending"] != "retained")
                throw new InvalidOperationException("Retry must retain both the committed and pending values.");
            ReentryAndIndependence(path);
            AbandonedHolder(path, store);
            Console.WriteLine("PASS: profile leases preserve timeout/retry, reentry, thread exclusion, path/kind independence and abandoned-holder recovery (6 scenarios).");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ReentryAndIndependence(string path)
    {
        using (ProfileLock.Acquire("Preferences", path, TimeSpan.FromSeconds(2), "busy"))
        {
            using (ProfileLock.Acquire("Preferences", Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path)).ToUpperInvariant(), TimeSpan.FromSeconds(2), "busy")) { }
            using (ProfileLock.Acquire("Migrate", path, TimeSpan.FromSeconds(2), "busy")) { }
            using (ProfileLock.Acquire("Preferences", path + ".other", TimeSpan.FromSeconds(2), "busy")) { }
            // Disposing an inner lease must leave the outer file handle and monitor owned.
            var contender = Task.Run(() =>
            {
                try
                {
                    using var lease = ProfileLock.Acquire("Preferences", path, TimeSpan.FromMilliseconds(100), "busy");
                    return false;
                }
                catch (IOException error) { return error.Message == "busy"; }
            });
            if (!contender.Wait(3000) || !contender.Result)
                throw new InvalidOperationException("An inner release must not admit another thread before the outer release.");
            try
            {
                using var reopened = new FileStream(path + ".Preferences.lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                throw new InvalidOperationException("An inner release must not release the cross-process file handle.");
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { }
        }
        using (ProfileLock.Acquire("Preferences", path, TimeSpan.FromSeconds(2), "busy")) { }
    }

    internal static void HoldUntilKilled(string path)
    {
        using var lease = ProfileLock.Acquire("Preferences", path, TimeSpan.FromSeconds(2), "busy");
        Console.WriteLine("LEASE-OWNED");
        Console.Out.Flush();
        Thread.Sleep(Timeout.Infinite);
    }

    private static void AbandonedHolder(string path, JsonPreferenceFile<Dictionary<string, string>> store)
    {
        using var child = Process.Start(new ProcessStartInfo(Assembly.GetEntryAssembly()!.Location,
            "--profile-lock-child \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            var ready = Task.Run(() => child.StandardOutput.ReadLine());
            if (!ready.Wait(5000) || ready.Result != "LEASE-OWNED")
                throw new InvalidOperationException("The child did not acquire its profile lease.");
            if (store.Change(value => value["after-kill"] = "recovered"))
                throw new InvalidOperationException("An independent process must exclude preference publication.");
            child.Kill();
            if (!child.WaitForExit(5000) || !store.Save())
                throw new InvalidOperationException("Process termination must release the lease and allow a pending retry.");
            store.Load();
            if (store.Value["after-kill"] != "recovered" || store.Value["committed"] != "original")
                throw new InvalidOperationException("Recovery must retain both prior and pending data.");
        }
        finally { if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); } }
    }
}
