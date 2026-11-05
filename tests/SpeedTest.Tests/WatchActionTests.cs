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

        // F3 on a window that is not watching anything yet starts the run, so the card appears.
        using (var fresh = new NetworkToolsWindow("", 1))
        {
            var freshCards = (ListBox)fresh.FindName("Cards");
            var freshStop = (Button)fresh.FindName("StopButton");
            fresh.ShowDetails("192.0.2.9", new[] { 8080 });
            var card = freshCards.Items.OfType<WatchCard>().SingleOrDefault(c => c.Address == "192.0.2.9");
            Require(card != null && ReferenceEquals(freshCards.SelectedItem, card) && card.Checks.Any(row => row.Check == "TCP 8080") && freshStop.IsEnabled,
                "Diagnose on a fresh Network Tools window starts watching and shows the target's card with its checks.");
            freshStop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(() => !freshStop.IsEnabled, TimeSpan.FromSeconds(1));
        }
        CheckStopThenCheckAgain();
        CheckLanguageChangeAfterStop();
        CheckCheckedAtTime();
        CheckShellCommands();
        Console.WriteLine("PASS: active Keep Alive and Diagnose requests add targets without resetting existing counters.");
    }
    private static void CheckStopThenCheckAgain()
    {
        using var watch = new NetworkToolsWindow("", 1);
        var stop = (Button)watch.FindName("StopButton");
        var cards = (ListBox)watch.FindName("Cards");
        watch.ShowDetails("192.0.2.9", new[] { 8080 });
        var card = cards.Items.OfType<WatchCard>().Single(c => c.Address == "192.0.2.9");
        Require(card.Checks.Count > 0, "The first diagnosis fills the card's rows.");
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !stop.IsEnabled, TimeSpan.FromSeconds(1));
        // Check again after Stop must run on a fresh token rather than the canceled one.
        var click = typeof(NetworkToolsWindow).GetMethod("CardDiagnose_Click", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        click.Invoke(watch, new object[] { new FrameworkElement { DataContext = card }, new RoutedEventArgs() });
        PumpUntil(() => card.Checks.Any(row => row.Check == "TCP 8080"), TimeSpan.FromSeconds(2));
        Require(card.Checks.Count > 1, "Check again after Stop repopulates the card instead of wiping it.");
    }    private static void CheckLanguageChangeAfterStop()
    {
        using var watch = new NetworkToolsWindow("", 1);
        var stop = (Button)watch.FindName("StopButton");
        var cards = (ListBox)watch.FindName("Cards");
        watch.ShowDetails("192.0.2.9", new[] { 8080 });
        var card = cards.Items.OfType<WatchCard>().Single(c => c.Address == "192.0.2.9");
        stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        PumpUntil(() => !stop.IsEnabled, TimeSpan.FromSeconds(1));
        PumpUntil(() => card.Checks.Count > 1, TimeSpan.FromSeconds(1));
        var marker = new CheckRow { Check = "marker" };
        card.Checks.Add(marker);
        typeof(NetworkToolsWindow).GetMethod("OnLocaleChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(watch, null);
        Pump(TimeSpan.FromMilliseconds(400));
        Require(card.Checks.Contains(marker), "Changing language after Stop leaves the stopped run's rows alone instead of rerunning every check.");
    }    private static void CheckCheckedAtTime()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            // fi-FI writes a time separator of "." so a culture-formatted export would differ from the invariant one.
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fi-FI");
            using var watch = new NetworkToolsWindow("", 1);
            var stop = (Button)watch.FindName("StopButton");
            var cards = (ListBox)watch.FindName("Cards");
            watch.ShowDetails("192.0.2.9", new[] { 8080 });
            var card = cards.Items.OfType<WatchCard>().Single(c => c.Address == "192.0.2.9");
            PumpUntil(() => card.Checks.Count > 1, TimeSpan.FromSeconds(2));
            var timeRow = card.Checks.Last();
            Require(System.Text.RegularExpressions.Regex.IsMatch(timeRow.Result, @"^\d\d[:.]\d\d[:.]\d\d$"),
                "The Checked at row is 24-hour hh:mm:ss like the event log, not 12-hour with AM/PM (" + timeRow.Result + ").");
            string stamp = timeRow.ExportResult;
            Require(System.Text.RegularExpressions.Regex.IsMatch(stamp, @"^\d\d:\d\d:\d\d$"), "The Checked at export value is invariant 24-hour (" + stamp + ").");
            Require(watch.BuildCsv().Contains(stamp) && watch.BuildHtml().Contains(stamp) && watch.BuildText().Contains(stamp),
                "CSV, HTML and text exports write the Checked at time in the invariant form.");
            stop.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }    private static void CheckShellCommands()
    {
        var previous = DemoData.Current;
        DemoData.Generate(new Random(1729));
        var shell = new KillerScan.Shell.MainWindow();
        try
        {
            var added = new KillerScan.Engine.NetworkDevice { IpAddress = "192.0.2.200" };
            shell.OpenNetworkTool(added, false);
            var watch = Descendants((DependencyObject)shell.Content).OfType<NetworkToolsWindow>().Single();
            var cards = (ListBox)watch.FindName("Cards");
            PumpUntil(() => cards.Items.Count > 0, TimeSpan.FromSeconds(1));
            var first = cards.Items.OfType<WatchCard>().First();
            PumpUntil(() => int.Parse(first.Sent) >= 2, TimeSpan.FromSeconds(4));
            int samples = int.Parse(first.Sent);
            shell.OpenNetworkTool(new KillerScan.Engine.NetworkDevice { IpAddress = "192.0.2.201", OpenPorts = [8443] }, true);
            PumpUntil(() => ((Button)watch.FindName("StartButton")).IsEnabled == false &&
                cards.Items.OfType<WatchCard>().Any(card => card.Address == "192.0.2.201" && card.Sent != "0"), TimeSpan.FromSeconds(4));
            Require(cards.Items.OfType<WatchCard>().Any(card => ReferenceEquals(card, first)) && int.Parse(first.Sent) >= samples,
                "The shell Diagnose command preserves existing card identity and samples.");
            Require(cards.Items.OfType<WatchCard>().Any(card => card.Address == added.IpAddress),
                "The shell Diagnose command preserves a previously added Keep Alive target.");
            var diagnosed = cards.Items.OfType<WatchCard>().Single(card => card.Address == "192.0.2.201");
            Require(ReferenceEquals(cards.SelectedItem, diagnosed) && diagnosed.Checks.Any(row => row.Check == "TCP 8443"),
                "The shell Diagnose command selects and checks the new target.");
            shell.OpenNetworkTool(added, false);
            Require(cards.Items.OfType<WatchCard>().Any(card => ReferenceEquals(card, first)) &&
                cards.Items.OfType<WatchCard>().Count(card => card.Address == added.IpAddress) == 1 &&
                cards.Items.OfType<WatchCard>().Any(card => ReferenceEquals(card, diagnosed)),
                "Repeated shell Keep Alive commands preserve all cards without duplication.");
        }
        finally { shell.Close(); DemoData.Current = previous; }
    }
    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
    private static void Pump(TimeSpan span)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = span };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
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
