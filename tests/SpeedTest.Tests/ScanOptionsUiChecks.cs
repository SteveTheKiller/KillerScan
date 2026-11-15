using System;
using System.Collections.Generic;
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
        var menu = Open(button);
        Require(menu.IsOpen && ReferenceEquals(menu.PlacementTarget, button),
            "Scan settings opens from its own button.");
        var probes = Chips(menu).Where(chip => chip.Tag is string).ToList();
        Require(probes.Count == 13, "All thirteen probe choices are available.");
        Require(probes.All(chip => chip.IsChecked == true), "All probe choices start enabled.");
        Require(probes.All(chip => chip.ToolTip != null && ToolTipService.GetShowOnDisabled(chip)),
            "Every probe chip explains itself, even while it is unavailable.");
        var unknownPorts = Chips(menu).Where(chip => chip.Tag is null).ToList();
        Require(unknownPorts.Count == 1 && unknownPorts[0].IsChecked != true,
            "Fingerprinting unknown ports is one separate choice that starts off.");
        menu.IsOpen = false;

        var profile = JsonSerializer.Deserialize<ScanProfile>(
            "{\"Name\":\"Settings fixture\",\"Target\":\"127.0.0.1\",\"Options\":{\"Http\":false}}")!;
        Require(DemoData.Enabled, "Profile fixture is isolated from saved user profiles.");
        Require(ScanProfiles.Save(profile), "A profile can be saved with its probe choices.");
        var saved = JsonSerializer.Deserialize<ScanProfile>(JsonSerializer.Serialize(
            ScanProfiles.Items.Single(item => item.Name == profile.Name)))!;
        workspace.RunProfile(saved);
        menu = Open(button);
        Require(Chips(menu).Single(chip => Equals(chip.Tag, "http")).IsChecked != true,
            "Running a saved profile restores its disabled HTTP check.");
        var choices = Chips(menu).Where(chip => chip.Tag is string).ToDictionary(chip => (string)chip.Tag);
        var unknown = Chips(menu).Single(chip => chip.Tag is null);
        Toggle(unknown, true);
        Require(unknown.IsChecked == true && unknown.IsEnabled && menu.IsOpen,
            "Fingerprinting unknown ports can be turned on, and the card stays open.");
        Toggle(choices["ports"], false);
        Require(!choices["http"].IsEnabled && !choices["tls"].IsEnabled && !choices["ssh"].IsEnabled
            && choices["snmp"].IsEnabled && choices["netbios"].IsEnabled && !unknown.IsEnabled,
            "Disabling ports makes only port-dependent checks unavailable.");
        string reset = (string)Application.Current.FindResource("Str_Card_Reset");
        var resetButton = Descendants(menu).OfType<Button>().Single(b => Equals(b.Tag, reset));
        resetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        menu = Open(button);
        Require(Chips(menu).Where(chip => chip.Tag is string).All(chip => chip.IsChecked == true && chip.IsEnabled)
                && Chips(menu).Single(chip => chip.Tag is null) is { IsEnabled: true } off && off.IsChecked != true,
            "Reset turns every check back on and turns unknown-port fingerprinting off.");
        menu.IsOpen = false;
        workspace.LoadProfile(JsonSerializer.Deserialize<ScanProfile>("{\"Target\":\"127.0.0.1\",\"Options\":null}")!);
        menu = Open(button);
        Require(Chips(menu).Where(chip => chip.Tag is string).All(chip => chip.IsChecked == true),
            "A legacy or null-options profile retains enabled defaults.");
        menu.IsOpen = false;
    }

    private static ContextMenu Open(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var menu = button.ContextMenu;
        Require(menu != null, "Scan settings has a card.");
        // The offscreen host has no presentation source to raise the popup's opened event.
        menu!.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        return menu;
    }

    // A real click flips IsChecked before the Click event, so do the same.
    private static void Toggle(ToggleButton chip, bool on)
    {
        chip.IsChecked = on;
        chip.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }

    private static List<ToggleButton> Chips(ContextMenu menu) =>
        Descendants(menu).OfType<ToggleButton>().Where(t => t is not RadioButton).ToList();

    private static List<DependencyObject> Descendants(DependencyObject root)
    {
        var found = new List<DependencyObject>();
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            {
                found.Add(child);
                pending.Push(child);
            }
        }
        return found;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
