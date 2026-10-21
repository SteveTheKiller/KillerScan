using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Engine;
using KillerScan.Services;

// After a language switch, the device and Services column headers and HTML reports follow it:
// the report declares the language it is written in and has a translated title.
internal static class ReportLanguageTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;

    internal static void Run()
    {
        using var workspace = new ScanWorkspace("192.0.2.0/24", false);
        try
        {
            Apply("De");
            workspace.RefreshLocale();
            string Text(string key) => (string)Application.Current.FindResource(key);
            var ip = (DataGridColumn)workspace.FindName("ColIp");
            var services = (DataGrid)workspace.FindName("ServicesGrid");
            Require(Equals(ip.Header, Text("Str_Col_Ip")) && Equals(((DataGridColumn)workspace.FindName("ColVendor")).Header, Text("Str_Col_Vendor")),
                "Device columns follow the language switch.");
            Require(Equals(services.Columns[0].Header, Text("Str_Services_Service")), "Services columns follow the language switch.");

            string html = ReportExport.BuildSnapshotHtml([new NetworkDevice { IpAddress = "192.0.2.1" }], "192.0.2.0/24", "dark",
                DateTimeOffset.Now, ScanCompletion.Complete);
            Require(html.Contains("<html lang='de-DE'"), "The report declares the language it is written in.");
            Require(html.Contains("<title>" + Text("Str_Rep_Title") + "</title>") && Text("Str_Rep_Title") != "KillerScan Report",
                "The report title is translated.");

            Apply("EnUS");
            html = ReportExport.BuildSnapshotHtml([], "192.0.2.0/24", "dark", DateTimeOffset.Now, ScanCompletion.Complete);
            Require(html.Contains("<html lang='en-US'") && html.Contains("<title>KillerScan Report</title>"),
                "English reports keep their English title and declare en-US.");
        }
        finally { Apply("EnUS"); }
        Console.WriteLine("PASS: column headers and HTML reports follow the app language.");
    }

    private static void Apply(string locale)
    {
        var manager = typeof(LocaleManager);
        var value = Enum.Parse(typeof(KillerScan.Services.Locale), locale);
        manager.GetField("_current", Static)!.SetValue(null, value);
        manager.GetMethod("ApplyInternal", Static)!.Invoke(null, new[] { value });
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
