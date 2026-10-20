using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerScan.Controls;
using KillerScan.Services;

internal static class WatchActionTests
{
    internal static void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        DemoData.Enabled = true;
        using var watch = new NetworkToolsWindow("192.0.2.1", 1);
        var start = (Button)watch.FindName("StartButton");
        var stop = (Button)watch.FindName("StopButton");
        var cards = (ListBox)watch.FindName("Cards");
        var targets = (TextBox)watch.FindName("Targets");
        start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var first = cards.Items.OfType<WatchCard>().Single();
        Require(first.Sent == "1" && !start.IsEnabled, "A real watch run has recorded its first sample.");
        watch.IncludeTarget("192.0.2.2");
        Require(cards.Items.OfType<WatchCard>().Any(card => card.Address == "192.0.2.2"), "Keep Alive adds a card during an active run.");
        watch.ShowDetails("192.0.2.3", new[] { 8443 });
        var diagnosed = cards.Items.OfType<WatchCard>().Single(card => card.Address == "192.0.2.3");
        Require(ReferenceEquals(cards.SelectedItem, diagnosed) && diagnosed.Checks.Any(row => row.Check == "TCP 8443"),
            "Diagnose adds, selects and inspects the requested target with its known ports.");
        watch.IncludeTarget("192.0.2.2");
        Require(cards.Items.Count == 3 && targets.Text.Contains("192.0.2.2") && targets.Text.Contains("192.0.2.3"), "Repeated requests are deduplicated and the target box reflects additions.");
        PumpUntil(() => diagnosed.Sent != "0", TimeSpan.FromSeconds(4));
        Require(ReferenceEquals(cards.Items[0], first) && int.Parse(first.Sent) >= 2, "Existing cards and counters continue without a reset while new cards receive samples.");
        for (int host = 4; host <= 16; host++) watch.IncludeTarget("192.0.2." + host);
        Require(cards.Items.Count == 16, "The supported target limit remains available while watching.");
        string accepted = targets.Text;
        watch.IncludeTarget("192.0.2.17");
        watch.IncludeTarget("invalid");
        Require(cards.Items.Count == 16 && targets.Text == accepted && ((TextBlock)watch.FindName("Status")).Text.Length > 0,
            "Unsupported additions give visible validation feedback without disturbing accepted targets.");
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => start.IsEnabled, TimeSpan.FromSeconds(1));
        Require(!stop.IsEnabled, "Stop ends the updated watch run.");
        Console.WriteLine("PASS: active Keep Alive and Diagnose requests add targets without resetting existing counters.");
    }
    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start(); Dispatcher.PushFrame(frame);
        }
        Require(condition(), "The watch command completes within its controlled timeout.");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
