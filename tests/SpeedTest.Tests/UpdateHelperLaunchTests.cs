using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using KillerScan.Services;

/// <summary>
/// Runs the real unelevated self-update helper through the same launch the app uses, with a copy of cmd.exe
/// standing in for the downloaded update. Relaunch and the recovery report are recorded instead of opened.
/// The elevated path is the trusted machine helper, covered by <see cref="UpdateHandoffTests"/>.
/// </summary>
internal static class UpdateHelperLaunchTests
{
    private static readonly byte[] Original = Encoding.UTF8.GetBytes("original inert application image");
    // cmd.exe carries no embedded signature, so the publisher check is replaced and checked on its own.
    private static readonly string Signer = new string('A', 40);

    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-update-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Every helper's first statement limits modules to Windows PowerShell's own folder, before
            // any cmdlet can autoload one planted in the user's Documents (review D2-001).
            string arguments = ImmutablePowerShell.Arguments("exit 0");
            string bootstrap = UninstallCommandTests.Bootstrap(arguments);
            Require(bootstrap.StartsWith("$env:PSModulePath=[IO.Path]::Combine($PSHOME,'Modules');", StringComparison.Ordinal),
                "The immutable helper resets PSModulePath before anything else runs.");
            Console.WriteLine("PASS every PowerShell helper loads modules only from Windows PowerShell's own folder.");

            byte[] standIn = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "cmd.exe"));
            LaunchShape(root, standIn);

            var ran = Case(root, "verified", standIn, standIn);
            Require(ran.Exit == 0 && ran.Target.SequenceEqual(standIn) && ran.Relaunched && !ran.ReportShown && !ran.DownloadLeft,
                "A verified update is published from the command-line helper and relaunched: " + ran);
            var swapped = Case(root, "swapped", standIn.Concat(new byte[] { 0 }).ToArray(), standIn);
            Require(swapped.Exit != 0 && swapped.Target.SequenceEqual(Original) && !swapped.Relaunched && swapped.ReportShown,
                "A download replaced after verification is refused and its recovery report is shown: " + swapped);
            Console.WriteLine("PASS the unelevated update helper publishes only the verified image and refuses a swapped download.");

            // The elevated helper has no window to report in, so a refused update reopens the
            // installed copy instead of leaving KillerScan closed with nothing said.
            var refused = Case(root, "elevated-swapped", standIn.Concat(new byte[] { 0 }).ToArray(), standIn, elevated: true);
            Require(refused.Exit != 0 && refused.Target.SequenceEqual(Original) && refused.Relaunched && !refused.ReportShown,
                "A refused elevated publication relaunches the installed copy: " + refused);
            Console.WriteLine("PASS a failed elevated publication reopens the installed KillerScan.");

            // Reopening can fail on its own (an administrator from another account, no desktop). The
            // update is finished by then, so it still succeeds and leaves no backup behind.
            var reopenFails = Case(root, "reopen-fails", standIn, standIn, elevated: true, relaunchFails: true);
            Require(reopenFails.Exit == 0 && reopenFails.Target.SequenceEqual(standIn) && !reopenFails.DownloadLeft
                    && !Directory.EnumerateFiles(Path.Combine(root, "reopen-fails"), "*.old*").Any(),
                "A relaunch failure after a finished publication is not reported as a failed update: " + reopenFails);
            Console.WriteLine("PASS a relaunch failure leaves a finished update finished.");
            ElevatedRelaunch(root, standIn);

            // Downloads from earlier attempts are swept; recent ones and other files are not.
            string temp = Path.Combine(root, "temp");
            Directory.CreateDirectory(temp);
            string old = Path.Combine(temp, "KillerScan_update_" + Guid.NewGuid().ToString("N") + ".exe");
            string recent = Path.Combine(temp, "KillerScan_update_" + Guid.NewGuid().ToString("N") + ".exe");
            string other = Path.Combine(temp, "KillerScan_update_notes.exe");
            foreach (string file in new[] { old, old + ".recovery.txt", recent, other }) File.WriteAllBytes(file, Original);
            foreach (string file in new[] { old, old + ".recovery.txt", other }) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddHours(-2));
            UpdateService.SweepStaleDownloads(temp, DateTime.UtcNow);
            Require(!File.Exists(old) && !File.Exists(old + ".recovery.txt") && File.Exists(recent) && File.Exists(other),
                "Only update downloads and reports older than an hour are swept.");
            Console.WriteLine("PASS stale update downloads and their reports are swept.");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // The cases above record the relaunch instead of running it, and only a real one proves Windows
    // accepts the token, so this needs an elevated process on the signed-in desktop (SN02, not CI).
    private static void ElevatedRelaunch(string root, byte[] standIn)
    {
        using var self = Process.GetCurrentProcess();
        if (!KillerFamily.Deployment.InstallerExitCodes.IsElevated()
            || !Process.GetProcessesByName("explorer").Any(shell => shell.SessionId == self.SessionId))
        {
            Console.WriteLine("SKIP the real elevated relaunch needs an elevated process on the signed-in desktop.");
            return;
        }
        string name = "relaunch-" + Guid.NewGuid().ToString("N");
        string target = Path.Combine(root, name + ".exe");
        File.WriteAllBytes(target, standIn);
        string script = "$ErrorActionPreference='Stop'\n" + UpdateImagePublication.Guard
            + "\n[KillerUpdateGuard]::Launch('" + target + "','',$true)";
        using var helper = Process.Start(UpdateService.HelperStart(script)) ?? throw new IOException("The update helper did not start.");
        if (!helper.WaitForExit(60000)) { helper.Kill(); throw new TimeoutException("The update helper did not finish."); }
        Process[] started = Process.GetProcessesByName(name);
        try
        {
            Require(helper.ExitCode == 0 && started.Length == 1,
                "An elevated helper starts the app with the desktop shell's token: exit " + helper.ExitCode + ", started " + started.Length);
        }
        finally
        {
            foreach (var process in started) { process.Kill(); process.WaitForExit(10000); process.Dispose(); }
        }
        Console.WriteLine("PASS an elevated helper really starts the app with the desktop shell's token.");
    }

    private static void LaunchShape(string root, byte[] standIn)
    {
        if (KillerFamily.Deployment.InstallerExitCodes.IsElevated())
        {
            Console.WriteLine("SKIP the unelevated update launch shape needs an unelevated test process.");
            return;
        }
        string download = Path.Combine(root, "launch.exe");
        File.WriteAllBytes(download, standIn);
        UpdateImagePublication.RememberVerified(download, Hash(standIn), Signer);
        ProcessStartInfo? captured = null;
        try
        {
            UpdateService.StartSwap(download, "9.9.9", new UpdateService.Platform
            {
                CanWrite = _ => true,
                Start = start => { captured = start; return null; }
            });
        }
        catch (IOException) { }
        Require(captured is { UseShellExecute: true } start && start.Verb.Length == 0
            && start.FileName.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", StringComparison.OrdinalIgnoreCase)
            && !start.Arguments.Contains(".bat") && start.WorkingDirectory == Environment.GetFolderPath(Environment.SpecialFolder.System),
            "An unelevated update starts PowerShell directly instead of a batch file in the temp folder");
        string script = UninstallCommandTests.Decode(captured!.Arguments);
        UninstallCommandTests.Parse(script);
        Require(script.Contains("Invoke-UpdateImagePublication") && script.Contains("ShowReport = $true"),
            "The unelevated helper carries the publication script and shows its recovery report on failure");
        Require(script.Contains("StartTime.ToUniversalTime().Ticks -ne") && script.Contains("WaitForExit(60000)") && !script.Contains("Get-Process -Id"),
            "The unelevated helper waits for this process by id and start time, for at most a minute, so a reused id cannot hold it forever");
        Require(File.ReadAllBytes(download).SequenceEqual(standIn), "Building the helper leaves the verified download untouched");

        // A per-user or portable copy run as administrator can write its own folder, so it takes the
        // same helper instead of the machine-only trusted path, which would refuse it.
        captured = null;
        try
        {
            UpdateService.StartSwap(download, "9.9.9", new UpdateService.Platform
            {
                CanWrite = _ => true,
                IsElevated = () => true,
                Start = start => { captured = start; return null; }
            });
        }
        catch (IOException) { }
        Require(captured is { Verb.Length: 0 } elevated && UninstallCommandTests.Decode(elevated.Arguments).Contains("Invoke-UpdateImagePublication"),
            "An elevated copy outside Program Files updates through the helper for its own folder");
        Console.WriteLine("PASS an unelevated update launches PowerShell with its whole helper in the command line.");
    }

    private sealed class Outcome
    {
        internal int Exit;
        internal byte[] Target = Array.Empty<byte>();
        internal bool Relaunched, ReportShown, DownloadLeft;
        public override string ToString() => "exit " + Exit + ", relaunched " + Relaunched + ", report shown " + ReportShown;
    }

    private static Outcome Case(string root, string name, byte[] download, byte[] verified, bool elevated = false, bool relaunchFails = false)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "KillerScan.exe");
        string path = Path.Combine(folder, "download.exe");
        File.WriteAllBytes(target, Original);
        File.WriteAllBytes(path, download);
        UpdateImagePublication.RememberVerified(path, Hash(verified), Signer);
        string relaunched = Path.Combine(folder, "relaunched.txt");
        string shown = Path.Combine(folder, "report-shown.txt");
        string overrides = (relaunchFails ? "$ops.Relaunch = { param($plan) throw 'The shell belongs to another account.' }\n"
                : "$ops.Relaunch = { param($plan) [IO.File]::WriteAllText('" + relaunched + "', 'yes') }\n") +
            "$ops.ShowReport = { param($path) [IO.File]::WriteAllText('" + shown + "', $path) }\n" +
            "$ops.Publisher = { param($path, $plan) $plan.Thumb -eq '" + Signer + "' }";
        // The elevated publication never opens a report window; TrustedUpdate builds it without one.
        string script = UpdateImagePublication.Script(path, target, elevated, "https://example.invalid/releases", "KillerScan",
            showReport: !elevated, overrides: overrides);
        using var process = Process.Start(UpdateService.HelperStart(script)) ?? throw new IOException("The update helper did not start.");
        if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException("The update helper did not finish."); }
        return new Outcome { Exit = process.ExitCode, Target = File.ReadAllBytes(target), Relaunched = File.Exists(relaunched),
            ReportShown = File.Exists(shown), DownloadLeft = File.Exists(path) };
    }

    private static string Hash(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", "");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
