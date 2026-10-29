using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using KillerScan.Controls;
using KillerScan.Services;

internal static class ScanOptionsUiChecks
{
    internal static void Run(ScanWorkspace workspace)
    {
        var button = workspace.FindName("ScanOptionsButton") as Button;
        Require(button != null, "Scan settings is available beside Scan.");
        var scan = (Button)workspace.FindName("ScanBtn");
        var row = (Panel)scan.Parent;
        Require(row.Children.IndexOf(button!) == row.Children.IndexOf(scan) + 1,
            "Scan settings immediately follows Scan.");
        Require(!string.IsNullOrWhiteSpace(AutomationProperties.GetName(button!)),
            "Scan settings has an accessible name.");
        // Icon-only since the scan bar moved into the title bar; the name is its tooltip.
        Require(button!.Content is Panel content && content.Children.OfType<TextBlock>().Any(t => t.Text == "")
                && button.ToolTip != null,
            "Scan settings shows its gear icon with a tooltip.");
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var menu = button.ContextMenu;
        // The offscreen host has no presentation source to raise the popup's opened event.
        menu?.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Require(menu != null && menu.IsOpen && ReferenceEquals(menu.PlacementTarget, button),
            "Scan settings opens from its own button.");
        var probes = menu!.Items.OfType<MenuItem>().Where(item => item.IsCheckable && item.Tag is string).ToList();
        Require(probes.Count == 13, "All thirteen probe choices are available.");
        Require(probes.All(item => item.IsChecked), "All probe choices start enabled.");
        var unknownPorts = menu.Items.OfType<MenuItem>().Where(item => item.IsCheckable && item.Tag is null).ToList();
        Require(unknownPorts.Count == 1 && !unknownPorts[0].IsChecked,
            "Fingerprinting unknown ports is one separate choice that starts off.");
        menu.IsOpen = false;

        var profile = JsonSerializer.Deserialize<ScanProfile>(
            "{\"Name\":\"Settings fixture\",\"Target\":\"127.0.0.1\",\"Options\":{\"Http\":false}}")!;
        Require(DemoData.Enabled, "Profile fixture is isolated from saved user profiles.");
        Require(ScanProfiles.Save(profile), "A profile can be saved with its probe choices.");
        var saved = JsonSerializer.Deserialize<ScanProfile>(JsonSerializer.Serialize(
            ScanProfiles.Items.Single(item => item.Name == profile.Name)))!;
        workspace.RunProfile(saved);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        menu = button.ContextMenu;
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Require(!menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "http")).IsChecked,
            "Running a saved profile restores its disabled HTTP check.");
        var choices = menu.Items.OfType<MenuItem>().Where(item => item.IsCheckable && item.Tag is string)
            .ToDictionary(item => (string)item.Tag);
        var unknown = menu.Items.OfType<MenuItem>().Single(item => item.IsCheckable && item.Tag is null);
        unknown.IsChecked = true;
        unknown.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(unknown.IsChecked && unknown.IsEnabled, "Fingerprinting unknown ports can be turned on.");
        choices["ports"].IsChecked = false;
        choices["ports"].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(!choices["http"].IsEnabled && !choices["tls"].IsEnabled && !choices["ssh"].IsEnabled
            && choices["snmp"].IsEnabled && choices["netbios"].IsEnabled && !unknown.IsEnabled,
            "Disabling ports makes only port-dependent checks unavailable.");
        menu.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(choices.Values.All(item => item.IsChecked && item.IsEnabled) && !unknown.IsChecked && unknown.IsEnabled,
            "Restore defaults immediately updates every visible check and turns unknown-port fingerprinting off.");
        menu.IsOpen = false;
        workspace.LoadProfile(JsonSerializer.Deserialize<ScanProfile>("{\"Target\":\"127.0.0.1\",\"Options\":null}")!);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        menu = button.ContextMenu;
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Require(menu.Items.OfType<MenuItem>().Where(item => item.IsCheckable && item.Tag is string).All(item => item.IsChecked),
            "A legacy or null-options profile retains enabled defaults.");
        menu.IsOpen = false;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
