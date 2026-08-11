using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using KillerScan.Models;
using KillerScan.Services;

namespace KillerScan.Features
{
    // ============================================================
    // Command-line interface
    // ============================================================
    //
    // Dispatcher for the headless commands, invoked from App.OnStartup before any window exists.
    // A launch with no recognized command falls straight through to the normal GUI, so nothing
    // about the app's ordinary behavior changes.
    //
    // A scan run here goes through the SAME Services/NetworkScanner the window drives, with the
    // same vendor database and the same manual type overrides loaded, so the output is the device
    // list the GUI would have shown. Exports go through Services/ReportExport, so a CSV or HTML
    // report written from a script is byte-for-byte the one the export menu writes.
    //
    // Output is English whatever language the app is set to: the scanner's Localizer is left unset
    // (it falls back to English by design) and nothing here touches the WPF resource dictionaries,
    // which are not loaded this early.
    //
    // Console output rides on AttachConsole, because this is a GUI-subsystem exe. That has one
    // consequence worth knowing: cmd.exe hands the prompt back before the scan finishes, so a
    // script that needs the exit code has to wait for the process explicitly. The help text says so.
    //
    // Progress goes to stderr and results go to stdout, so `KillerScan.exe /scan > hosts.txt`
    // captures the table and nothing else.
    //
    // Exit codes: 0 = success, 1 = scan failed, 2 = bad usage.
    internal static class CliRunner
    {
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        /// <summary>Options that consume the next argument as their value.</summary>
        private static readonly string[] ValueOptions = ["/export", "--export"];

        /// <summary>
        /// Entry point for all CLI commands. Returns false when the arguments carry no recognized
        /// command (a normal GUI launch); otherwise runs the command and returns true with the
        /// process exit code set.
        /// </summary>
        internal static bool TryRunCli(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args is null || args.Length == 0) return false;

            string? command = args.FirstOrDefault(a =>
                IsHelp(a) || IsVersion(a) || Eq(a, "/scan") || Eq(a, "--scan"));
            if (command is null) return false;

            var (@out, err) = OpenConsole();

            if (IsHelp(command))    { @out.WriteLine(HelpText()); return true; }
            if (IsVersion(command)) { @out.WriteLine(AppInfo.Version); return true; }

            var (positionals, options) = ParseArgs(args, command);
            try
            {
                exitCode = RunScan(positionals, options, @out, err);
            }
            catch (Exception ex)
            {
                err.WriteLine("Error: " + Flatten(ex.Message));
                exitCode = 1;
            }
            return true;
        }

        // ---- /scan ----

        private static int RunScan(List<string> positionals, Dictionary<string, string> options,
                                   TextWriter @out, TextWriter err)
        {
            bool quiet = options.ContainsKey("/quiet") || options.ContainsKey("--quiet");
            bool quick = options.ContainsKey("/quick") || options.ContainsKey("--quick");
            string? exportPath = Value(options, "/export", "--export");

            // Several targets can arrive as separate arguments or as one comma-separated string;
            // joining with commas makes both read the same to the parser the subnet box uses.
            string targetText = string.Join(",", positionals);

            // No targets given: scan whatever the machine is attached to, which is what a bare
            // /scan is asking for.
            if (targetText.Length == 0)
            {
                var local = LocalNetwork.Detect();
                if (local is null)
                {
                    err.WriteLine("Error: no active IPv4 network was found. Give a target, for example: /scan 192.168.1.0/24");
                    return 2;
                }
                targetText = local.Subnet;
            }
            else
            {
                // A target list was given, so nothing has published the gateway and DNS addresses
                // the classifier uses. Detect is best-effort here: it is fine for it to find
                // nothing, the classifier just loses two of its hints.
                LocalNetwork.Detect();
            }

            var parsed = ScanTargets.Parse(targetText);
            if (!parsed.Ok)
            {
                err.WriteLine(parsed.Error switch
                {
                    TargetError.TooLarge =>
                        $"Error: that is {parsed.Detail} addresses, and the ceiling is {ScanTargets.MaxAddresses:N0}.",
                    TargetError.Invalid =>
                        $"Error: \"{parsed.Detail}\" is not a target. Use a CIDR block, a single host, or a range.",
                    _ => "Error: no target given.",
                });
                return 2;
            }

            if (exportPath != null && exportPath.Length == 0)
            {
                err.WriteLine("Error: /export needs a file path.");
                return 2;
            }

            // The vendor database and the manual type overrides are what make the output match the
            // window's. Without them every row would read "Unknown" with no vendor.
            OuiLookup.Load();
            DeviceOverrides.Load();

            using var cts = new CancellationTokenSource();
            ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
            try { Console.CancelKeyPress += onCancel; } catch { /* no console attached */ }

            if (!quiet) err.WriteLine($"Scanning {parsed.Summary} ({parsed.Addresses.Count:N0} addresses)...");

            var scanner = new NetworkScanner();
            List<NetworkDevice> devices;
            try
            {
                devices = scanner.ScanSubnetAsync(targetText, cts.Token, fullScan: !quick)
                                 .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                err.WriteLine("Scan cancelled.");
                return 1;
            }
            finally
            {
                try { Console.CancelKeyPress -= onCancel; } catch { }
            }

            devices = devices.OrderBy(d => d.IpSortKey).ToList();

            if (!quiet)
            {
                @out.WriteLine(Table(devices));
                @out.WriteLine();
                @out.WriteLine($"{devices.Count} device{(devices.Count == 1 ? "" : "s")} found on {parsed.Summary}.");
            }

            if (exportPath != null)
            {
                try
                {
                    bool html = exportPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                             || exportPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);
                    string body = html
                        ? ReportExport.BuildHtml(devices, parsed.Summary, "dark")
                        : ReportExport.BuildCsv(devices);

                    var dir = Path.GetDirectoryName(Path.GetFullPath(exportPath));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
                    File.WriteAllText(exportPath, body, Encoding.UTF8);
                    if (!quiet) err.WriteLine($"Wrote {Path.GetFullPath(exportPath)}");
                }
                catch (Exception ex)
                {
                    err.WriteLine("Error: could not write the export - " + Flatten(ex.Message));
                    return 1;
                }
            }

            return 0;
        }

        // ---- Table ----

        /// <summary>Fixed-width table sized to the data, so columns line up in a terminal and stay
        /// readable when the output is piped into a file.</summary>
        private static string Table(List<NetworkDevice> devices)
        {
            string[] headers = ["IP ADDRESS", "HOSTNAME", "MAC ADDRESS", "VENDOR", "TYPE", "OPEN PORTS"];
            var rows = devices.Select(d => new[]
            {
                d.IpAddress,
                d.Hostname,
                d.MacAddress,
                Clip(d.Vendor, 32),
                d.DeviceType,
                d.OpenPortsDisplay,
            }).ToList();

            var width = new int[headers.Length];
            for (int c = 0; c < headers.Length; c++)
            {
                width[c] = headers[c].Length;
                foreach (var r in rows) width[c] = Math.Max(width[c], (r[c] ?? "").Length);
            }

            var sb = new StringBuilder();
            AppendRow(sb, headers, width);
            AppendRow(sb, width.Select(w => new string('-', w)).ToArray(), width);
            foreach (var r in rows) AppendRow(sb, r, width);
            return sb.ToString().TrimEnd();
        }

        private static void AppendRow(StringBuilder sb, string[] cells, int[] width)
        {
            for (int c = 0; c < cells.Length; c++)
            {
                // No trailing padding on the last column: it only produces ragged whitespace in a
                // captured file.
                if (c == cells.Length - 1) sb.Append(cells[c] ?? "");
                else sb.Append((cells[c] ?? "").PadRight(width[c])).Append("  ");
            }
            sb.AppendLine();
        }

        /// <summary>Truncates with a plain ASCII ellipsis. Console code pages are not reliably
        /// UTF-8, and a mangled character in a piped file is worse than three dots.</summary>
        private static string Clip(string? s, int max)
        {
            s ??= "";
            return s.Length <= max ? s : s.Substring(0, max - 3) + "...";
        }

        // ---- Help ----

        private static string HelpText() => string.Join(Environment.NewLine,
        [
            $"KillerScan {AppInfo.Version} - command line usage",
            "",
            "  KillerScan.exe                              open the app",
            "  KillerScan.exe /scan                        scan the detected local subnet",
            "  KillerScan.exe /scan <targets>              scan the given targets",
            "  KillerScan.exe /version | -v                print the version",
            "  KillerScan.exe /help | -h | /?              this text",
            "",
            "Options for /scan:",
            "  /export <path>   write the results to a file. A .html or .htm path gives the full",
            "                   interactive report; anything else gives CSV.",
            "  /quick           discovery sweep only. Skips the deep port and fingerprint probe, so",
            "                   it finishes far faster and the device type is a guess from the MAC",
            "                   vendor alone.",
            "  /quiet           print nothing. Use it with /export, and read the exit code.",
            "",
            "Targets are written exactly as in the subnet box: a CIDR block (192.168.1.0/24), a",
            "single host (192.168.1.7), or a range, either in full (192.168.1.10-192.168.1.50) or",
            "with just the last octet on the right (192.168.1.10-50). Several can be given at once,",
            "separated by commas or passed as separate arguments; overlapping targets are counted",
            $"once and the combined ceiling is {ScanTargets.MaxAddresses:N0} addresses.",
            "",
            "  KillerScan.exe /scan 192.168.1.0/24 /export hosts.csv",
            "  KillerScan.exe /scan 10.0.0.10-50, 10.0.1.0/24 /quick",
            "  KillerScan.exe /scan /export report.html /quiet",
            "",
            "Ctrl+C stops a scan in progress. Progress goes to stderr and the table to stdout, so",
            "redirecting stdout captures the results and nothing else.",
            "",
            "Exit codes: 0 success, 1 scan failed or cancelled, 2 bad usage.",
            "",
            "Runs headless, works while the KillerScan window is open, and prints in English",
            "whatever language the app is set to.",
            "",
            "KillerScan is a GUI program, so cmd.exe hands the prompt back before the scan",
            "finishes. A script that needs to wait for the exit code should launch it as",
            "`start /wait KillerScan.exe /scan` in cmd, or",
            "`Start-Process -Wait KillerScan.exe -ArgumentList '/scan'` in PowerShell.",
        ]);

        // ---- Argument plumbing ----

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static bool IsHelp(string a) =>
            Eq(a, "/help") || Eq(a, "--help") || Eq(a, "-h") || Eq(a, "/?") || Eq(a, "-?");

        private static bool IsVersion(string a) =>
            Eq(a, "/version") || Eq(a, "--version") || Eq(a, "-v");

        /// <summary>First non-null value among the given option spellings; null when none was
        /// given. An option present with no value comes back as an empty string, which the caller
        /// treats as a usage error rather than silently ignoring it.</summary>
        private static string? Value(Dictionary<string, string> options, params string[] names)
        {
            foreach (var n in names)
                if (options.TryGetValue(n, out var v)) return v;
            return null;
        }

        /// <summary>
        /// Splits the arguments into positionals (the scan targets) and an option dictionary.
        /// Anything starting with / or - is an option, which is why a target can never begin with
        /// either character - no IPv4 target does.
        /// </summary>
        private static (List<string> Positionals, Dictionary<string, string> Options)
            ParseArgs(string[] args, string command)
        {
            var positionals = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int start = Array.FindIndex(args, a => Eq(a, command)) + 1;
            for (int i = start; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("/", StringComparison.Ordinal) ||
                    a.StartsWith("-", StringComparison.Ordinal))
                {
                    if (ValueOptions.Any(o => Eq(o, a)))
                        options[a] = i + 1 < args.Length ? args[++i] : string.Empty;
                    else
                        options[a] = string.Empty;
                }
                else
                {
                    // A trailing comma is how a shell splits "10.0.0.0/24, 10.0.1.0/24" into two
                    // arguments; the join puts it back, so drop the duplicate separator here.
                    positionals.Add(a.TrimEnd(','));
                }
            }
            return (positionals, options);
        }

        // ---- Console ----

        /// <summary>
        /// Attaches to the launching console and returns writers for stdout and stderr. Both are
        /// TextWriter.Null when there is no console to attach to (launched from Explorer, or from a
        /// scheduler), so a headless run with /export still works with nowhere to print.
        /// </summary>
        private static (TextWriter Out, TextWriter Error) OpenConsole()
        {
            try
            {
                if (AttachConsole(AttachParentProcess))
                {
                    var @out = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                    var err  = new StreamWriter(Console.OpenStandardError())  { AutoFlush = true };
                    Console.SetOut(@out);
                    Console.SetError(err);
                    return (@out, err);
                }
            }
            catch { }
            return (TextWriter.Null, TextWriter.Null);
        }

        private static string Flatten(string? s) =>
            (s ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
    }
}
