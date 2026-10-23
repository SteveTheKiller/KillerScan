using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Engine;

internal static class ScanOptionsUiChecks
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;

    // Called by the existing offscreen/demo UI fixture; never saves real user data or scans.
    internal static void Run(ScanWorkspace workspace)
    {
        var assembly = typeof(KillerScan.App).Assembly;
        Require((bool)workspace.GetType().GetField("DemoMode", Instance)!.GetValue(workspace)!, "UI checks use demo data.");
        var optionsProperty = workspace.GetType().GetProperty("Options", Instance)!;
        var button = (Button)workspace.FindName("ScanOptionsButton");
        var menu = button.ContextMenu;
        var items = menu.Items.OfType<MenuItem>().Where(item => item.Tag is string).ToArray();
        Require(items.Select(item => (string)item.Tag).SequenceEqual(ScanOptions.Features), "Every engine choice is available in the GUI.");
        foreach (var item in items)
            Require(item.Header is string label && label.Length > 0 && !label.StartsWith("Str_"), "Option labels resolve through resources.");
        var dns = items.Single(item => Equals(item.Tag, "dns"));
        dns.IsChecked = false;
        dns.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(!((ScanOptions)optionsProperty.GetValue(workspace)!).ReverseDns, "Clicking DNS off changes the session options.");
        var running = workspace.GetType().GetField("_rescanCts", Instance)!;
        using (var cancellation = new CancellationTokenSource())
        {
            running.SetValue(workspace, cancellation);
            workspace.GetType().GetMethod("UpdateDeepScanButton", Instance)!.Invoke(workspace, null);
            Require(!button.IsEnabled && !menu.IsOpen, "Options are locked during a deep scan.");
            dns.IsChecked = true;
            dns.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(!((ScanOptions)optionsProperty.GetValue(workspace)!).ReverseDns, "An already open menu cannot change running choices.");
            running.SetValue(workspace, null);
            workspace.GetType().GetMethod("UpdateDeepScanButton", Instance)!.Invoke(workspace, null);
        }

        var profileType = assembly.GetType("KillerScan.Services.ScanProfile", true)!;
        var old = JsonSerializer.Deserialize("{\"Name\":\"Legacy\",\"Target\":\"192.0.2.10\"}", profileType)!;
        var profileOptions = profileType.GetProperty("Options")!;
        Require((ScanOptions)profileOptions.GetValue(old)! == new ScanOptions(), "Old profiles default to all enabled.");
        var choices = new ScanOptions().Without("dns,mdns,ports");
        profileOptions.SetValue(old, choices);
        var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(old, profileType), profileType)!;
        Require((ScanOptions)profileOptions.GetValue(roundTrip)! == choices, "Profiles preserve all options in JSON.");
        workspace.GetType().GetMethod("LoadProfile", Instance)!.Invoke(workspace, new[] { roundTrip });
        Require(workspace.Targets == "192.0.2.10" && (ScanOptions)optionsProperty.GetValue(workspace)! == choices,
            "Loading a profile restores targets and options without starting a scan.");
        workspace.GetType().GetMethod("RunProfile", Instance)!.Invoke(workspace, new[] { old });
        Require((ScanOptions)optionsProperty.GetValue(workspace)! == choices, "Running a demo profile restores the same options.");
        menu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require((ScanOptions)optionsProperty.GetValue(workspace)! == new ScanOptions(), "Restore defaults clears all disabled choices.");

        var cli = assembly.GetType("KillerScan.Features.Cli.CliRunner", true)!;
        var run = cli.GetMethod("RunDevices", Static)!;
        var parsed = ((List<string>, Dictionary<string, string>, string?))cli.GetMethod("ParseArgs", Static)!.Invoke(null,
            new object[] { new[] { "/scan", "192.0.2.10", "/skip", "dns", "--skip", "ports" }, "/scan" })!;
        Require(parsed.Item3 == null && parsed.Item2["/skip"] == "dns,ports", "Repeated skip options combine across aliases.");
        using var output = new StringWriter();
        using var error = new StringWriter();
        int invalid = (int)run.Invoke(null, new object[] { new List<string> { "192.0.2.10" },
            new Dictionary<string, string> { ["/skip"] = "dnss" }, output, error, false })!;
        Require(invalid == 2 && output.ToString().Length == 0, "Unknown scan options fail before any I/O.");
        int valid = (int)run.Invoke(null, new object[] { new List<string>(),
            new Dictionary<string, string> { ["/skip"] = "dns,mdns", ["/demo"] = "", ["/json"] = "" }, output, error, false })!;
        Require(valid == 0 && output.ToString().Contains("IpAddress"), "Demo CLI accepts the same feature names.");
    }

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException(message); }
}
