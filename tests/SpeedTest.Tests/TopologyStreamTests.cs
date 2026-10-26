using System;
using System.Linq;
using System.Windows.Controls;
using KillerScan.Controls;
using KillerScan.Engine;

// A visible topology must not rebuild the whole diagram for every device a scan reports.
// Results arrive in bursts and one by one; either way the diagram is rebuilt a bounded number
// of times and ends up showing every device.
internal static class TopologyStreamTests
{
    internal static void Run()
    {
        using var workspace = new ScanWorkspace("10.0.0.0/22", false);
        workspace.SetView("topology");
        int before = workspace.TopologyRefreshes;

        // A burst: 500 devices arrive before the dispatcher gets a chance to draw.
        for (int i = 1; i <= 500; i++)
            workspace.Session.Devices.Add(new NetworkDevice { IpAddress = "10.0." + (i / 250) + "." + (i % 250 + 1) });
        Pump(TimeSpan.FromMilliseconds(800));
        int burst = workspace.TopologyRefreshes - before;
        Require(burst >= 1 && burst <= 3, "A burst of 500 devices rebuilds the diagram 1 to 3 times, not " + burst + ".");
        var canvas = (Canvas)workspace.FindName("TopologyCanvas");
        int nodes = canvas.Children.OfType<Border>().Count();
        Require(nodes >= 500, "After the burst the diagram shows every device; found " + nodes + " nodes.");

        // A trickle: 40 devices about 20 ms apart, as a slow scan reports them.
        before = workspace.TopologyRefreshes;
        for (int i = 0; i < 40; i++)
        {
            workspace.Session.Devices.Add(new NetworkDevice { IpAddress = "10.0.3." + (i + 1) });
            Pump(TimeSpan.FromMilliseconds(20));
        }
        Pump(TimeSpan.FromMilliseconds(800));
        int trickle = workspace.TopologyRefreshes - before;
        Require(trickle >= 1 && trickle <= 8, "40 devices over 0.8 s rebuild the diagram at most 8 times, not " + trickle + ".");
        Require(canvas.Children.OfType<Border>().Count() >= 540, "The final refresh shows the last device too.");

        // A drag-resize or the sidebar slide delivers dozens of size changes a second.
        var resized = typeof(ScanWorkspace).GetMethod("TopologyPane_SizeChanged",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Pump(TimeSpan.FromMilliseconds(400));
        before = workspace.TopologyRefreshes;
        for (int i = 0; i < 30; i++)
        {
            resized.Invoke(workspace, new object?[] { workspace, null });
            Pump(TimeSpan.FromMilliseconds(16));
        }
        Pump(TimeSpan.FromMilliseconds(800));
        int resizes = workspace.TopologyRefreshes - before;
        Require(resizes >= 1 && resizes <= 4, "30 size changes over half a second rebuild the diagram 1 to 4 times, not " + resizes + ".");

        workspace.SetView("devices");
        before = workspace.TopologyRefreshes;
        workspace.Session.Devices.Add(new NetworkDevice { IpAddress = "10.0.3.200" });
        Pump(TimeSpan.FromMilliseconds(500));
        Require(workspace.TopologyRefreshes == before, "A hidden topology is not rebuilt.");
        Console.WriteLine($"PASS: a visible topology rebuilds {burst} time(s) for a 500-device burst and {trickle} for a 40-device trickle.");
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
