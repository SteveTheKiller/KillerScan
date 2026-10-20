using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using KillerScan.Controls;
using KillerScan.Engine;

internal static class ExportActionTests
{
    internal static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "KillerScan-export-actions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var workspace = new ScanWorkspace();
            workspace.LoadSnapshot("192.0.2.0/24", [new NetworkDevice { IpAddress = "192.0.2.1", Hostname = "device-a", OpenPorts = [80, 443] },
                new NetworkDevice { IpAddress = "192.0.2.2", Hostname = "device-b", OpenPorts = [22] }]);
            workspace.SetView("services");
            ((TextBox)workspace.FindName("FilterInput")).Text = "device-a";
            var button = (Button)workspace.FindName("ExportButton");
            workspace.Export();
            button.ContextMenu.IsOpen = false;
            var csvItems = button.ContextMenu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible && item.InputGestureText == "Ctrl+E").ToArray();
            Require(csvItems.Length == 1, "Services must offer exactly one CSV export action for Ctrl+E.");
            Require(((MenuItem)workspace.FindName("ExportHtmlItem")).Visibility == Visibility.Collapsed, "Services must not advertise an unsupported HTML export.");
            int dialogs = 0;
            string path = Path.Combine(directory, "menu.csv");
            workspace.ExportDialog = dialog => { dialogs++; dialog.FileName = path; return true; };
            csvItems[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            string menu = File.ReadAllText(path);
            Require(menu.StartsWith("Service,Port,Name,IP Address,Type") && menu.Contains("device-a") && !menu.Contains("device-b"), "The menu exports the filtered Services dataset with one row per port.");
            int rows = ((DataGrid)workspace.FindName("ServicesGrid")).Items.Count;
            Require(menu.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Length == rows + 1, "Export row count matches the visible Services rows.");
            path = Path.Combine(directory, "shortcut.csv");
            workspace.Export("csv");
            Require(File.ReadAllText(path) == menu, "Menu and keyboard context commands produce the same service CSV.");
            workspace.Export("html");
            Require(dialogs == 2, "An unsupported service format does not open a save dialog.");
            string tooltip = button.ToolTip is TextBlock text ? new TextRange(text.ContentStart, text.ContentEnd).Text : button.ToolTip?.ToString() ?? "";
            Require(tooltip.Contains("Ctrl+E") && !tooltip.Contains("HTML") && !tooltip.Contains("Ctrl+Shift+E"), "The Services tooltip lists only available shortcuts.");
            workspace.SetView("devices");
            workspace.Export(); button.ContextMenu.IsOpen = false;
            Require(Visible().Length == 2 && Visible().Any(item => item.Name == "ExportHtmlItem"), "Devices retains its CSV and HTML actions.");
            path = Path.Combine(directory, "devices.csv");
            ((MenuItem)workspace.FindName("ExportCsvItem")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(File.ReadAllText(path).StartsWith("IP Address,Hostname,MAC Address,Vendor,Type,Open Ports"), "The same CSV gesture uses the device schema in Devices.");
            workspace.SetView("topology");
            workspace.Export(); button.ContextMenu.IsOpen = false;
            Require(Visible().Select(item => item.Name).OrderBy(name => name).SequenceEqual(new[] { "ExportTopologyJpegItem", "ExportTopologyPngItem", "ExportTopologySvgItem" }), "Topology offers only its three supported snapshot formats.");
            var signals = new List<string>();
            workspace.ShellExportRequested += (_, format) => signals.Add(format);
            workspace.ExportContext = "watch";
            workspace.Export(); button.ContextMenu.IsOpen = false;
            foreach (var item in Visible()) item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(signals.SequenceEqual(new[] { "watch-csv", "watch-html", "watch-txt", "watch-png" }), "Watch menu actions share context dispatch without opening device writers.");
            signals.Clear(); workspace.ExportContext = "terminal";
            workspace.Export(); button.ContextMenu.IsOpen = false;
            Require(Visible().Length == 1, "Terminal exposes only its text export.");
            Visible()[0].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(signals.SequenceEqual(new[] { "terminal-txt" }), "Terminal menu dispatch reaches its own writer.");
            signals.Clear(); workspace.ExportContext = "history"; workspace.Export("csv");
            Require(signals.SequenceEqual(new[] { "history-csv" }), "History CSV retains the shell-owned export route.");

            MenuItem[] Visible() => button.ContextMenu.Items.OfType<MenuItem>().Where(item => item.Visibility == Visibility.Visible).ToArray();
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: service export menu, tooltip and keyboard command share the filtered dataset.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
