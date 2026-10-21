using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Engine;
using KillerScan.Services;

// The Access submenu lists each way into a device from what the scan recorded, and each entry
// opens with the existing browser, SSH or Remote Desktop action. Nothing here sends traffic,
// and no entry is clicked that would touch the real clipboard.
internal static class AccessUiTests
{
    internal static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(
            System.Windows.Threading.Dispatcher.CurrentDispatcher));
        bool demo = DemoData.Enabled;
        DemoData.Enabled = false;
        try
        {
            var seen = new DateTimeOffset(2026, 10, 21, 18, 30, 0, TimeSpan.Zero);
            var panel = new NetworkDevice
            {
                IpAddress = "192.0.2.10", OpenPorts = [22, 80, 8443, 9100], SshBanner = "SSH-2.0-OpenSSH_9.6",
                Access = [new AccessEndpoint { Protocol = "https", Port = 8443, Evidence = AccessEvidence.HttpResponse, HttpStatus = 200, Login = AccessLogin.LoginForm, ObservedAt = seen }],
            };
            var quiet = new NetworkDevice { IpAddress = "192.0.2.11" };

            using var workspace = new ScanWorkspace("192.0.2.0/24", false);
            workspace.LoadSnapshot("192.0.2.0/24", [panel, quiet]);
            var grid = (DataGrid)workspace.FindName("ResultsGrid");
            var menu = (MenuItem)workspace.FindName("AccessMenuItem");
            string Res(string key) => (string)Application.Current.FindResource(key);

            List<MenuItem> Open(NetworkDevice device)
            {
                grid.SelectedItem = device;
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, menu));
                return [.. menu.Items.OfType<MenuItem>()];
            }

            var items = Open(panel);
            var headers = items.Select(i => i.Header as string).ToList();
            string Item(string protocol, int port, string evidence) => string.Format(Res("Str_Access_Item"), protocol, port, evidence);
            Require(headers.SequenceEqual(
                [
                    Item("SSH", 22, Res("Str_Access_Banner")), Item("HTTP", 80, Res("Str_Access_PortOpen")),
                    Item("HTTPS", 8443, Res("Str_Access_LoginForm")), Item("TCP", 9100, Res("Str_Access_PortOpen")),
                    Res("Str_Access_CopyMenu"),
                ]), "Access lists every endpoint by port, then Copy Address: " + string.Join(" | ", headers));
            var copy = items.Last();
            Require(copy.Items.OfType<MenuItem>().Select(i => (string)i.Header).SequenceEqual(
                    ["192.0.2.10:22", "http://192.0.2.10:80/", "https://192.0.2.10:8443/", "192.0.2.10:9100"]),
                "Copy Address offers each endpoint's address.");
            string tip = (string)items[2].ToolTip;
            Require(tip.Contains(Res("Str_Access_Tip_LoginForm")) && tip.Contains(string.Format(Res("Str_Access_Tip_Seen"), seen.ToLocalTime().ToString("g"))),
                "A recorded response explains itself and says when it was seen: " + tip);
            Require(((string)items[1].ToolTip).StartsWith(Res("Str_Access_Tip_PortOpen")) && ((string)items[3].ToolTip).Contains(Res("Str_Access_Tip_Copy")),
                "Port-only endpoints say the protocol is a guess, and unknown ones say a click copies.");

            // Clicking opens through the existing actions, with the endpoint's port.
            var raised = new List<(string Action, int? Port)>();
            workspace.DeviceAction += (_, e) => raised.Add((e.Action, e.ServicePort));
            items[2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            items[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(raised.SequenceEqual([("Browser", (int?)8443), ("Ssh", (int?)22)]), "Endpoints open with the browser and SSH actions on their port.");

            Require(Open(quiet).Single() is { IsEnabled: false } none && (string)none.Header == Res("Str_Access_None"), "A device with no open ports says so.");

            // Open in Browser uses the endpoint that answered, not plain http://address.
            Require(ScanWorkspace.BrowserAddress(panel) == "https://192.0.2.10:8443/", "Open in Browser prefers the web endpoint that answered.");
            Require(ScanWorkspace.BrowserAddress(panel, 80) == "http://192.0.2.10:80/" && ScanWorkspace.BrowserAddress(panel, 9100) == null,
                "A service opens with its own port's scheme, and a non-web port does not open in a browser.");
            Require(ScanWorkspace.BrowserAddress(quiet) == "http://192.0.2.11/", "With no web port, Open in Browser falls back to http://address/.");

            // Exports carry the same evidence.
            var lines = ReportExport.BuildCsv([panel]).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            Require(lines[0].EndsWith(",Confidence,Evidence,Access") &&
                    lines[1].EndsWith("\"ssh:22 banner; http:80 open; https:8443 200 login-form; tcp:9100 open\""),
                "CSV ends with an Access column: " + lines[1]);
            string html = ReportExport.BuildHtml([panel], "192.0.2.0/24", "dark");
            Require(html.Contains(System.Net.WebUtility.HtmlEncode(Item("HTTPS", 8443, Res("Str_Access_LoginForm")))), "The HTML ports cell lists the endpoints on hover.");
        }
        finally { DemoData.Enabled = demo; }
        Require(DisposedWorkspaceIsCollected(), "A closed scan workspace is not kept alive by its font hooks or other handlers.");
        Console.WriteLine("PASS: the Access menu lists each endpoint, opens it with the right action and claims no login.");
    }

    private static bool DisposedWorkspaceIsCollected()
    {
        WeakReference Make()
        {
            var workspace = new ScanWorkspace("192.0.2.0/24", false);
            workspace.Dispose();
            return new WeakReference(workspace);
        }
        var weak = Make();
        for (int i = 0; i < 5 && weak.IsAlive; i++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        return !weak.IsAlive;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
