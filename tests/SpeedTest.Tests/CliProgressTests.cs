using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using KillerScan.Engine;
using KillerScan.Features.Cli;

internal static class CliProgressTests
{
    private const int Threads = 50;
    private const int Rounds = 40;

    internal static void Run()
    {
        // The scanner raises progress and device events from many worker threads. The handlers must
        // never tear a line and a percent step must be reported at most once.
        for (int attempt = 0; attempt < 5; attempt++) Once();
        Console.WriteLine("PASS: CLI scan progress handlers tolerate concurrent scanner events.");
    }

    private static void Once()
    {
        var scanner = new NetworkScanner();
        var err = new StringWriter();
        CliRunner.AttachProgress(scanner, err, terminalProgress: true, showProgress: true);
        var progress = Raiser<int>(scanner, nameof(NetworkScanner.ProgressChanged));
        var found = Raiser<NetworkDevice>(scanner, nameof(NetworkScanner.DeviceFound));
        using var gate = new ManualResetEventSlim();
        int counter = 0;
        var workers = Enumerable.Range(0, Threads).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            for (int round = 0; round < Rounds; round++)
            {
                found(new NetworkDevice { IpAddress = "192.0.2.1" });
            }
            int p;
            while ((p = Interlocked.Increment(ref counter)) <= 20000) progress(p);
        })).ToArray();
        gate.Set();
        Task.WaitAll(workers);
        var lines = err.ToString().Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var deviceLines = lines.Where(line => line.StartsWith("Devices: ", StringComparison.Ordinal)).ToArray();
        var progressLines = lines.Where(line => line.StartsWith("Progress: ", StringComparison.Ordinal)).ToArray();
        Require(deviceLines.Length + progressLines.Length == lines.Length
            && deviceLines.All(line => int.TryParse(line.Substring(9), out _))
            && progressLines.All(line => line.EndsWith("%", StringComparison.Ordinal) && int.TryParse(line.Substring(10, line.Length - 11), out _)),
            "Every output line from concurrent scanner events is a complete key line.");
        Require(deviceLines.Length == Threads * Rounds, "Every device event writes one line (" + deviceLines.Length + ").");
        Require(progressLines.Distinct().Count() == progressLines.Length,
            "A percent step is reported once even when threads race on it (" + progressLines.Length + " lines, " + progressLines.Distinct().Count() + " distinct).");
    }

    private static Action<T> Raiser<T>(NetworkScanner scanner, string eventName)
    {
        var field = typeof(NetworkScanner).GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("No backing field for " + eventName);
        return value => ((Action<T>?)field.GetValue(scanner))?.Invoke(value);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
