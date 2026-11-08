using KillerScan.Engine;

/// <summary>
/// One command-line scan in fifty sat for minutes before it printed anything, with no CPU and no
/// sockets. The watchdog turns that into a message that names the step it stalled in.
/// </summary>
internal static class StallWatchdogTests
{
    internal static async Task Run()
    {
        string? stalledIn = null;
        using (var dog = new StallWatchdog(TimeSpan.FromMilliseconds(200), step => stalledIn = step))
        {
            dog.Step("loading the vendor list");
            await Task.Delay(600);
        }
        Require(stalledIn == "loading the vendor list", $"A stall reports the step it stalled in (got {stalledIn ?? "nothing"}).");

        int fired = 0;
        using (var dog = new StallWatchdog(TimeSpan.FromMilliseconds(200), _ => Interlocked.Increment(ref fired)))
        {
            dog.Step("detecting the local network");
            dog.Disarm();
            await Task.Delay(600);
        }
        Require(fired == 0, "A disarmed watchdog never fires.");

        fired = 0;
        using (var dog = new StallWatchdog(TimeSpan.FromMilliseconds(300), _ => Interlocked.Increment(ref fired)))
        {
            for (int i = 0; i < 6; i++) { dog.Step("step " + i); await Task.Delay(100); }
            dog.Disarm();
        }
        Require(fired == 0, "Each new step restarts the clock, so steady progress never counts as a stall.");
        Console.WriteLine("PASS: the startup watchdog names a stalled step and stays quiet otherwise.");
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
