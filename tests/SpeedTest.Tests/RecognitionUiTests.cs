using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Engine;
using KillerScan.Services;

// The type cell explains itself: a tooltip with the confidence and strongest signals, a
// "Why This Type?" window, manual types that keep the automatic result, and exports that carry
// the evidence. Everything runs on fabricated devices in a scratch Location.
internal static class RecognitionUiTests
{
    internal static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        bool demo = DemoData.Enabled;
        DemoData.Enabled = false;
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-recognition-ui-" + Guid.NewGuid().ToString("N"));
        try
        {
            var catalog = new LocationCatalog(root);
            catalog.Load("Unassigned");
            catalog.Create("Site", out var info);
            var site = catalog.Open(info!);

            var printer = new NetworkDevice
            {
                IpAddress = "192.0.2.10", MacAddress = "00:00:5E:00:53:10", Vendor = "Brother Industries",
                OpenPorts = [631, 9100], MdnsServices = ["_ipp._tcp"], SnmpDescr = "Brother <b>printer</b>",
            };
            printer.Recognition = NetworkScanner.AssessDevice(printer, null);
            printer.DeviceType = printer.Recognition.Type;
            var old = new NetworkDevice { IpAddress = "192.0.2.11", DeviceType = "Windows" };

            using var workspace = new ScanWorkspace("192.0.2.0/24", false);
            Require(workspace.SetLocation(site), "The tab moves to the scratch Location.");
            workspace.LoadSnapshot("192.0.2.0/24", [printer, old]);
            var grid = (DataGrid)workspace.FindName("ResultsGrid");
            var shown = workspace.ScannedDevices.Single(d => d.IpAddress == printer.IpAddress);
            grid.SelectedItem = shown;

            string Res(string key) => (string)Application.Current.FindResource(key);

            // Tooltip: built when it opens, from the device's current evidence.
            string Tip(NetworkDevice device)
            {
                var cell = new DataGridCell { DataContext = device, ToolTip = " " };
                Invoke(workspace, "TypeCell_ToolTipOpening", cell, null!);
                return (string)cell.ToolTip;
            }
            string tip = Tip(shown);
            Require(tip.StartsWith(Res("Str_Rec_Conf_High")) && tip.Contains(Res("Str_RecSrc_mdns") + ": _ipp._tcp"),
                "The tooltip leads with the confidence and lists the strongest signals: " + tip);
            Require(Tip(old) == Res("Str_Rec_None"), "A device with no recorded evidence says so.");

            // "Why This Type?" reads only what was recorded.
            string? heading = null, detail = null;
            workspace.ShowRecognition = (h, d) => { heading = h; detail = d; };
            var menu = (MenuItem)workspace.FindName("WhyTypeMenuItem");
            Require(menu.InputGestureText == "Ctrl+I", "The menu item shows its shortcut.");
            workspace.WhyType_Click(workspace, new RoutedEventArgs());
            Require(heading == string.Format(Res("Str_Rec_Heading"), DeviceTypeConverter.Display("Printer"), Res("Str_Rec_Conf_High")),
                "The window heading names the type and confidence: " + heading);
            Require(detail!.Contains(Res("Str_Rec_Evidence")) && detail.Contains(Res("Str_RecSrc_snmp") + ": Brother <b>printer</b>") &&
                    detail.Contains(Res("Str_Rec_Caveat")), "The window lists every signal and the caveat: " + detail);

            // A manual type keeps the automatic result; clearing it restores the evidence.
            Invoke(workspace, "SetType_Click", new MenuItem { Tag = "NAS" }, new RoutedEventArgs());
            {
                // The status line names the type in the language on screen, not by its English storage key.
                var resources = Application.Current.Resources;
                object? saved = resources["Str_Dev_NAS"];
                resources["Str_Dev_NAS"] = "Netzspeicher";
                try
                {
                    Invoke(workspace, "SetType_Click", new MenuItem { Tag = "NAS" }, new RoutedEventArgs());
                    Require(((TextBlock)workspace.GetType().GetField("StatusText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(workspace)!).Text.Contains("Netzspeicher"),
                        "The Set type status line shows the localized type name.");
                }
                finally { if (saved == null) resources.Remove("Str_Dev_NAS"); else resources["Str_Dev_NAS"] = saved; }
            }
            Require(shown.DeviceType == "NAS" && shown.Recognition!.Confidence == RecognitionConfidence.Manual &&
                    shown.Recognition.AutomaticType == "Printer", "A manual type is recorded as Manual over the automatic result.");
            Require(Tip(shown).Contains(string.Format(Res("Str_Rec_Automatic"), DeviceTypeConverter.Display("Printer"), Res("Str_Rec_Conf_High"))),
                "The tooltip of a manual type still shows the automatic result.");
            Invoke(workspace, "ClearOverride_Click", workspace, new RoutedEventArgs());
            Require(shown.DeviceType == "Printer" && shown.Recognition!.Confidence == RecognitionConfidence.High,
                "Clearing the manual type restores the automatic result.");

            // Exports carry the evidence: CSV adds two trailing columns, HTML a hover title.
            string csv = ReportExport.BuildCsv([shown, old]);
            var lines = csv.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Require(lines[0] == "IP Address,Hostname,MAC Address,Vendor,Type,Open Ports,Confidence,Evidence,Access", "CSV adds Confidence and Evidence at the end.");
            Require(lines[1].Contains("\"High\"") && lines[1].Contains("mdns: _ipp._tcp"), "A CSV row has the confidence and evidence: " + lines[1]);
            Require(lines[2].EndsWith(",\"\",\"\",\"\""), "A device with no evidence leaves its evidence columns empty.");
            string html = ReportExport.BuildHtml([shown], "192.0.2.0/24", "dark");
            Require(html.Contains("title='" + Res("Str_Rec_Conf_High")) && html.Contains("&lt;b&gt;printer&lt;/b&gt;") && !html.Contains("<b>printer</b>"),
                "The HTML type cell has an escaped evidence title.");
        }
        finally
        {
            DemoData.Enabled = demo;
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine("PASS: the type cell explains itself in a tooltip, a window and exports.");
    }

    private static void Invoke(object target, string method, object sender, RoutedEventArgs? e)
    {
        var info = target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException(method + " not found.");
        object? args = info.GetParameters()[1].ParameterType == typeof(ToolTipEventArgs)
            ? Activator.CreateInstance(typeof(ToolTipEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, [true], null)
            : e;
        info.Invoke(target, [sender, args]);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
