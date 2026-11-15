using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using KillerScan.Engine;
using KillerScan.Features.Cli;

/// <summary>
/// killerscan watch /max-seconds is a hard cap: a ping still in flight when the time runs out
/// does not hold the run past it, and a text-mode run that the cap cut short says so.
/// </summary>
internal static class CliWatchDeadlineTests
{
    internal static void Run()
    {
        // A ping that never answers. Without the cap racing it, the run would hang for good;
        // with it, the run ends near one second. Three seconds leaves room for a slow machine.
        var seam = typeof(ConnectionChecks).GetField("PingOverride", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("ConnectionChecks.PingOverride not found.");
        seam.SetValue(null, (Func<IPAddress, Task<long?>>)(_ => new TaskCompletionSource<long?>().Task));
        var watch = Stopwatch.StartNew();
        int code;
        try
        {
            var run = Task.Run(() => Watch("192.0.2.1", "/count", "1", "/max-seconds", "1", "/json").Code);
            if (!run.Wait(10000)) throw new Exception("watch ignored its 1 s /max-seconds cap while a ping hung.");
            code = run.Result;
        }
        finally { seam.SetValue(null, null); }
        watch.Stop();
        if (watch.ElapsedMilliseconds >= 3000)
            throw new Exception($"watch ran {watch.ElapsedMilliseconds} ms past a 1 s /max-seconds cap (exit {code}).");

        var (_, text, err) = Watch("127.0.0.1", "/count", "20", "/interval", "10", "/max-seconds", "1");
        if (text.Trim().Length == 0) throw new Exception("text-mode watch dropped the samples it collected.");
        if (err.IndexOf("max-seconds", StringComparison.OrdinalIgnoreCase) < 0)
            throw new Exception("text-mode watch did not say the /max-seconds cap ended it early. stderr: " + err);
        Console.WriteLine("PASS: watch /max-seconds is a hard cap and text mode reports the early stop.");
    }

    private static (int Code, string Out, string Err) Watch(params string[] args)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("/", StringComparison.Ordinal)) { positionals.Add(args[i]); continue; }
            bool flag = args[i] == "/json";
            options[args[i]] = flag ? "" : args[++i];
        }
        var run = typeof(CliRunner).GetMethod("RunWatch", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("CliRunner.RunWatch not found.");
        var @out = new StringWriter();
        var err = new StringWriter();
        int code = (int)run.Invoke(null, new object[] { positionals, options, @out, err })!;
        return (code, @out.ToString(), err.ToString());
    }
}
