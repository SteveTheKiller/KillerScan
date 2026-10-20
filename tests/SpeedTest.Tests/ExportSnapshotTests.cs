using System;
using System.IO;
using KillerScan.Controls;
using KillerScan.Engine;

internal static class ExportSnapshotTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KillerScan-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var workspace = new ScanWorkspace();
            var scannedAt = new DateTimeOffset(2025, 3, 4, 5, 6, 0, TimeSpan.Zero);
            workspace.LoadSnapshot("192.0.2.0/24", [new NetworkDevice { IpAddress = "192.0.2.1", Hostname = "original-device", OpenPorts = [80] }], scannedAt);
            workspace.Targets = "198.51.100.0/24";
            string output = Path.Combine(directory, "report.html");
            workspace.ExportDialog = dialog => { dialog.FileName = output; return true; };
            workspace.Export("html");
            string report = File.ReadAllText(output);
            if (!report.Contains("<b>192.0.2.0/24</b>") || report.Contains("198.51.100.0/24"))
                throw new InvalidOperationException("The real HTML export must identify its scanned dataset even after the pending target changes.");
            if (!report.Contains("<b>2025-03-04 05:06</b>"))
                throw new InvalidOperationException("The report timestamp must belong to the captured dataset rather than the export clock.");
            workspace.ExportDialog = dialog =>
            {
                workspace.Session.Devices[0].Hostname = "changed-during-dialog";
                workspace.LoadSnapshot("203.0.113.0/24", [new NetworkDevice { IpAddress = "203.0.113.1", Hostname = "next-scan" }]);
                dialog.FileName = output;
                return true;
            };
            workspace.Export("html");
            report = File.ReadAllText(output);
            if (!report.Contains("<b>192.0.2.0/24</b>") || !report.Contains("original-device") ||
                report.Contains("203.0.113.0/24") || report.Contains("next-scan") || report.Contains("changed-during-dialog"))
                throw new InvalidOperationException("Export must capture both identity and device values before the save dialog can change the live dataset.");
            workspace.LoadSnapshot("192.0.2.0/24", [new NetworkDevice { IpAddress = "192.0.2.1", Hostname = "csv-before-dialog" }]);
            workspace.ExportDialog = dialog =>
            {
                workspace.Session.Devices[0].Hostname = "csv-after-dialog";
                dialog.FileName = output;
                return true;
            };
            workspace.Export("csv");
            string csv = File.ReadAllText(output);
            if (!csv.Contains("csv-before-dialog") || csv.Contains("csv-after-dialog"))
                throw new InvalidOperationException("CSV export also captures device values before opening its save dialog.");
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: HTML export keeps its scanned target after pending input changes.");
    }

    internal static void CheckPartial(ScanWorkspace workspace)
    {
        string path = Path.Combine(Path.GetTempPath(), "KillerScan-partial-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            workspace.ExportDialog = dialog => { dialog.FileName = path; return true; };
            workspace.Export("html");
            if (!File.ReadAllText(path).Contains("<b>Partial scan</b>"))
                throw new InvalidOperationException("Export during the automatic deep phase must identify its captured results as partial.");
        }
        finally { workspace.ExportDialog = null; if (File.Exists(path)) File.Delete(path); }
    }
}
