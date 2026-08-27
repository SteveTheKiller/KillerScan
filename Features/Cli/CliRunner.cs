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

namespace KillerScan.Features.Cli
{
    // Headless commands use the same scanner, classifier, overrides, OUI database and report
    // writer as the GUI. Progress is stderr; result data is stdout.
    internal static class CliRunner
    {
        private const int AttachParentProcess = -1;
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);

        private static readonly string[] ValueOptions =
        [
            "/export", "--export", "/output", "--output", "/format", "--format",
            "/sort", "--sort", "/filter", "--filter", "/type", "--type",
            "/vendor-filter", "--vendor-filter", "/ports", "--ports", "/limit", "--limit",
            "/timeout", "--timeout", "/theme", "--theme"
        ];

        private static readonly string[] FlagOptions =
        [
            "/quick", "--quick", "/full", "--full", "/quiet", "--quiet",
            "/progress", "--progress", "/descending", "--descending", "/desc", "--desc",
            "/no-header", "--no-header", "/fail-empty", "--fail-empty",
            "/json", "--json", "/csv", "--csv", "/html", "--html", "/table", "--table"
        ];

        private static readonly string[] HtmlThemes =
        [
            "dark", "light", "black", "98se", "blood", "greed", "cyanotic", "ectoplasm",
            "decay", "malaise", "sepulchre", "delirium", "mourning"
        ];

        internal static bool TryRunCli(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args is null || args.Length == 0) return false;
            string? command = args.FirstOrDefault(a => IsHelp(a) || IsVersion(a) ||
                Eq(a, "/scan") || Eq(a, "--scan") || Eq(a, "/probe") || Eq(a, "--probe") ||
                Eq(a, "/network") || Eq(a, "--network") || Eq(a, "/vendor") || Eq(a, "--vendor"));
            if (command is null) return false;

            var (@out, err) = OpenConsole();
            if (IsHelp(command)) { @out.WriteLine(HelpText()); return true; }
            if (IsVersion(command)) { @out.WriteLine(AppInfo.Version); return true; }

            try
            {
                var (positionals, options, parseError) = ParseArgs(args, command);
                if (parseError != null) { err.WriteLine("Error: " + parseError); exitCode = 2; }
                else if (Eq(command, "/network") || Eq(command, "--network"))
                    exitCode = RunNetwork(positionals, options, @out, err);
                else if (Eq(command, "/vendor") || Eq(command, "--vendor"))
                    exitCode = RunVendor(positionals, options, @out, err);
                else
                    exitCode = RunDevices(positionals, options, @out, err,
                        probe: Eq(command, "/probe") || Eq(command, "--probe"));
            }
            catch (Exception ex) { err.WriteLine("Error: " + Flatten(ex.Message)); exitCode = 1; }
            return true;
        }

        private static int RunNetwork(List<string> positionals, Dictionary<string, string> options,
                                      TextWriter @out, TextWriter err)
        {
            if (positionals.Count > 0 || options.Count > 0) return Usage(err, "/network takes no options or targets.");
            var net = LocalNetwork.Detect();
            if (net is null) return Usage(err, "no active IPv4 network was found.", 1);
            @out.WriteLine("INTERFACE  " + net.InterfaceLabel);
            @out.WriteLine("LOCAL IP   " + net.LocalIp);
            @out.WriteLine("SUBNET     " + net.Subnet);
            @out.WriteLine("GATEWAY    " + (net.Gateway.Length == 0 ? "-" : net.Gateway));
            @out.WriteLine("DNS        " + (net.Dns.Length == 0 ? "-" : net.Dns));
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
            @out.WriteLine(vendor.Length == 0 ? "Unknown" : vendor);
            return vendor.Length == 0 ? 3 : 0;
        }

        private static int RunDevices(List<string> positionals, Dictionary<string, string> options,
                                      TextWriter @out, TextWriter err, bool probe)
        {
            bool quiet = Has(options, "/quiet", "--quiet");
            bool quick = Has(options, "/quick", "--quick");
            bool progress = Has(options, "/progress", "--progress");
            bool noHeader = Has(options, "/no-header", "--no-header");
            bool failEmpty = Has(options, "/fail-empty", "--fail-empty");
            string? exportPath = Value(options, "/export", "--export", "/output", "--output");
            string format = ResolveFormat(options, Value(options, "/format", "--format"), exportPath);
            if (format.Length == 0) return Usage(err, "format must be table, csv, json, or html, with only one format selected.");
            if (exportPath != null && exportPath.Length == 0) return Usage(err, "/export needs a file path.");
            IPAddress? probeIp = null;
            if (probe && positionals.Count != 1) return Usage(err, "/probe needs exactly one IPv4 address.");
            if (probe && (!IPAddress.TryParse(positionals[0], out probeIp) || probeIp.GetAddressBytes().Length != 4))
                return Usage(err, "/probe currently accepts an IPv4 address, not a hostname.");

            string targetText = string.Join(",", positionals);
            ScanTargetResult? parsed = null;
            if (!probe)
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

            OuiLookup.Load();
            DeviceOverrides.Load();
            using var cts = new CancellationTokenSource();
            if (timeout > 0) cts.CancelAfter(TimeSpan.FromSeconds(timeout));
            void OnCancel(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; cts.Cancel(); }
            try { Console.CancelKeyPress += OnCancel; } catch { }

            var scanner = new NetworkScanner();
            int lastProgress = -1;
            if (progress && !quiet)
            {
                scanner.StatusChanged += s => err.WriteLine(s);
                scanner.ProgressChanged += p =>
                {
                    if (p == 100 || p >= lastProgress + 5)
                    { lastProgress = p; err.WriteLine($"Progress: {p}%"); }
                };
            }
            if (!quiet && !progress)
                err.WriteLine(probe ? $"Deep-probing {positionals[0]}..." : $"Scanning {parsed!.Summary} ({parsed.Addresses.Count:N0} addresses)...");

            List<NetworkDevice> devices;
            try
            {
                Task<List<NetworkDevice>> operation = probe
                    ? ProbeAsListAsync(scanner, probeIp!.ToString(), cts.Token)
                    : scanner.ScanSubnetAsync(targetText, cts.Token, fullScan: !quick);
                if (timeout > 0)
                {
                    var deadline = Task.Delay(TimeSpan.FromSeconds(timeout));
                    if (Task.WhenAny(operation, deadline).GetAwaiter().GetResult() != operation)
                    {
                        cts.Cancel();
                        err.WriteLine($"Operation timed out after {timeout} seconds.");
                        return 1;
                    }
                }
                devices = operation.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                err.WriteLine(timeout > 0 ? $"Operation timed out after {timeout} seconds." : "Operation cancelled.");
                return 1;
            }
            finally { try { Console.CancelKeyPress -= OnCancel; } catch { } }

            devices = ApplyFilters(devices, options, ports);
            var sorted = ApplySort(devices, Value(options, "/sort", "--sort"),
                Has(options, "/descending", "--descending", "/desc", "--desc"), err);
            if (sorted is null) return 2;
            devices = sorted;
            if (limit > 0) devices = [.. devices.Take(limit)];

            string label = probe ? positionals[0] : parsed!.Summary;
            string body = Render(format, devices, label, theme, noHeader);
            if (!quiet) @out.WriteLine(body);
            if (exportPath != null)
            {
                try
                {
                    string full = Path.GetFullPath(exportPath);
                    string? dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(full, body, new UTF8Encoding(false));
                    if (!quiet) err.WriteLine("Wrote " + full);
                }
                catch (Exception ex) { err.WriteLine("Error: could not write the export - " + Flatten(ex.Message)); return 1; }
            }
            if (!quiet) err.WriteLine($"{devices.Count} matching device{(devices.Count == 1 ? "" : "s")}.");
            return devices.Count == 0 && failEmpty ? 3 : 0;
        }

        private static async Task<List<NetworkDevice>> ProbeAsListAsync(
            NetworkScanner scanner, string ip, CancellationToken cancellationToken)
        {
            return [await scanner.DeepProbeHostAsync(ip, cancellationToken)];
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

        private static string Render(string format, List<NetworkDevice> devices, string label,
                                     string theme, bool noHeader) => format switch
        {
            "csv" => Csv(devices, !noHeader),
            "json" => JsonSerializer.Serialize(devices, new JsonSerializerOptions { WriteIndented = true }),
            "html" => ReportExport.BuildHtml(devices, label, theme.ToLowerInvariant()),
            _ => Table(devices, !noHeader),
        };

        private static string Table(List<NetworkDevice> devices, bool header)
        {
            string[] headers = ["IP ADDRESS", "HOSTNAME", "MAC ADDRESS", "VENDOR", "TYPE", "OPEN PORTS"];
            var rows = devices.Select(d => new[] { d.IpAddress, d.Hostname, d.MacAddress, Clip(d.Vendor, 32), d.DeviceType, d.OpenPortsDisplay }).ToList();
            var width = new int[headers.Length];
            for (int c = 0; c < headers.Length; c++)
            { width[c] = header ? headers[c].Length : 0; foreach (var row in rows) width[c] = Math.Max(width[c], (row[c] ?? "").Length); }
            var sb = new StringBuilder();
            if (header) { AppendRow(sb, headers, width); AppendRow(sb, [.. width.Select(w => new string('-', w))], width); }
            foreach (var row in rows) AppendRow(sb, row, width);
            return sb.ToString().TrimEnd();
        }

        private static string Csv(List<NetworkDevice> devices, bool header)
        {
            var sb = new StringBuilder();
            if (header) sb.AppendLine("IP Address,Hostname,MAC Address,Vendor,Type,Open Ports");
            foreach (var d in devices) sb.AppendLine(string.Join(",", new[]
                { d.IpAddress, d.Hostname, d.MacAddress, d.Vendor, d.DeviceType, d.OpenPortsDisplay }.Select(CsvCell)));
            return sb.ToString().TrimEnd();
        }

        private static string CsvCell(string? value)
        { value ??= ""; return value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value; }
        private static void AppendRow(StringBuilder sb, string[] cells, int[] width)
        { for (int c = 0; c < cells.Length; c++) if (c == cells.Length - 1) sb.Append(cells[c] ?? ""); else sb.Append((cells[c] ?? "").PadRight(width[c])).Append("  "); sb.AppendLine(); }

        private static string SearchText(NetworkDevice d) => string.Join(" ", d.IpAddress, d.Hostname,
            d.MacAddress, d.Vendor, d.DeviceType, d.OpenPortsDisplay, d.HttpTitle, d.HttpServer,
            d.SshBanner, d.TlsSubject, d.SmbOs, d.SnmpDescr, d.NetbiosName, string.Join(" ", d.MdnsServices), d.SsdpServer);

        private static string ResolveFormat(Dictionary<string, string> options, string? explicitFormat, string? path)
        {
            var flags = new[] { ("table", Has(options, "/table", "--table")), ("csv", Has(options, "/csv", "--csv")),
                ("json", Has(options, "/json", "--json")), ("html", Has(options, "/html", "--html")) }
                .Where(x => x.Item2).Select(x => x.Item1).ToList();
            if (flags.Count > 1 || (flags.Count == 1 && explicitFormat != null)) return "";
            if (flags.Count == 1) return flags[0];
            if (explicitFormat != null) return new[] { "table", "csv", "json", "html" }.Contains(explicitFormat, StringComparer.OrdinalIgnoreCase) ? explicitFormat.ToLowerInvariant() : "";
            if (path != null)
            { string ext = Path.GetExtension(path).ToLowerInvariant(); if (ext is ".html" or ".htm") return "html"; if (ext == ".json") return "json"; if (ext == ".csv") return "csv"; }
            return "table";
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
            $"KillerScan {AppInfo.Version} - command line", "", "COMMANDS",
            "  /scan [targets]        scan one or several CIDRs, hosts, or ranges",
            "  /probe <IPv4>          deep-probe one host, including ports 1-1024",
            "  /network               show the detected interface, subnet, gateway, and DNS",
            "  /vendor <MAC>          look up a MAC address in the offline OUI database",
            "  /version               print the version             /help  show this text", "",
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
            "  /fail-empty            return exit code 3 when no rows remain", "", "OUTPUT OPTIONS",
            "  /format <kind>         table, csv, json, or html",
            "  /table | /csv | /json | /html   shorthand for /format",
            "  /export <path>         write output; extension selects format unless overridden",
            "  /no-header             omit table and CSV headings",
            "  /theme <name>          HTML theme (dark, light, black, 98se, or a grunge theme)",
            "  /quiet                 suppress stdout and status; normally paired with /export", "", "TARGET EXAMPLES",
            "  192.168.1.0/24   192.168.1.7   192.168.1.10-50",
            "  /scan 10.0.0.0/24,10.0.1.0/24 /json /ports 22,443 /sort vendor",
            "  /probe 192.168.1.20 /json /export host.json /timeout 30",
            "  /scan /quick /progress /filter printer /csv /no-header", "",
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
        private static int Usage(TextWriter err, string message, int code = 2) { err.WriteLine("Error: " + message); return code; }
        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool IsHelp(string a) => Eq(a, "/help") || Eq(a, "--help") || Eq(a, "-h") || Eq(a, "/?") || Eq(a, "-?");
        private static bool IsVersion(string a) => Eq(a, "/version") || Eq(a, "--version") || Eq(a, "-v");
        private static string Clip(string? value, int max) => (value ?? "").Length <= max ? value ?? "" : value![..(max - 3)] + "...";
        private static string Flatten(string? value) => (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        private static (TextWriter, TextWriter) OpenConsole()
        {
            try
            {
                if (AttachConsole(AttachParentProcess))
                {
                    var @out = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                    var err = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
                    Console.SetOut(@out); Console.SetError(err); return (@out, err);
                }
            }
            catch { }
            return (TextWriter.Null, TextWriter.Null);
        }
    }
}
