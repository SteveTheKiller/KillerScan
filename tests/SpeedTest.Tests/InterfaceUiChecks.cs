using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Controls;
using KillerScan.Controls;

internal static class InterfaceUiChecks
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    internal static void Run(ScanWorkspace workspace)
    {
        var type = workspace.GetType();
        var assembly = type.Assembly;
        Require((bool)type.GetField("DemoMode", Instance)!.GetValue(workspace)!, "Interface UI tests use only demo data.");
        var profileType = assembly.GetType("KillerScan.Services.ScanProfile", true)!;
        object profile = JsonSerializer.Deserialize("{\"Name\":\"Interface fixture\",\"Target\":\"192.0.2.10\",\"InterfaceId\":\"fixture-adapter\",\"SourceAddress\":\"192.0.2.20\"}", profileType)!;
        type.GetMethod("LoadProfile", Instance)!.Invoke(workspace, new[] { profile });
        Require((string)type.GetProperty("SelectedAdapterId", Instance)!.GetValue(workspace)! == "fixture-adapter" &&
            (string)type.GetProperty("SelectedSourceAddress", Instance)!.GetValue(workspace)! == "192.0.2.20", "Profiles retain explicit interface identity and source address.");
        profile = JsonSerializer.Deserialize(JsonSerializer.Serialize(profile, profileType), profileType)!;
        Require((string)profileType.GetProperty("InterfaceId")!.GetValue(profile)! == "fixture-adapter", "Interface identity survives JSON persistence.");
        using (var stop = new CancellationTokenSource())
        {
            type.GetField("_rescanCts", Instance)!.SetValue(workspace, stop);
            type.GetMethod("UpdateDeepScanButton", Instance)!.Invoke(workspace, null);
            Require(!((Button)workspace.FindName("InterfaceSelectionButton")).IsEnabled, "Interface selection is locked during deep scans.");
            type.GetMethod("SelectInterface", Instance)!.Invoke(workspace, new object[] { "other", "192.0.2.30", false });
            Require((string)type.GetProperty("SelectedAdapterId", Instance)!.GetValue(workspace)! == "fixture-adapter", "Running scans cannot change interfaces.");
            type.GetField("_rescanCts", Instance)!.SetValue(workspace, null);
        }
        var legacy = JsonSerializer.Deserialize("{\"Name\":\"Old\",\"Target\":\"192.0.2.0/24\"}", profileType)!;
        type.GetMethod("LoadProfile", Instance)!.Invoke(workspace, new[] { legacy });
        Require((string)type.GetProperty("SelectedAdapterId", Instance)!.GetValue(workspace)! == "", "Old profiles preserve Automatic mode.");
        var cli = assembly.GetType("KillerScan.Features.Cli.CliRunner", true)!;
        using var output = new StringWriter(); using var error = new StringWriter();
        int result = (int)cli.GetMethod("RunDevices", Static)!.Invoke(null, new object[] { new List<string> { "192.0.2.10" },
            new Dictionary<string, string> { ["/interface"] = "fixture-adapter" }, output, error, false })!;
        Require(result == 2 && output.ToString().Length == 0, "CLI rejects incomplete interface choices before scanning.");
        type.GetMethod("UpdateDeepScanButton", Instance)!.Invoke(workspace, null);
        workspace.Scan(); // Restore synthetic devices after the selector deliberately cleared them.
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
