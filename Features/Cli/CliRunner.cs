using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KillerScan.Models;
using KillerScan.Services;
using KillerScan.Engine.SpeedTest;

namespace KillerScan.Features.Cli
{
    // Headless commands use the same scanner, classifier, overrides, OUI database and report
    // writer as the GUI. Progress is stderr; result data is stdout.
    internal static class CliRunner
    {
        private const int AttachParentProcess = -1;
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetFileType(IntPtr hFile);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
        private const int StdOutputHandle = -11;
        private const int StdErrorHandle = -12;
        private const uint FileTypeDisk = 0x0001;
        private const uint FileTypePipe = 0x0003;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        // ANSI color state, per stream. True only when that stream is a real interactive console
        // (not redirected or piped) whose VT processing could be enabled - so CSV/JSON piped to a
        // file, and everything written by /export, stays free of escape codes.
        private static bool _outAnsi, _errAnsi;

        private const string AnsiReset = "\x1b[0m";
        private const string AnsiIp = "\x1b[96m";      // bright cyan
        private const string AnsiHost = "\x1b[97m";    // bright white
        private const string AnsiMac = "\x1b[93m";     // bright yellow
        private const string AnsiType = "\x1b[95m";    // bright magenta
        private const string AnsiPorts = "\x1b[92m";   // bright green
        private const string AnsiGood = "\x1b[92m";    // bright green
        private const string AnsiBad = "\x1b[91m";     // bright red
        private const string AnsiDim = "\x1b[90m";
        // Brand prefix on the scan/probe status line, so a driving process (Cojack, etc.) or a
        // human watching the console knows which tool is running. Kept on stderr - the CSV/JSON/
        // HTML pipe is untouched. "Killer" in bright white, "Scan" in the app's orange from the
        // logo (256-color 208), then a divider. Both halves fall back to plain text on a console
        // without VT support.
        private const string AnsiBrandKiller = "\x1b[97m";
        private const string AnsiBrandScan   = "\x1b[38;5;208m";
        private static string BrandPrefix(bool ansi) =>
            ansi ? AnsiBrandKiller + "Killer" + AnsiBrandScan + "Scan" + AnsiReset + " | "
                 : "KillerScan | ";

        /// <summary>Returns true when the given std stream is an interactive console; enables VT
        /// escape processing on it so ANSI colors render (a no-op on Windows Terminal, required
        /// on classic conhost).</summary>
        private static bool TryEnableVt(int stdHandle)
        {
            try
            {
                IntPtr handle = GetStdHandle(stdHandle);
                if (handle == IntPtr.Zero || !GetConsoleMode(handle, out uint mode)) return false;
                return (mode & EnableVirtualTerminalProcessing) != 0
                    || SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
            }
            catch { return false; }
        }

        private static bool IsRedirected(int stdHandle)
        {
            try
            {
                IntPtr handle = GetStdHandle(stdHandle);
                if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
                uint type = GetFileType(handle);
                return type == FileTypeDisk || type == FileTypePipe;
            }
            catch { return false; }
        }

        private static string PaintErr(string text, string code) => _errAnsi ? code + TerminalText.Escape(text) + AnsiReset : TerminalText.Escape(text);

        /// <summary>Little stderr spinner shown while a scan or probe runs on an interactive
        /// console. Skipped when stderr is redirected (a \r animation would be garbage in a log),
        /// and when /quiet or /progress already own the status stream.</summary>
        private sealed class Spinner : IDisposable
        {
            private static readonly char[] Frames = ['|', '/', '-', '\\'];
            private readonly TextWriter _err;
            private readonly string _label;
            private readonly int _visibleLength;
            private readonly Thread _thread;
            private volatile bool _running = true;

            /// <summary>The label may carry ANSI color codes; visibleLength is its on-screen
            /// character count, used to wipe the line without wrapping past it.</summary>
            internal Spinner(TextWriter err, string label, int visibleLength)
            {
                _err = err;
                _label = label;
                _visibleLength = visibleLength;
                _thread = new Thread(Run) { IsBackground = true };
                _thread.Start();
            }

            private void Run()
            {
                int frame = 0;
                while (_running)
                {
                    try { _err.Write($"\r{AnsiMac}{Frames[frame++ % Frames.Length]}{AnsiReset} {_label}"); } catch { return; }
                    Thread.Sleep(120);
                }
            }

            public void Dispose()
            {
                _running = false;
                try { _thread.Join(300); } catch { }
                // Wipe the spinner line so the results start on a clean row.
                try { _err.Write("\r" + new string(' ', _visibleLength + 2) + "\r"); } catch { }
            }
        }

        private static readonly string[] ValueOptions =
        [
            "/export", "--export", "/output", "--output", "/format", "--format",
            "/sort", "--sort", "/filter", "--filter", "/type", "--type",
            "/vendor-filter", "--vendor-filter", "/ports", "--ports", "/limit", "--limit",
            "/timeout", "--timeout", "/theme", "--theme", "/count", "--count",
            "/interval", "--interval", "/max-hops", "--max-hops", "/location", "--location", "/skip", "--skip",
            "/adapter", "--adapter"
        ];

        /// <summary>
        /// Finds the Location a command line names, by ID or by name in any letter case. Returns
        /// null with a usage message when nothing matches or more than one Location shares the name.
        /// </summary>
        internal static LocationInfo? ResolveLocation(string text, IReadOnlyList<LocationInfo> items, out string? error)
        {
            error = null;
            string wanted = text.Trim();
            if (wanted.Length == 0) { error = "/location needs a Location name."; return null; }
            if (items.FirstOrDefault(item => string.Equals(item.Id, wanted, StringComparison.OrdinalIgnoreCase)) is { } byId) return byId;
            var named = items.Where(item => string.Equals(item.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (named.Count == 1) return named[0];
            error = named.Count == 0
                ? $"no Location is named \"{wanted}\"."
                : $"more than one Location is named \"{wanted}\". Use its ID instead: {string.Join(", ", named.Select(item => item.Id))}.";
            return null;
        }

        /// <summary>The connected adapter /adapter names, by Windows name or address, or null with a message.</summary>
        internal static ScanBinding? ResolveAdapter(string text, IReadOnlyList<ScanBinding> available, out string? error)
        {
            error = null;
            string wanted = text.Trim();
            var found = System.Net.IPAddress.TryParse(wanted, out var address)
                ? available.FirstOrDefault(b => b.LocalAddress.Equals(address))
                : available.FirstOrDefault(b => string.Equals(b.InterfaceName, wanted, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;
            error = $"no connected adapter is named \"{wanted}\"." + (available.Count == 0 ? " No adapters are connected."
                : " Connected: " + string.Join(", ", available.Select(b => $"{b.InterfaceName} ({b.LocalAddress}/{b.PrefixLength})")) + ".");
            return null;
        }

        /// <summary>
        /// True, with a message, when a target is neither on the bound adapter's network nor
        /// routed through it by Windows.
        /// </summary>
        internal static bool OffLinkTarget(ScanBinding binding, IEnumerable<System.Net.IPAddress> targets, out string? error)
        {
            foreach (var address in targets)
            {
                if (binding.Reaches(address, out string route)) continue;
                error = binding.Unreachable(address, route) + " Leave out /adapter to scan it.";
                return true;
            }
            error = null;
            return false;
        }

        /// <summary>
        /// The checks /skip turns off, by the same names Scan settings saves (dns, http, ...).
        /// No /skip means every check; an empty or unknown name is a usage error.
        /// </summary>
        internal static bool ParseSkip(string? value, out ScanOptions options, out string? error)
        {
            options = new ScanOptions();
            error = null;
            if (value == null) return true;
            try
            {
                options = options.Without(value);
                return true;
            }
            catch (ArgumentException)
            {
                var unknown = value.Split(',').Select(name => name.Trim()).FirstOrDefault(name =>
                    name.Length == 0 || !ScanOptions.Features.Contains(name.ToLowerInvariant()));
                error = (unknown is { Length: > 0 } ? $"\"{unknown}\" is not a check name." : "/skip needs check names.") +
                    " Use any of: " + string.Join(", ", ScanOptions.Features) + ".";
                return false;
            }
        }

        /// <summary>
        /// Records a finished command-line scan in its Location: one history entry, then the
        /// Known list. Status lines go to stderr; the in-app terminal reads the keyed form.
        /// </summary>
        internal static void RecordToLocation(LocationData location, string caption, string targets,
            IReadOnlyList<NetworkDevice> devices, bool quick, TextWriter err, bool quiet, bool keyed, ScanOptions? scanOptions = null,
            string adapter = "", ScanNetwork? network = null)
        {
            // A quick scan checks no ports, so it never becomes the baseline for a full one.
            var options = scanOptions ?? new ScanOptions();
            if (quick) options = options.Without("ports,http,tls,ssh");
            var history = location.History;
            if (!history.Record(caption, targets, devices, ScanCompletion.Complete, options, deep: false, adapter, network).Saved)
                err.WriteLine(PaintErr("Warning: the scan could not be saved to history - " + Flatten(history.SaveError), AnsiBad));
            if (devices.Count == 0) return;
            var seen = location.Devices.Observe(devices);
            if (!seen.Saved)
                err.WriteLine(PaintErr("Warning: known devices could not be saved - " + Flatten(location.Devices.SaveError), AnsiBad));
            if (keyed)
            {
                if (seen.BaselineCreated) err.WriteLine(ScanStatusText.Counted("Str_St_KnownBaseline", devices.Count) + "|" + devices.Count);
                else if (seen.NewDevices.Count > 0) err.WriteLine(ScanStatusText.Counted("Str_St_UnknownDevices", seen.NewDevices.Count) + "|" + seen.NewDevices.Count);
            }
            else if (!quiet)
            {
                if (seen.BaselineCreated) err.WriteLine(devices.Count == 1 ? "Baseline set: 1 device is now known. It is not approved yet."
                    : $"Baseline set: {devices.Count} devices are now known. None are approved yet.");
                else if (seen.NewDevices.Count > 0) err.WriteLine(seen.NewDevices.Count == 1 ? "Alert: 1 device is new since the baseline."
                    : $"Alert: {seen.NewDevices.Count} devices are new since the baseline.");
            }
        }

        private static readonly string[] FlagOptions =
        [
            "/terminal-progress",
            "/quick", "--quick", "/full", "--full", "/quiet", "--quiet",
            "/progress", "--progress", "/descending", "--descending", "/desc", "--desc",
            "/no-header", "--no-header", "/fail-empty", "--fail-empty", "/demo", "--demo",
            "/json", "--json", "/csv", "--csv", "/html", "--html", "/table", "--table"
        ];

        private static readonly string[] HtmlThemes =
        [
            "dark", "light", "black", "98se", "blood", "greed", "cyanotic", "ectoplasm",
            "decay", "malaise", "sepulchre", "delirium", "mourning"
        ];

        /// <summary>
        /// Where the uiscan command is, or -1. It has to come first, after any demo switch, because
        /// a later word can be a value: /location uiscan names a Location and must not open the app.
        /// </summary>
        internal static int UiScanIndex(string[] args)
        {
            int index = 0;
            while (index < args.Length && (Eq(args[index], "/demo") || Eq(args[index], "--demo"))) index++;
            return index < args.Length && (Eq(args[index], "/uiscan") || Eq(args[index], "--uiscan") || Eq(args[index], "uiscan")) ? index : -1;
        }

        internal static bool TryRunCli(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args is null || args.Length == 0) return false;
            args = KillerFamily.CommandLine.CommandLineEntry.GetArguments(args,
                ["scan", "probe", "network", "locations", "vendor", "ping", "trace", "diagnose", "watch", "speedtest", "version"], "--") ?? args;
            string? command = args.FirstOrDefault(a => IsHelp(a) || IsVersion(a) ||
                Eq(a, "/scan") || Eq(a, "--scan") || Eq(a, "/probe") || Eq(a, "--probe") ||
                Eq(a, "/network") || Eq(a, "--network") || Eq(a, "/locations") || Eq(a, "--locations") ||
                Eq(a, "/vendor") || Eq(a, "--vendor") ||
                Eq(a, "/ping") || Eq(a, "--ping") || Eq(a, "/trace") || Eq(a, "--trace") ||
                Eq(a, "/diagnose") || Eq(a, "--diagnose") || Eq(a, "/watch") || Eq(a, "--watch") ||
                Eq(a, "/speedtest") || Eq(a, "--speedtest"));
            if (command is null) return false;

            // CLI mode runs on the WPF dispatcher thread inside OnStartup, where a
            // DispatcherSynchronizationContext is current. The commands below block with
            // GetAwaiter().GetResult() while the scanner's awaits (no ConfigureAwait(false))
            // post their continuations back to that context - a guaranteed deadlock, seen in
            // the field as a /scan that prints its status line and then hangs forever with
            // /export never written. Dropping the context sends every continuation to the
            // thread pool instead. Safe here: the CLI never returns to the GUI - App.OnStartup
            // calls Shutdown as soon as this method returns.
            SynchronizationContext.SetSynchronizationContext(null);

            var (@out, err) = OpenConsole();
            if (IsHelp(command)) { @out.WriteLine(HelpText()); return true; }
            if (IsVersion(command)) { @out.WriteLine(AppInfo.Version); return true; }

            try
            {
                var (positionals, options, parseError) = ParseArgs(args, command);
                if (parseError != null) { err.WriteLine(PaintErr("Error: " + parseError, AnsiBad)); exitCode = 2; }
                else if (Eq(command, "/network") || Eq(command, "--network"))
                    exitCode = RunNetwork(positionals, options, @out, err);
                else if (Eq(command, "/locations") || Eq(command, "--locations"))
                    exitCode = RunLocations(positionals, options, @out, err);
                else if (Eq(command, "/vendor") || Eq(command, "--vendor"))
                    exitCode = RunVendor(positionals, options, @out, err);
                else if (Eq(command, "/ping") || Eq(command, "--ping"))
                    exitCode = RunPing(positionals, options, @out, err);
                else if (Eq(command, "/trace") || Eq(command, "--trace"))
                    exitCode = RunTrace(positionals, options, @out, err);
                else if (Eq(command, "/diagnose") || Eq(command, "--diagnose"))
                    exitCode = RunDiagnose(positionals, options, @out, err);
                else if (Eq(command, "/watch") || Eq(command, "--watch"))
                    exitCode = RunWatch(positionals, options, @out, err);
                else if (Eq(command, "/speedtest") || Eq(command, "--speedtest"))
                    exitCode = RunSpeedTest(positionals, options, @out, err);
                else
                    exitCode = RunDevices(positionals, options, @out, err,
                        probe: Eq(command, "/probe") || Eq(command, "--probe"));
            }
            catch (Exception ex) { err.WriteLine(PaintErr("Error: " + Flatten(ex.Message), AnsiBad)); exitCode = 1; }
            return true;
        }

        private static int RunNetwork(List<string> positionals, Dictionary<string, string> options,
                                      TextWriter @out, TextWriter err)
        {
            if (positionals.Count > 0 || options.Count > 0) return Usage(err, "/network takes no options or targets.");
            var net = LocalNetwork.Detect();
            if (net is null) return Usage(err, "no active IPv4 network was found.", 1);
            @out.WriteLine("INTERFACE  " + TerminalText.Escape(net.InterfaceLabel));
            @out.WriteLine("LOCAL IP   " + net.LocalIp);
            @out.WriteLine("SUBNET     " + net.Subnet);
            @out.WriteLine("GATEWAY    " + (net.Gateway.Length == 0 ? "-" : net.Gateway));
            @out.WriteLine("DNS        " + (net.Dns.Length == 0 ? "-" : net.Dns));
            return 0;
        }

        /// <summary>
        /// Lists the saved Locations so a script or agent can find the name or ID /location takes.
        /// Notes are left out: they are free text, and nothing here needs them.
        /// </summary>
        internal static int RunLocations(List<string> positionals, Dictionary<string, string> options,
                                         TextWriter @out, TextWriter err)
        {
            if (positionals.Count > 0 || !Only(options, "/json", "--json"))
                return Usage(err, "/locations takes no targets and accepts /json.");
            var found = LocationCatalog.ReadExisting(LocationService.Root, out string? error);
            if (error != null) return Usage(err, "Locations are unavailable - " + Flatten(error), 1);
            var items = found.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (Has(options, "/json", "--json"))
            {
                @out.WriteLine(JsonSerializer.Serialize(items.Select(item => new
                {
                    id = item.Id, name = item.Name, customer = item.Customer,
                    lastTargets = item.LastTargets, lastAdapter = item.LastAdapter
                })));
                return 0;
            }
            if (items.Count == 0) { @out.WriteLine("No Locations yet."); return 0; }
            string Row(string name, string id, string customer, string targets) =>
                name.PadRight(items.Max(item => TerminalText.Escape(item.Name).Length) + 2) + id.PadRight(34) + customer.PadRight(items.Max(item => TerminalText.Escape(item.Customer).Length) + 2) + targets;
            @out.WriteLine(Row("NAME", "ID", "CUSTOMER", "LAST TARGETS").TrimEnd());
            foreach (var item in items)
                @out.WriteLine(Row(TerminalText.Escape(item.Name), item.Id, TerminalText.Escape(item.Customer), TerminalText.Escape(item.LastTargets)).TrimEnd());
            return 0;
        }

        private static int RunVendor(List<string> positionals, Dictionary<string, string> options,
                                     TextWriter @out, TextWriter err)
        {
            if (options.Count > 0 || positionals.Count != 1) return Usage(err, "/vendor needs exactly one MAC address.");
            string normalized = new([.. positionals[0].Where(Uri.IsHexDigit)]);
            if (normalized.Length != 12) return Usage(err, "the MAC address must contain 12 hex digits.");
            OuiLookup.Load();
            string vendor = OuiLookup.GetVendor(positionals[0]);
            @out.WriteLine(vendor.Length == 0 ? "Unknown" : TerminalText.Escape(vendor));
            return vendor.Length == 0 ? 3 : 0;
        }

        private sealed class PingSample
        {
            public int Sequence { get; set; }
            public string Address { get; set; } = "";
            public bool Success { get; set; }
            public long? LatencyMs { get; set; }
        }

        private sealed class TraceSample
        {
            public int Hop { get; set; }
            public string? Address { get; set; }
            public long? LatencyMs { get; set; }
            public bool Arrived { get; set; }
        }

        private static int RunPing(List<string> positionals, Dictionary<string, string> options,
                                   TextWriter @out, TextWriter err)
        {
            if (positionals.Count != 1 || !Only(options, "/count", "--count", "/json", "--json"))
                return Usage(err, "/ping needs one IPv4 address or hostname and accepts /count and /json.");
            if (!TryResolveIPv4(positionals[0], out var address)) return Usage(err, "the ping target could not be resolved.");
            int count = ParsePositive(options, "/count", "--count");
            if (count == -1 || count > 20) return Usage(err, "/count must be from 1 to 20.");
            if (count == 0) count = 4;
            var replies = new List<PingSample>();
            for (int i = 1; i <= count; i++)
            {
                long? latency = ConnectionChecks.PingAsync(address).GetAwaiter().GetResult();
                replies.Add(new PingSample { Sequence = i, Address = address.ToString(), Success = latency.HasValue, LatencyMs = latency });
            }
            int received = replies.Count(reply => reply.Success);
            var result = new { target = positionals[0], address = address.ToString(), sent = count, received,
                lossPercent = 100d * (count - received) / count, replies };
            if (Has(options, "/json", "--json")) @out.WriteLine(JsonSerializer.Serialize(result));
            else
            {
                foreach (var reply in replies) @out.WriteLine(reply.Success ? $"Reply from {address}: {reply.LatencyMs} ms" : $"No reply from {address}");
                @out.WriteLine($"Sent {count}, received {received}, lost {count - received} ({result.lossPercent:0.#}%).");
            }
            return received > 0 ? 0 : 3;
        }

        private static int RunTrace(List<string> positionals, Dictionary<string, string> options,
                                    TextWriter @out, TextWriter err)
        {
            if (positionals.Count != 1 || !Only(options, "/max-hops", "--max-hops", "/json", "--json"))
                return Usage(err, "/trace needs one IPv4 address or hostname and accepts /max-hops and /json.");
            if (!TryResolveIPv4(positionals[0], out var address)) return Usage(err, "the trace target could not be resolved.");
            int maxHops = ParsePositive(options, "/max-hops", "--max-hops");
            if (maxHops == -1 || maxHops > 64) return Usage(err, "/max-hops must be from 1 to 64.");
            if (maxHops == 0) maxHops = 30;
            var hops = new List<TraceSample>();
            ConnectionChecks.TraceAsync(address, maxHops, hop => hops.Add(new TraceSample
            {
                Hop = hop.Ttl, Address = hop.Address?.ToString(), LatencyMs = hop.Latency, Arrived = hop.Arrived
            }), CancellationToken.None).GetAwaiter().GetResult();
            if (Has(options, "/json", "--json")) @out.WriteLine(JsonSerializer.Serialize(new { target = positionals[0], address = address.ToString(), hops }));
            else foreach (var hop in hops) @out.WriteLine($"{hop.Hop,2}  {hop.Address ?? "*"}  {(hop.LatencyMs.HasValue ? hop.LatencyMs + " ms" : "*")}");
            return hops.Any(hop => hop.Arrived) ? 0 : 3;
        }

        private static int RunDiagnose(List<string> positionals, Dictionary<string, string> options,
                                       TextWriter @out, TextWriter err)
        {
            if (positionals.Count != 1 || !Only(options, "/ports", "--ports", "/json", "--json"))
                return Usage(err, "/diagnose needs one IPv4 address or hostname and accepts /ports and /json.");
            if (!TryResolveIPv4(positionals[0], out var address)) return Usage(err, "the diagnostic target could not be resolved.");
            int[] ports = ParsePorts(Value(options, "/ports", "--ports")) ?? [22, 80, 443, 445, 3389];
            if (ports.Length == 0) return Usage(err, "/ports must be comma-separated numbers from 1 to 65535.");
            string reverse = "";
            try { reverse = Dns.GetHostEntry(address).HostName; } catch { }
            long? ping = ConnectionChecks.PingAsync(address).GetAwaiter().GetResult();
            var route = ConnectionChecks.Route(address);
            var checks = ports.Select(port => new { port, open = ConnectionChecks.TcpAsync(address, port, CancellationToken.None).GetAwaiter().GetResult() }).ToArray();
            var result = new { target = positionals[0], address = address.ToString(), reverseDns = reverse,
                pingMs = ping, route = route?.Interface, nextHop = route?.NextHop, ports = checks };
            if (Has(options, "/json", "--json")) @out.WriteLine(JsonSerializer.Serialize(result));
            else
            {
                @out.WriteLine("ADDRESS     " + address);
                @out.WriteLine("REVERSE DNS " + (reverse.Length == 0 ? "-" : TerminalText.Escape(reverse)));
                @out.WriteLine("PING        " + (ping.HasValue ? ping + " ms" : "no reply"));
                @out.WriteLine("ROUTE       " + TerminalText.Escape((route?.Interface ?? "-") + (string.IsNullOrEmpty(route?.NextHop) ? "" : " via " + route?.NextHop)));
                foreach (var check in checks) @out.WriteLine($"TCP {check.port,-5} {(check.open ? "open" : "closed")}");
            }
            return ping.HasValue || checks.Any(check => check.open) ? 0 : 3;
        }

        private static int RunWatch(List<string> positionals, Dictionary<string, string> options,
                                    TextWriter @out, TextWriter err)
        {
            if (positionals.Count == 0 || !Only(options, "/count", "--count", "/interval", "--interval", "/json", "--json"))
                return Usage(err, "/watch needs one to 16 IPv4 addresses and accepts /count, /interval, and /json.");
            if (!ConnectionChecks.TryTargets(string.Join(",", positionals), out var targets))
                return Usage(err, "/watch needs one to 16 IPv4 addresses.");
            int count = ParsePositive(options, "/count", "--count");
            int interval = ParsePositive(options, "/interval", "--interval");
            if (count == -1 || count > 60) return Usage(err, "/count must be from 1 to 60.");
            if (interval == -1 || interval > 60) return Usage(err, "/interval must be from 1 to 60 seconds.");
            if (count == 0) count = 4;
            if (interval == 0) interval = 1;
            var samples = new List<PingSample>();
            for (int sequence = 1; sequence <= count; sequence++)
            {
                foreach (var target in targets)
                {
                    long? latency = ConnectionChecks.PingAsync(target).GetAwaiter().GetResult();
                    samples.Add(new PingSample { Sequence = sequence, Address = target.ToString(), Success = latency.HasValue, LatencyMs = latency });
                }
                if (sequence < count) Thread.Sleep(TimeSpan.FromSeconds(interval));
            }
            if (Has(options, "/json", "--json")) @out.WriteLine(JsonSerializer.Serialize(new { count, intervalSeconds = interval, samples }));
            else foreach (var sample in samples) @out.WriteLine($"{sample.Sequence,2}  {sample.Address,-15}  {(sample.Success ? sample.LatencyMs + " ms" : "no reply")}");
            return samples.Any(sample => sample.Success) ? 0 : 3;
        }

        private static int RunSpeedTest(List<string> positionals, Dictionary<string, string> options,
                                        TextWriter @out, TextWriter err)
        {
            if (positionals.Count > 0 || !Only(options, "/json", "--json"))
                return Usage(err, "/speedtest takes no target and accepts /json.");
            var result = new SpeedTestEngine().RunAsync(new SpeedTestOptions(), null, CancellationToken.None).GetAwaiter().GetResult();
            var output = new { endpoint = result.Endpoint.ToString(), downloadMbps = result.Download.Mbps,
                uploadMbps = result.Upload.Mbps, idleLatencyMs = result.IdleLatencyMs, jitterMs = result.JitterMs,
                downloadLoadedLatencyMs = result.Download.LoadedLatencyMs, uploadLoadedLatencyMs = result.Upload.LoadedLatencyMs,
                elapsedSeconds = result.Elapsed.TotalSeconds };
            if (Has(options, "/json", "--json")) @out.WriteLine(JsonSerializer.Serialize(output));
            else
            {
                @out.WriteLine($"Download  {output.downloadMbps:0.##} Mbps");
                @out.WriteLine($"Upload    {output.uploadMbps:0.##} Mbps");
                @out.WriteLine($"Latency   {output.idleLatencyMs:0.##} ms");
                @out.WriteLine($"Jitter    {output.jitterMs:0.##} ms");
            }
            return 0;
        }

        private static bool TryResolveIPv4(string target, out IPAddress address)
        {
            if (IPAddress.TryParse(target, out address!) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) return true;
            try
            {
                address = Dns.GetHostAddresses(target).First(candidate => candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                return true;
            }
            catch { address = IPAddress.None; return false; }
        }

        private static bool Only(Dictionary<string, string> options, params string[] allowed) =>
            options.Keys.All(key => allowed.Any(name => Eq(key, name)));

        private static int RunDevices(List<string> positionals, Dictionary<string, string> options,
                                      TextWriter @out, TextWriter err, bool probe)
        {
            bool quiet = Has(options, "/quiet", "--quiet");
            bool quick = Has(options, "/quick", "--quick");
            bool progress = Has(options, "/progress", "--progress");
            bool terminalProgress = Has(options, "/terminal-progress");
            bool noHeader = Has(options, "/no-header", "--no-header");
            bool failEmpty = Has(options, "/fail-empty", "--fail-empty");
            string? exportPath = Value(options, "/export", "--export", "/output", "--output");
            var (format, formatFromExtension) = ResolveFormat(options, Value(options, "/format", "--format"), exportPath);
            if (format.Length == 0) return Usage(err, "format must be table, csv, json, or html, with only one format selected.");
            if (exportPath != null && exportPath.Length == 0) return Usage(err, "/export needs a file path.");
            IPAddress? probeIp = null;
            if (probe && positionals.Count != 1) return Usage(err, "/probe needs exactly one IPv4 address.");
            if (probe && (!IPAddress.TryParse(positionals[0], out probeIp) || probeIp.GetAddressBytes().Length != 4))
                return Usage(err, "/probe currently accepts an IPv4 address, not a hostname.");

            // Screenshot mode: same fabricated-network generator the GUI's --demo uses, so the
            // console output never shows a real environment. Scan only; no network is touched.
            bool demo = Has(options, "/demo", "--demo");
            if (demo && probe) return Usage(err, "/demo works with /scan only.");
            DemoScan? demoScan = demo ? DemoData.Generate(new Random()) : null;

            string targetText = string.Join(",", positionals);
            ScanTargetResult? parsed = null;
            if (!probe && !demo)
            {
                if (targetText.Length == 0)
                {
                    var local = LocalNetwork.Detect();
                    if (local is null) return Usage(err, "no active IPv4 network was found. Give a target such as /scan 192.168.1.0/24.");
                    targetText = local.Subnet;
                }
                else LocalNetwork.Detect();
                parsed = ScanTargets.Parse(targetText);
                if (!parsed.Ok) return Usage(err, parsed.Error == TargetError.TooLarge
                    ? $"that is {parsed.Detail} addresses; the ceiling is {ScanTargets.MaxAddresses:N0}."
                    : $"\"{parsed.Detail}\" is not a CIDR block, host, or range.");
            }

            int timeout = ParsePositive(options, "/timeout", "--timeout");
            if (timeout == -1) return Usage(err, "/timeout must be a positive number of seconds.");
            int limit = ParsePositive(options, "/limit", "--limit");
            if (limit == -1) return Usage(err, "/limit must be a positive integer.");
            string theme = Value(options, "/theme", "--theme") ?? "dark";
            if (!HtmlThemes.Contains(theme, StringComparer.OrdinalIgnoreCase))
                return Usage(err, "/theme must name one of: " + string.Join(", ", HtmlThemes) + ".");
            int[]? ports = ParsePorts(Value(options, "/ports", "--ports"));
            if (ports != null && ports.Length == 0) return Usage(err, "/ports must be comma-separated numbers from 1 to 65535.");

            if (!ParseSkip(Value(options, "/skip", "--skip"), out var scanOptions, out string? skipError))
                return Usage(err, skipError!);

            // With /adapter every probe leaves through that adapter, and only its own network can
            // be scanned. Without it Windows picks the route, as before.
            ScanBinding? binding = null;
            if (Value(options, "/adapter", "--adapter") is { } adapterText)
            {
                if (demo) return Usage(err, "/adapter does not work with /demo.");
                binding = ResolveAdapter(adapterText, ScanBinding.Available(), out string? adapterError);
                if (binding == null) return Usage(err, adapterError!);
                var reach = probe ? [probeIp!] : parsed!.Addresses;
                if (OffLinkTarget(binding, reach, out string? offLink)) return Usage(err, offLink!);
            }

            // With /location the run reads that Location's names and types and records there.
            // Without it, it is a Temporary scan: nothing saved is read and nothing is written.
            string? locationText = Value(options, "/location", "--location");
            LocationData? location = null;
            if (locationText != null)
            {
                if (demo) return Usage(err, "/location does not work with /demo.");
                if (!LocationService.Loaded) LocationService.Load("Unassigned");
                if (LocationService.LoadError is { } locationError)
                    return Usage(err, "Locations are unavailable - " + Flatten(locationError), 1);
                var info = ResolveLocation(locationText, LocationService.Items, out string? resolveError);
                if (info == null) return Usage(err, resolveError!);
                location = LocationService.Get(info);
            }

            OuiLookup.Load();
            using var cts = new CancellationTokenSource();
            if (timeout > 0) cts.CancelAfter(TimeSpan.FromSeconds(timeout));
            void OnCancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; cts.Cancel(); }
            try { Console.CancelKeyPress += OnCancel; } catch { }

            var scanner = new NetworkScanner
            {
                ManualTypes = mac => location?.Overrides.Get(mac),
                Completed = device => location?.Devices.Apply(device),
                Binding = binding
            };
            if (terminalProgress)
            {
                int found = 0;
                scanner.DeviceFound += _ => err.WriteLine("Devices: " + Interlocked.Increment(ref found));
            }
            int lastProgress = -1;
            if (progress && !quiet)
            {
                // The in-app terminal reads key|argument lines and localizes them itself.
                scanner.StatusChanged += s => err.WriteLine(terminalProgress ? ScanStatusText.Protocol(s) : s.ToString());
                scanner.ProgressChanged += p =>
                {
                    if (p == 100 || p >= lastProgress + 5)
                    {
                        lastProgress = p;
                        string percent = $"{p}%";
                        if (_errAnsi && !terminalProgress) percent = "\x1b[94m" + percent + AnsiReset;
                        err.WriteLine("Progress: " + percent);
                    }
                };
            }
            string target = probe ? positionals[0] : demo ? demoScan!.Subnet : parsed!.Summary;
            string suffix = probe ? "..." : $" ({(demo ? 254 : parsed!.Addresses.Count):N0} addresses)...";
            string verb = probe ? "deep-probing " : "scanning ";
            // Interactive console gets the animated spinner on the same line, with the target in
            // the IP color and the spinner glyph in the MAC color; a redirected stderr gets the
            // plain one-shot line so logs stay clean. Prefixed with the BrandPrefix so a driving
            // process (Cojack, etc.) that reads stderr can see which tool is running the scan.
            Spinner? spinner = null;
            if (!quiet && !progress)
            {
                if (_errAnsi)
                    spinner = new Spinner(err, BrandPrefix(true) + verb + AnsiIp + target + AnsiReset + suffix,
                        (BrandPrefix(false) + verb + target + suffix).Length);
                else err.WriteLine(BrandPrefix(false) + verb + target + suffix);
            }

            List<NetworkDevice> devices;
            if (demo)
            {
                // A short beat so the spinner is visible (and screenshottable), then the
                // fabricated device list - nothing on the wire.
                if (spinner != null) Thread.Sleep(1500);
                spinner?.Dispose(); spinner = null;
                try { Console.CancelKeyPress -= OnCancel; } catch { }
                devices = [.. demoScan!.Devices];
            }
            else
            try
            {
                Task<List<NetworkDevice>> operation = probe
                    ? ProbeAsListAsync(scanner, probeIp!.ToString(), scanOptions, cts.Token)
                    : scanner.ScanSubnetAsync(targetText, cts.Token, scanOptions, fullScan: !quick);
                if (timeout > 0)
                {
                    var deadline = Task.Delay(TimeSpan.FromSeconds(timeout));
                    if (Task.WhenAny(operation, deadline).GetAwaiter().GetResult() != operation)
                    {
                        cts.Cancel();
                        spinner?.Dispose(); spinner = null;
                        err.WriteLine(PaintErr($"Operation timed out after {timeout} seconds.", AnsiBad));
                        return 1;
                    }
                }
                devices = operation.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                spinner?.Dispose(); spinner = null;
                err.WriteLine(PaintErr(timeout > 0 ? $"Operation timed out after {timeout} seconds." : "Operation canceled.", AnsiBad));
                return 1;
            }
            finally
            {
                spinner?.Dispose();
                try { Console.CancelKeyPress -= OnCancel; } catch { }
            }

            // History and the Known list get everything the scan found, before display filters.
            if (location != null && !probe && !demo)
            {
                string adapter = binding?.InterfaceName ?? string.Empty;
                ScanNetwork network;
                try { network = ScanNetwork.Identify(targetText, adapter.Length > 0 ? adapter : ScanNetwork.RouteAdapter(targetText), ScanNetwork.Macs(devices)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { network = ScanNetwork.Unknown; }
                // The Location can be deleted in the app while this scan runs. Writing then would
                // recreate its folder as an orphan nobody can open, so the run is not recorded.
                if (location.Info is { } info && !LocationService.StillExists(info.Id))
                    err.WriteLine(PaintErr("Warning: Location \"" + info.Name + "\" was deleted during the scan, so the run was not recorded.", AnsiBad));
                else
                    RecordToLocation(location, parsed!.Summary, targetText, devices, quick, err, quiet, terminalProgress, scanOptions, adapter, network);
            }

            devices = ApplyFilters(devices, options, ports);
            var sorted = ApplySort(devices, Value(options, "/sort", "--sort"),
                Has(options, "/descending", "--descending", "/desc", "--desc"), err);
            if (sorted is null) return 2;
            devices = sorted;
            if (limit > 0) devices = [.. devices.Take(limit)];

            string label = probe ? positionals[0] : demo ? demoScan!.Subnet : parsed!.Summary;
            // The plain body is what exports and pipes receive; an interactive console gets the
            // colorized table instead. That includes the case where the format is csv/json/html
            // only because the /export extension picked it - the FILE gets that format, but raw
            // CSV on a live console helps nobody. An EXPLICIT /csv, /json or /format choice is
            // honored on stdout as-is (that is the scripting contract), just without colors.
            string body = Render(format, devices, label, theme, noHeader);
            bool showTable = _outAnsi && (format == "table" || formatFromExtension);
            // On a live console, device-chosen text (a hostname with ESC sequences) must not reach
            // the terminal as control codes, so an explicit format is shown with them written as
            // \xNN. Line breaks and tabs stay. Redirected output and exported files are untouched.
            if (!quiet)
                @out.WriteLine(showTable ? Table(devices, !noHeader, ansi: true) : _outAnsi ? ConsoleSafe(body) : body);
            if (exportPath != null)
            {
                try
                {
                    string full = Path.GetFullPath(exportPath);
                    string? dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    ExportFile.WriteText(full, body, new UTF8Encoding(false));
                    if (!quiet) err.WriteLine(PaintErr("Wrote " + full, AnsiGood));
                }
                catch (Exception ex) { err.WriteLine(PaintErr("Error: could not write the export - " + Flatten(ex.Message), AnsiBad)); return 1; }
            }
            if (!quiet)
            {
                err.WriteLine($"{devices.Count} matching device{(devices.Count == 1 ? "" : "s")}.");
                err.WriteLine(PaintErr("KillerScan complete.", AnsiGood));
                // Interactive consoles only.
                if (_errAnsi) err.WriteLine(PaintErr("Press ENTER to exit", AnsiDim));
            }
            return devices.Count == 0 && failEmpty ? 3 : 0;
        }

        private static async Task<List<NetworkDevice>> ProbeAsListAsync(
            NetworkScanner scanner, string ip, ScanOptions options, CancellationToken cancellationToken)
        {
            return [await scanner.DeepProbeHostAsync(ip, cancellationToken, options)];
        }

        private static List<NetworkDevice> ApplyFilters(List<NetworkDevice> devices,
            Dictionary<string, string> options, int[]? ports)
        {
            string? text = Value(options, "/filter", "--filter");
            string? type = Value(options, "/type", "--type");
            string? vendor = Value(options, "/vendor-filter", "--vendor-filter");
            IEnumerable<NetworkDevice> query = devices;
            if (!string.IsNullOrWhiteSpace(text)) query = query.Where(d => SearchText(d).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(type)) query = query.Where(d => d.DeviceType.IndexOf(type, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!string.IsNullOrWhiteSpace(vendor)) query = query.Where(d => d.Vendor.IndexOf(vendor, StringComparison.OrdinalIgnoreCase) >= 0);
            if (ports is { Length: > 0 }) query = query.Where(d => ports.Any(d.OpenPorts.Contains));
            return [.. query];
        }

        private static List<NetworkDevice>? ApplySort(List<NetworkDevice> devices, string? field,
            bool descending, TextWriter err)
        {
            field = (field ?? "ip").ToLowerInvariant();
            Func<NetworkDevice, object>? key = field switch
            {
                "ip" => d => d.IpSortKey, "hostname" or "host" => d => d.Hostname,
                "mac" => d => d.MacAddress, "vendor" => d => d.Vendor,
                "type" => d => d.DeviceType, "ports" => d => d.OpenPorts.Count, _ => null,
            };
            if (key is null) { err.WriteLine("Error: /sort must be ip, hostname, mac, vendor, type, or ports."); return null; }
            return descending ? [.. devices.OrderByDescending(key)] : [.. devices.OrderBy(key)];
        }

        private static string ConsoleSafe(string text)
        {
            var result = new StringBuilder(text.Length);
            foreach (char character in text)
                if ((character < 0x20 && character is not ('\r' or '\n' or '\t')) || character is >= (char)0x7f and <= (char)0x9f)
                    result.Append("\\x").Append(((int)character).ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                else result.Append(character);
            return result.ToString();
        }

        internal static string Render(string format, List<NetworkDevice> devices, string label,
                                     string theme, bool noHeader) => format switch
        {
            "csv" => Csv(devices, !noHeader),
            "json" => JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true }),
            "html" => ReportExport.BuildHtml(devices, label, theme.ToLowerInvariant()),
            _ => Table(devices, !noHeader),
        };

        internal static string Table(List<NetworkDevice> devices, bool header, bool ansi = false)
        {
            string[] headers = ["IP ADDRESS", "HOSTNAME", "MAC ADDRESS", "VENDOR", "TYPE", "OPEN PORTS"];
            var rows = devices.Select(d => new[] { d.IpAddress, d.Hostname, d.MacAddress, d.Vendor, d.DeviceType, d.OpenPortsDisplay }
                .Select(TerminalText.Escape).ToArray()).ToList();
            foreach (var row in rows) row[3] = Clip(row[3], 32);
            var width = new int[headers.Length];
            for (int c = 0; c < headers.Length; c++)
            { width[c] = header ? headers[c].Length : 0; foreach (var row in rows) width[c] = Math.Max(width[c], (row[c] ?? "").Length); }
            var sb = new StringBuilder();
            // Colors wrap the ALREADY-PADDED cell so escape codes never disturb column alignment.
            string ColorFor(int c) => c switch
            {
                0 => AnsiIp, 1 => AnsiHost, 2 => AnsiMac, 4 => AnsiType, 5 => AnsiPorts, _ => "",
            };
            void Row(string[] cells, string forceColor)
            {
                for (int c = 0; c < cells.Length; c++)
                {
                    string cell = c == cells.Length - 1 ? cells[c] ?? "" : (cells[c] ?? "").PadRight(width[c]) + "  ";
                    string color = forceColor.Length > 0 ? forceColor : ColorFor(c);
                    sb.Append(ansi && color.Length > 0 ? color + cell + AnsiReset : cell);
                }
                sb.AppendLine();
            }
            if (header)
            {
                Row(headers, ansi ? "\x1b[1;37m" : "");
                Row([.. width.Select(w => new string('-', w))], ansi ? AnsiDim : "");
            }
            foreach (var row in rows) Row(row, "");
            return sb.ToString().TrimEnd();
        }

        internal static string Csv(List<NetworkDevice> devices, bool header)
        {
            var sb = new StringBuilder();
            if (header) sb.AppendLine(ReportExport.CsvHeader);
            foreach (var d in devices) sb.AppendLine(string.Join(",", ReportExport.CsvValues(d).Select(CsvCell)));
            return sb.ToString().TrimEnd();
        }

        private static string CsvCell(string? value)
        { value = ReportExport.SpreadsheetSafeValue(value); return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value; }

        private static string SearchText(NetworkDevice d) => string.Join(" ", d.IpAddress, d.Hostname,
            d.MacAddress, d.Vendor, d.DeviceType, d.OpenPortsDisplay, d.HttpTitle, d.HttpServer,
            d.SshBanner, d.TlsSubject, d.SmbOs, d.SnmpDescr, d.NetbiosName, string.Join(" ", d.MdnsServices), d.SsdpServer);

        /// <summary>Resolves the output format. FromExtension is true only when nothing explicit
        /// chose it and the /export file extension did - the one case where the console display
        /// may substitute the table while the file keeps the extension's format.</summary>
        private static (string Format, bool FromExtension) ResolveFormat(
            Dictionary<string, string> options, string? explicitFormat, string? path)
        {
            var flags = new[] { ("table", Has(options, "/table", "--table")), ("csv", Has(options, "/csv", "--csv")),
                ("json", Has(options, "/json", "--json")), ("html", Has(options, "/html", "--html")) }
                .Where(x => x.Item2).Select(x => x.Item1).ToList();
            if (flags.Count > 1 || (flags.Count == 1 && explicitFormat != null)) return ("", false);
            if (flags.Count == 1) return (flags[0], false);
            if (explicitFormat != null) return (new[] { "table", "csv", "json", "html" }.Contains(explicitFormat, StringComparer.OrdinalIgnoreCase) ? explicitFormat.ToLowerInvariant() : "", false);
            if (path != null)
            { string ext = Path.GetExtension(path).ToLowerInvariant(); if (ext is ".html" or ".htm") return ("html", true); if (ext == ".json") return ("json", true); if (ext == ".csv") return ("csv", true); }
            return ("table", false);
        }

        private static int[]? ParsePorts(string? value)
        {
            if (value is null) return null;
            var result = new List<int>();
            foreach (string part in value.Split(','))
                if (!int.TryParse(part.Trim(), out int port) || port is < 1 or > 65535) return [];
                else result.Add(port);
            return [.. result.Distinct()];
        }

        private static int ParsePositive(Dictionary<string, string> options, params string[] names)
        { string? value = Value(options, names); if (value is null) return 0; return int.TryParse(value, out int n) && n > 0 ? n : -1; }

        private static string HelpText() => string.Join(Environment.NewLine,
        [
            $"KillerScan {AppInfo.Version} command line", "", "Usage: killerscan <command> [options]",
            "Existing /command and --command forms stay compatible.", "", "COMMANDS",
            "  killerscan scan [targets]        scan one or several CIDRs, hosts, or ranges",
            "  killerscan uiscan [targets]      open the interface and begin scanning",
            "  killerscan probe <IPv4>          deep-probe one host, including ports 1-1024",
            "  killerscan network               show the detected interface, subnet, gateway, and DNS",
            "  killerscan locations             list saved Locations and their IDs; changes nothing",
            "  killerscan vendor <MAC>          look up a MAC address in the offline OUI database",
            "  killerscan ping <target>         send a bounded set of ICMP checks",
            "  killerscan trace <target>        trace the network path to an IPv4 target",
            "  killerscan diagnose <target>     check DNS, ping, route, and TCP ports",
            "  killerscan watch <targets>       sample up to 16 IPv4 targets for availability",
            "  killerscan speedtest             run the native KillerSpeed test",
            "  killerscan version               print the version             /help  show this text", "",
            "SCAN AND PROBE OPTIONS",
            "  /quick                 discovery only; skip fingerprinting and the full port pass",
            "  /progress              write status and progress every 5% to stderr",
            "  /timeout <seconds>     cancel the operation after a deadline",
            "  /filter <text>         keep rows matching any displayed or fingerprint field",
            "  /type <text>           keep matching device types",
            "  /vendor-filter <text>  keep matching vendors",
            "  /ports <p,p,...>       keep devices with any listed open port",
            "  /sort <field>          ip, hostname, mac, vendor, type, or ports",
            "  /descending            reverse the selected sort",
            "  /limit <count>         keep the first N rows after filtering and sorting",
            "  /fail-empty            return exit code 3 when no rows remain",
            "  /demo                  fabricated network for screenshots; scans nothing (with /scan)",
            "  /location <name|id>    use and record into a saved Location; without it nothing is saved",
            "  /skip <check,...>      turn checks off: " + string.Join(", ", ScanOptions.Features),
            "  /adapter <name|ip>     send every probe from one adapter; only its own network is reachable", "", "OUTPUT OPTIONS",
            "  /format <kind>         table, csv, json, or html",
            "  /table | /csv | /json | /html   shorthand for /format",
            "  /export <path>         write output; extension selects format unless overridden",
            "  /no-header             omit table and CSV headings",
            "  /theme <name>          HTML theme (dark, light, black, 98se, or a grunge theme)",
            "  /quiet                 suppress stdout and status; normally paired with /export", "", "TARGET EXAMPLES",
            "  192.168.1.0/24   192.168.1.7   192.168.1.10-50",
            "  killerscan uiscan 192.168.1.10-50",
            "  killerscan scan 10.0.0.0/24,10.0.1.0/24 /json /ports 22,443 /sort vendor",
            "  killerscan probe 192.168.1.20 /json /export host.json /timeout 30",
            "  killerscan scan /quick /progress /filter printer /csv /no-header",
            "  killerscan ping gateway.local /count 4 /json",
            "  killerscan trace 1.1.1.1 /max-hops 20 /json",
            "  killerscan diagnose 192.168.1.20 /ports 22,80,443 /json",
            "  killerscan watch 192.168.1.1 192.168.1.20 /count 6 /interval 2 /json",
            "  killerscan speedtest /json", "",
            $"Several targets may be separate or comma-delimited; overlaps are deduplicated and the ceiling is {ScanTargets.MaxAddresses:N0} addresses.",
            "Ctrl+C cancels. Exit codes: 0 success, 1 failure/cancel/timeout, 2 usage, 3 empty/not found.",
            "Because this is a GUI-subsystem EXE, cmd scripts should use `start /wait`; PowerShell",
            "scripts should use `Start-Process -Wait` when they need the exit code."
        ]);

        private static (List<string>, Dictionary<string, string>, string?) ParseArgs(string[] args, string command)
        {
            var positionals = new List<string>();
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int start = Array.FindIndex(args, a => Eq(a, command)) + 1;
            for (int i = start; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("/", StringComparison.Ordinal) || a.StartsWith("-", StringComparison.Ordinal))
                {
                    if (ValueOptions.Any(o => Eq(o, a)))
                    { if (i + 1 >= args.Length) return (positionals, options, a + " needs a value."); options[a] = args[++i]; }
                    else if (FlagOptions.Any(o => Eq(o, a))) options[a] = "";
                    else return (positionals, options, "unknown option " + a + ".");
                }
                else positionals.Add(a.TrimEnd(','));
            }
            return (positionals, options, null);
        }

        private static bool Has(Dictionary<string, string> options, params string[] names) => names.Any(options.ContainsKey);
        private static string? Value(Dictionary<string, string> options, params string[] names)
        { foreach (string name in names) if (options.TryGetValue(name, out string? value)) return value; return null; }
        private static int Usage(TextWriter err, string message, int code = 2) { err.WriteLine(PaintErr("Error: " + message, AnsiBad)); return code; }
        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool IsHelp(string a) => Eq(a, "/help") || Eq(a, "--help") || Eq(a, "-h") || Eq(a, "/?") || Eq(a, "-?");
        private static bool IsVersion(string a) => Eq(a, "/version") || Eq(a, "--version") || Eq(a, "-v");
        private static string Clip(string? value, int max) => (value ?? "").Length <= max ? value ?? "" : value![..(max - 3)] + "...";
        private static string Flatten(string? value) => TerminalText.Escape(value).Trim();

        /// <summary>
        /// Attaches the parent console whenever stdout or stderr still needs it, so
        /// <c>killerscan /scan /json &gt; devices.json</c> keeps its errors on screen. Inherited pipe
        /// and file handles are preserved: attaching can replace them with console handles, which
        /// would leave a downstream process with no scan data, so each redirected handle is put back.
        /// </summary>
        internal static void AttachKeepingRedirects(Func<int, bool> isRedirected, Func<int, IntPtr> get,
            Action<int, IntPtr> set, Func<bool> attach)
        {
            bool outRedirected = isRedirected(StdOutputHandle), errRedirected = isRedirected(StdErrorHandle);
            if (outRedirected && errRedirected) return;
            IntPtr savedOut = outRedirected ? get(StdOutputHandle) : IntPtr.Zero;
            IntPtr savedErr = errRedirected ? get(StdErrorHandle) : IntPtr.Zero;
            if (!attach()) return;
            if (outRedirected) set(StdOutputHandle, savedOut);
            if (errRedirected) set(StdErrorHandle, savedErr);
        }

        private static (TextWriter, TextWriter) OpenConsole()
        {
            try
            {
                AttachKeepingRedirects(IsRedirected, GetStdHandle, (id, handle) => SetStdHandle(id, handle),
                    () => AttachConsole(AttachParentProcess));
                _outAnsi = TryEnableVt(StdOutputHandle);
                _errAnsi = TryEnableVt(StdErrorHandle);
                var @out = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                var err = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                Console.SetOut(@out);
                Console.SetError(err);
                return (@out, err);
            }
            catch { }
            return (TextWriter.Null, TextWriter.Null);
        }
    }
}
