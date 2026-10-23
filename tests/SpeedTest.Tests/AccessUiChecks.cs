using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Engine;
using KillerScan.Services;
using KillerScan.Shell;
using AccessText = KillerScan.Services.AccessText;

internal static class AccessUiChecks
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static System.Collections.Generic.IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T found) yield return found;
            foreach (var nested in Children<T>(child)) yield return nested;
        }
    }
    internal static void Run(MainWindow window, Action<FrameworkElement, string> capture)
    {
        Require(DemoData.Enabled, "Access fixtures require isolated demo history.");
        var endpoint = new AccessEndpoint { Protocol = "https", Port = 9187, Verification = "HttpResponse", Authentication = "Required", Challenge = "Basic", HttpStatus = 401,
            ObservedAtUtc = new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero) };
        var device = new NetworkDevice { IpAddress = "192.0.2.40", Hostname = "access-fixture", OpenPorts = { 9187, 53 }, Access = new() { endpoint } };
        var old = ScanHistory.Entries.ToArray();
        var stored = ScanHistory.Record(device.IpAddress, new[] { device }).Current.Devices.Single();
        device.Access!.Clear();
        Require(stored.Access!.Single() == endpoint, "History owns its access list snapshot.");
        device.Access.Add(endpoint); ScanHistory.SeedDemo(old);
        string csv = ReportExport.BuildCsv(new[] { device });
        string html = ReportExport.BuildHtml(new[] { device }, device.IpAddress, "black");
        Require(csv.Contains("Access JSON") && csv.Contains("9187") && html.Contains("https://192.0.2.40:9187/"), "Exports retain the captured endpoint.");
        var workspace = (ScanWorkspace)typeof(MainWindow).GetField("_scanWorkspace", Instance)!.GetValue(window)!;
        var actionField = workspace.GetType().GetField("DeviceAction", Instance)!;
        object? original = actionField.GetValue(workspace); actionField.SetValue(workspace, null);
        ScanDeviceActionEventArgs? raised = null;
        workspace.DeviceAction += (_, e) => raised = e;
        try
        {
            workspace.OpenAccess(device, endpoint);
            Require(raised?.Action == "Browser" && raised.ServicePort == 9187 && ReferenceEquals(raised.Access, endpoint), "The existing open action receives the selected exact endpoint.");
            raised = null; workspace.OpenAccess(device, endpoint with { Protocol = "file" });
            Require(raised == null, "Unsupported schemes cannot launch a process.");
            workspace.Session.Devices.Add(device); workspace.SetView("services");
            var services = (DataGrid)workspace.FindName("ServicesGrid");
            var row = services.Items.Cast<object>().Single(item => (string)item.GetType().GetProperty("IpAddress")!.GetValue(item)! == device.IpAddress && (int)item.GetType().GetProperty("Port")!.GetValue(item)! == 9187);
            Require((string)row.GetType().GetProperty("BrowserAddress")!.GetValue(row)! == "https://192.0.2.40:9187/", "Services use the observed protocol on a nonstandard port.");
            services.SelectedItem = row; services.CurrentItem = row;
            workspace.GetType().GetMethod("ServicesGrid_ContextMenuOpening", Instance)!.Invoke(workspace, new object?[] { services, null });
            var browser = services.ContextMenu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "Browser");
            Require(browser.Visibility == Visibility.Visible, "The existing browser action is available for a verified nonstandard endpoint.");
            workspace.Session.Devices.Remove(device); workspace.SetView("devices");
        }
        finally { actionField.SetValue(workspace, original); }
        foreach (Locale locale in Enum.GetValues(typeof(Locale)))
        {
            typeof(MainWindow).GetMethod("Lang_Checked", Instance)!.Invoke(window, new object[] { new RadioButton { Tag = locale.ToString() }, new RoutedEventArgs() });
            var resources = Application.Current.Resources.MergedDictionaries[locale == Locale.EnUS ? 1 : 2];
            foreach (string key in new[] { "Title", "None", "Authentication", "Unknown", "Required", "LoginPage", "HttpResponse", "SshBanner", "TcpOpen", "PortHint", "Redirect", "Note", "NoTime" })
                Require(resources.Contains("Str_Access_" + key), locale + ": own access resource " + key);
            int opened = 0; string? copied = null;
            var dialog = new DeviceAccessWindow(device, chosen => { Require(chosen == endpoint, "Open uses the selected endpoint."); opened++; }, value => copied = value);
            Require(opened == 0 && copied == null, "Viewing access does not connect or copy automatically.");
            var buttons = Children<Button>((DependencyObject)dialog.Content).ToArray();
            var launch = buttons.Single(button => ReferenceEquals(button.Tag, endpoint));
            launch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var copy = buttons.Single(button => button.Tag as string == "https://192.0.2.40:9187/"); copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(opened == 1 && copied == "https://192.0.2.40:9187/", "Open and copy act only on request.");
            Require(buttons.Where(button => button.Tag is AccessEndpoint { Protocol: "tcp" }).All(button => !button.IsEnabled), "Unknown TCP services are copy-only.");
            Require(AccessText.Describe(endpoint).Contains((string)resources["Str_Access_Required"]), "Authentication uses the selected language.");
            capture((FrameworkElement)dialog.Content, "Access-" + locale + ".png"); dialog.Close();
        }
        typeof(MainWindow).GetMethod("Lang_Checked", Instance)!.Invoke(window, new object[] { new RadioButton { Tag = Locale.EnUS.ToString() }, new RoutedEventArgs() });
        Console.WriteLine("PASS: access UI in 19 languages, exact open/copy actions, nonstandard service routing and independent history/exports.");
    }
}
