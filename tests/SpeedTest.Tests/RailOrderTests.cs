using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerScan.Shell;

/// <summary>
/// The family rail order: theme, language and shortcuts hold the rail's bottom edge with theme
/// outermost, and the other rail buttons keep a saved order of their own.
/// </summary>
internal static class RailOrderTests
{
    private static readonly string[] Edge = ["ThemeButton", "LangButton", "ShortcutsButton"];
    private static readonly string[] Movable = ["SpeedTestButton", "LocationsButton", "HistoryButton"];

    public static Task Run()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Check(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
        return Task.CompletedTask;
    }

    private static void Check()
    {
        Same(RailOrder.Arrange(Movable, Edge, null),
            ["SpeedTestButton", "LocationsButton", "HistoryButton", "ShortcutsButton", "LangButton", "ThemeButton"],
            "The default rail ends with shortcuts, language and theme, theme outermost.");
        Same(RailOrder.Arrange(Movable, Edge, "HistoryButton,SpeedTestButton,LocationsButton"),
            ["HistoryButton", "SpeedTestButton", "LocationsButton", "ShortcutsButton", "LangButton", "ThemeButton"],
            "A saved order rearranges only the movable buttons.");
        Same(RailOrder.Arrange(Movable, Edge, "ThemeButton,Gone,HistoryButton,SpeedTestButton,HistoryButton"),
            ["HistoryButton", "LocationsButton", "SpeedTestButton", "ShortcutsButton", "LangButton", "ThemeButton"],
            "Saved edge buttons, unknown names and repeats are ignored.");
        foreach (string? empty in new[] { null, "", " , ," })
            Same(RailOrder.Arrange(Movable, Edge, empty), RailOrder.Arrange(Movable, Edge, null), "An empty saved order is the default.");
        Require(RailOrder.NearestSlot([12.0, 36.0, 60.0], 25) == 1 && RailOrder.NearestSlot([12.0, 36.0, 60.0], 500) == 2
            && RailOrder.NearestSlot([], 10) == -1, "A dragged button drops into the nearest slot.");

        // A rail laid out like KillerScan's: the saved order is restored onto it, each button keeps
        // its own gap, and an element that is not registered stays where it is.
        var rail = new StackPanel();
        var spacer = new Border { Name = "Spacer" };
        rail.Children.Add(spacer);
        foreach (string name in Movable.Concat(Enumerable.Reverse(Edge)))
            rail.Children.Add(new Button { Name = name, Margin = name == "ThemeButton" ? new Thickness(0) : new Thickness(0, 0, 0, 10) });
        string? saved = null;
        RailOrder.Attach(rail, Edge.Select(n => Find(rail, n)).ToList(), Movable.Select(n => Find(rail, n)).ToList(),
            "LocationsButton,HistoryButton,SpeedTestButton", value => saved = value);
        Same(rail.Children.OfType<FrameworkElement>().Select(e => e.Name).ToArray(),
            ["Spacer", "LocationsButton", "HistoryButton", "SpeedTestButton", "ShortcutsButton", "LangButton", "ThemeButton"],
            "The saved order is restored with the edge buttons at the bottom.");
        Require(Find(rail, "ThemeButton").Margin == new Thickness(0) && Find(rail, "SpeedTestButton").Margin.Bottom == 10,
            "Each button keeps its own gap.");
        Require(saved == null, "Restoring does not rewrite the saved order.");

        // KillerScan's own rail registers the same buttons, so a new rail button cannot be left out.
        Same(MainWindow.RailEdgeNames, Edge, "KillerScan's edge buttons are theme, language and shortcuts.");
        Same(MainWindow.RailMovableNames, Movable, "KillerScan's movable buttons are speed test, saved scans and history.");
    }

    private static FrameworkElement Find(Panel rail, string name)
        => rail.Children.OfType<FrameworkElement>().Single(e => e.Name == name);

    private static void Same(string[] actual, string[] expected, string message)
        => Require(actual.SequenceEqual(expected), message + " Got " + string.Join(",", actual));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
