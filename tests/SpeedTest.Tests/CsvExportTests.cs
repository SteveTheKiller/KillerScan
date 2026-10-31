using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualBasic.FileIO;
using KillerScan.Engine;
using KillerScan.Features.Cli;
using KillerScan.Services;

internal static class CsvExportTests
{
    public static Task Run()
    {
        EvidenceMatchesGui();
        CommandRoutes();
        foreach (var (input, expected) in new (string Input, string Expected)[]
        {
            ("=1+1", "'=1+1"), ("+1", "'+1"), ("-1", "'-1"), ("@SUM(1)", "'@SUM(1)"),
            (" \t=1+1", "' \t=1+1"), ("printer", "printer"), ("a,\"b", "a,\"\"b"), ("-", "-"), (" + ", " + ")
        })
        {
            var device = new NetworkDevice { IpAddress = "192.0.2.10", Hostname = input };
            string csv = ReportExport.BuildCsv([device]);
            if (!csv.Contains("\"" + expected + "\"", StringComparison.Ordinal))
                throw new InvalidOperationException("Device CSV did not encode hostname safely: " + input);
        }
        string noPorts = ReportExport.BuildCsv([new NetworkDevice { IpAddress = "192.0.2.11" }]);
        if (!noPorts.Contains(",\"-\"", StringComparison.Ordinal) || noPorts.Contains("'-", StringComparison.Ordinal))
            throw new InvalidOperationException("A host with no open ports must export a bare dash.");

        string cli = CliRunner.Csv([new NetworkDevice { IpAddress = "192.0.2.12", Hostname = "=1+1" },
            new NetworkDevice { IpAddress = "192.0.2.13", Hostname = "a,\"b" }], header: false);
        string[] rows = cli.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (rows.Length != 2 || !rows[0].StartsWith("192.0.2.12,'=1+1,", StringComparison.Ordinal) || Parse(cli)[0][5] != "-")
            throw new InvalidOperationException("CLI CSV must neutralize formulas and keep a bare dash for no ports: " + cli);
        if (!rows[1].StartsWith("192.0.2.13,\"a,\"\"b\",", StringComparison.Ordinal))
            throw new InvalidOperationException("CLI CSV must quote fields holding commas or quotes: " + cli);
        return Task.CompletedTask;
    }

    private static void EvidenceMatchesGui()
    {
        var device = new NetworkDevice
        {
            IpAddress = "192.0.2.10", Hostname = "=formula", Vendor = "Caf\u00e9, \"vendor\"\r\nnext", DeviceType = "Printer",
            Recognition = new DeviceRecognition { Confidence = RecognitionConfidence.High,
                Signals = [new RecognitionSignal { Source = "http", Detail = "title, \"value\"\r\nnext" }] },
            Access = [new AccessEndpoint { Protocol = "https", Port = 443, Evidence = AccessEvidence.HttpResponse,
                HttpStatus = 401, Login = AccessLogin.Requested, AuthScheme = "Basic" },
                new AccessEndpoint { Protocol = "ssh", Port = 22, Evidence = AccessEvidence.SshBanner }]
        };
        string[] header = ["IP Address", "Hostname", "MAC Address", "Vendor", "Type", "Open Ports", "Confidence", "Evidence", "Access"];
        string[] expected = ["192.0.2.10", "'=formula", "", "Caf\u00e9, \"vendor\"\r\nnext", "Printer", "-", "High",
            "http: title, \"value\"\r\nnext", "ssh:22 banner; https:443 401 login-requested Basic"];
        var gui = Parse(ReportExport.BuildCsv([device]));
        var cli = Parse(CliRunner.Render("csv", [device], "fixture", "dark", noHeader: false));
        if (cli.Count != 2 || !cli[0].SequenceEqual(header) || !cli[1].SequenceEqual(expected)
            || !gui[0].SequenceEqual(header) || !gui[1].SequenceEqual(expected))
            throw new InvalidOperationException("CLI and GUI CSV must carry the nine-column evidence contract and identical decoded values.");
        var absent = Parse(CliRunner.Render("csv", [new NetworkDevice { IpAddress = "192.0.2.11" }], "fixture", "dark", noHeader: true));
        if (absent.Count != 1 || absent[0].Length != 9 || absent[0].Skip(6).Any(value => value != ""))
            throw new InvalidOperationException("Absent evidence stays empty and no-header suppresses only the header.");
        Console.WriteLine("PASS: parsed CLI and GUI evidence CSV agrees across 9 columns, quoting, multiline Unicode and absent evidence.");
    }

    private static void CommandRoutes()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-cli-csv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int count = 0;
        try
        {
            foreach (bool probe in new[] { false, true })
            foreach (bool explicitCsv in new[] { false, true })
            foreach (bool header in new[] { true, false })
            {
                string path = Path.Combine(root, "result.csv");
                string args = probe ? "probe 127.0.0.1 /skip " + string.Join(",", ScanOptions.Features) : "scan /demo";
                args += (explicitCsv ? " /csv" : "") + (header ? "" : " /no-header") + " /export \"" + path + "\"";
                using var process = Process.Start(new ProcessStartInfo(typeof(CliRunner).Assembly.Location, args)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
                using var captured = new MemoryStream();
                var output = process.StandardOutput.BaseStream.CopyToAsync(captured);
                var error = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000)) { process.Kill(); throw new InvalidOperationException("CLI CSV route timed out: " + args); }
                output.GetAwaiter().GetResult();
                string stdout = System.Text.Encoding.UTF8.GetString(captured.ToArray());
                string stderr = error.GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new InvalidOperationException("CLI CSV route failed: " + args + " " + stderr);
                var rows = Parse(stdout);
                var exported = Parse(File.ReadAllText(path));
                if (rows.Count <= (header ? 1 : 0) || rows.Any(row => row.Length != 9)
                    || exported.Count != rows.Count || rows.Where((row, index) => !row.SequenceEqual(exported[index])).Any()
                    || (header && !rows[0].SequenceEqual(new[] { "IP Address", "Hostname", "MAC Address", "Vendor", "Type", "Open Ports", "Confidence", "Evidence", "Access" }))
                    || (!header && rows[0][0] == "IP Address") || File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 239, 187, 191 })
                    || stdout.StartsWith("\ufeff", StringComparison.Ordinal))
                    throw new InvalidOperationException("CLI scan/probe must export and redirect the same nine-column CSV without a BOM: " + args);
                count++;
            }
            Console.WriteLine("PASS: " + count + " compiled CLI scan/probe CSV routes, explicit/extension format, headers on/off, export/redirect parity and no BOM.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static List<string[]> Parse(string csv)
    {
        using var reader = new TextFieldParser(new StringReader(csv));
        reader.SetDelimiters(",");
        reader.HasFieldsEnclosedInQuotes = true;
        reader.TrimWhiteSpace = false;
        var rows = new List<string[]>();
        while (!reader.EndOfData) rows.Add(reader.ReadFields()!);
        return rows;
    }
}
