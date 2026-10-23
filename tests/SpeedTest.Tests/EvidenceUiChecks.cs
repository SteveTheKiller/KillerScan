using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Engine;
using KillerScan.Services;
using KillerScan.Shell;

internal static class EvidenceUiChecks
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static void Run(MainWindow window, Action<FrameworkElement, string> capture)
    {
        Require(DemoData.Enabled, "Evidence UI fixtures cannot write real history.");
        var oldHistory = ScanHistory.Entries.ToArray();
        var device = new NetworkDevice { IpAddress = "192.0.2.40", Hostname = "printer-fixture", MacAddress = "02:00:00:00:00:01",
            HttpTitle = "Embedded Web Server; model=Unit42", SnmpDescr = "Printer; model=Unit42", OpenPorts = { 80, 9100 }, LocationId = Locations.Current.Id };
        device.DeviceType = NetworkScanner.ClassifyDevice(device);
        var when = new DateTimeOffset(2026, 10, 18, 7, 0, 0, TimeSpan.Zero);
        device.Identification = device.Identification! with { ObservationStartedAtUtc = when, ObservationCompletedAtUtc = when.AddSeconds(3),
            Evidence = device.Identification!.Evidence.Select(fact => fact with { ObservedAtUtc = when.AddSeconds(2) }).ToArray(), Scope = "full" };
        Require(device.Identification.Confidence == "High" && device.Identification.ReportedModel == "Unit42", "Fabricated matching protocols produce a model declaration and assessment.");
        var entry = ScanHistory.Record("192.0.2.40", new[] { device }).Current;
        Require(entry.Devices.Single().Identification?.Evidence.Length == device.Identification.Evidence.Length, "History preserves the evidence snapshot.");
        var restored = JsonSerializer.Deserialize<ScanHistoryEntry>(JsonSerializer.Serialize(entry))!;
        Require(restored.Devices[0].Identification!.ObservationCompletedAtUtc == when.AddSeconds(3), "History reload keeps the old observation time.");
        var old = JsonSerializer.Deserialize<HistoricalDevice>("{\"IpAddress\":\"192.0.2.50\",\"DeviceType\":\"NAS\"}")!;
        Require(old.Identification == null, "Old history is not retroactively assessed.");
        string csv = ReportExport.BuildCsv(new[] { device });
        Require(csv.Contains("Identification JSON") && csv.Contains("High") && csv.Contains("Unit42"), "Device CSV carries confidence, model and structured evidence.");
        var hostile = new NetworkDevice { IpAddress = "192.0.2.41", HttpTitle = "VMware ESXi <script>bad()</script>", OpenPorts = { 80 } };
        hostile.DeviceType = NetworkScanner.ClassifyDevice(hostile);
        string html = ReportExport.BuildHtml(new[] { hostile }, "192.0.2.41", "black");
        Require(html.Contains("&lt;script&gt;bad()&lt;/script&gt;") && !html.Contains("VMware ESXi <script>"), "HTML treats remote evidence as text, not markup.");
        var workspace = (ScanWorkspace)typeof(MainWindow).GetField("_scanWorkspace", Instance)!.GetValue(window)!;
        workspace.Session.Devices.Add(device); workspace.Session.Devices.Add(hostile);
        var grid = (DataGrid)workspace.FindName("ResultsGrid");
        grid.SelectedItems.Add(device); grid.SelectedItems.Add(hostile); grid.CurrentItem = hostile;
        Require(ReferenceEquals(workspace.EvidenceDevice, hostile), "Multi-selection details follow the focused device, not the first selected row.");
        workspace.SetView("services");
        var services = (DataGrid)workspace.FindName("ServicesGrid");
        object Service(string ip) => services.Items.Cast<object>().First(row => (string)row.GetType().GetProperty("IpAddress")!.GetValue(row)! == ip);
        services.SelectedItems.Add(Service(device.IpAddress)); services.SelectedItems.Add(Service(hostile.IpAddress)); services.CurrentItem = Service(hostile.IpAddress);
        Require(ReferenceEquals(workspace.EvidenceDevice, hostile), "Service details follow the focused service's actual device.");
        workspace.SetView("devices"); workspace.Session.Devices.Remove(device); workspace.Session.Devices.Remove(hostile);
        var assessmentTime = device.Identification.AssessedAtUtc;
        DeviceIdentification.ApplyManualType(device, "NAS");
        Require(device.Identification.EffectiveConfidence == "Manual" && IdentificationText.Describe(device).Contains("Unit42"), "Manual assignment keeps the original protocol evidence.");
        DeviceIdentification.ApplyManualType(device, null);
        Require(device.DeviceType == "Printer" && device.Identification.AssessedAtUtc == assessmentTime, "Clearing manual type does not fabricate a new scan.");
        foreach (Locale locale in Enum.GetValues(typeof(Locale)))
        {
            typeof(MainWindow).GetMethod("Lang_Checked", Instance)!.Invoke(window, new object[] { new RadioButton { Tag = locale.ToString() }, new RoutedEventArgs() });
            var resources = Application.Current.Resources.MergedDictionaries[locale == Locale.EnUS ? 1 : 2];
            foreach (string key in new[] { "Title", "Confidence", "None", "Low", "Medium", "High", "Manual", "Model", "Time", "Source", "Value", "Conflict", "Cached", "Note", "Automatic", "Disabled", "Quick", "Full", "Deep", "External", "Candidates" })
                Require(resources.Contains("Str_Evidence_" + key), locale + ": own evidence translation " + key);
            Require(IdentificationText.Describe(device).Contains((string)resources["Str_Evidence_High"]), locale + ": confidence uses the selected language.");
            var dialog = new DeviceEvidenceWindow(device);
            var text = ((DockPanel)dialog.Content).Children.OfType<TextBox>().Single();
            Require(text.IsReadOnly && text.Text.Contains("Unit42") && text.Text.Contains("2026-10-18 07:00:03 UTC"), "Read-only details preserve the stored model/time.");
            capture((FrameworkElement)dialog.Content, "Evidence-" + locale + ".png");
            dialog.Close();
        }
        Require(entry.Devices[0].Identification!.ManualType == null, "Later manual edits do not rewrite the history snapshot.");
        ScanHistory.SeedDemo(oldHistory);
        typeof(MainWindow).GetMethod("Lang_Checked", Instance)!.Invoke(window, new object[] { new RadioButton { Tag = Locale.EnUS.ToString() }, new RoutedEventArgs() });
        Console.WriteLine("PASS: evidence UI in 19 languages, stored/legacy history, manual provenance and safe CSV/HTML reports.");
    }
}
