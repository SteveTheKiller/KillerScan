using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerScan;
using KillerScan.Controls;
using KillerScan.Shell;
using KillerScan.Services;

internal static class SidebarPlacementTests
{
    internal static void Run()
    {
        App.SetSetting("SidebarSide", "Left");
        var window = new MainWindow();
        Layout(window);
        var railMenu = CheckEmptyRailMenu(window);
        var toggle = (Button)window.FindName("SidebarToggleBtn");
        var choices = Segments(railMenu);
        Require(choices.Length == 2, "The sidebar offers only left and right placement.");
        Require(System.Windows.Automation.AutomationProperties.GetAcceleratorKey(choices[0]) == "Alt+Shift+Left"
            && System.Windows.Automation.AutomationProperties.GetAcceleratorKey(choices[1]) == "Alt+Shift+Right",
            "Sidebar choices advertise the established family keyboard shortcuts.");
        Require(choices.All(item => item.Tag is string text && !string.IsNullOrWhiteSpace(text)),
            "Both sidebar placement choices have readable localized labels.");
        Require(IsOn(choices[0]) && !IsOn(choices[1]), "Left is the default side.");
        choices[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Layout(window);
        var rail = (FrameworkElement)window.FindName("RailButtons");
        var workspace = (FrameworkElement)window.FindName("WorkspaceHost");
        Require(rail.ActualWidth > 0 && workspace.ActualWidth > 0, "The headless host measured the real content.");
        var host = (FrameworkElement)window.FindName("ScaleHost");
        Require(rail.TranslatePoint(new Point(), host).X > workspace.TranslatePoint(new Point(), host).X,
            "Selecting Right places the sidebar rail after the workspace.");
        Require(App.GetSetting("SidebarSide") == "Right", "The chosen side is saved.");
        Require((string)toggle.Content == ((char)0xE76B).ToString(),
            "The collapsed right sidebar's chevron points left toward expansion.");
        ((Button)window.FindName("LocationsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require((string)toggle.Content == ((char)0xE76C).ToString(),
            "The expanded right sidebar's chevron points right toward collapse.");
        Require(((FrameworkElement)window.FindName("HistorySidebar")).HorizontalAlignment == HorizontalAlignment.Right,
            "The right panel wipes toward its outside edge while opening.");
        choices[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Layout(window);
        var column = (ColumnDefinition)window.FindName("SidebarCol");
        double width = column.ActualWidth;
        var grip = (Thumb)window.FindName("SidebarResizeGrip");
        grip.ApplyTemplate();
        Require(((FrameworkElement)grip.Template.FindName("hint", grip)).HorizontalAlignment == HorizontalAlignment.Left,
            "The right sidebar's resize line sits on its inner lip.");
        grip.RaiseEvent(new DragDeltaEventArgs(-24, 0) { RoutedEvent = Thumb.DragDeltaEvent });
        Layout(window);
        Require(Math.Abs(column.ActualWidth - width - 24) < 1,
            "Dragging the right sidebar's inner edge left grows its width.");
        CheckEmptyRailMenu(window);
        Require(Math.Abs(((FrameworkElement)window.FindName("HistorySidebar")).ActualWidth + 24 - column.ActualWidth) < 1,
            "Changing sides during the opening animation leaves the panel free to resize.");
        Require(((FrameworkElement)window.FindName("LocationsList")).Visibility == Visibility.Visible,
            "Moving and resizing the sidebar preserves the Locations section.");
        double savedWidth = column.ActualWidth;
        grip.RaiseEvent(new DragCompletedEventArgs(0, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
        choices[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Layout(window);
        Require(Math.Abs(column.ActualWidth - savedWidth) < 1
            && ((FrameworkElement)window.FindName("LocationsList")).Visibility == Visibility.Visible
            && rail.TranslatePoint(new Point(), host).X < workspace.TranslatePoint(new Point(), host).X,
            "Moving the expanded sidebar left preserves its width and section.");
        CheckEmptyRailMenu(window);
        choices[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        choices[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ThemeManager.Apply(Theme.SE98);
        Layout(window);
        Require(Math.Abs(column.ActualWidth - 20) < 0.1,
            "A collapsed sidebar follows the classic theme's 20 DIP rail on theme changes.");
        Require(workspace.Margin.Left == 2 && workspace.Margin.Right == 0,
            "The right sidebar mirrors the classic content pane's outside inset.");
        var band = ((Grid)workspace).Children.OfType<Grid>().Single(g => g.Children.OfType<UniformGrid>().Any());
        var pane = (FrameworkElement)window.FindName("DevicesPane");
        foreach (bool bottom in new[] { false, true })
        {
            band.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Segments(band.ContextMenu)[bottom ? 1 : 0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            foreach (var choice in choices)
            {
                choice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Layout(window);
                CheckEmptyRailMenu(window);
                double tabY = band.TranslatePoint(new Point(), workspace).Y;
                double paneY = pane.TranslatePoint(new Point(), workspace).Y;
                Require(band.ActualHeight > 0 && pane.ActualHeight > 100, "The tab band and pane have measured content.");
                // The joined tab seam overlaps the pane border by two DIPs.
                Require(bottom ? Math.Abs(tabY - paneY - pane.ActualHeight + 2) < 0.1
                    : Math.Abs(tabY + band.ActualHeight - paneY - 2) < 0.1,
                    "Top and bottom tabs keep their chosen edge on either sidebar side.");
                CheckFlyout(window, pane, ReferenceEquals(choice, choices[1]));
            }
        }
        var restored = new MainWindow();
        Layout(restored);
        var restoredToggle = (Button)restored.FindName("SidebarToggleBtn");
        var restoredMenu = CheckEmptyRailMenu(restored);
        var restoredRight = Segments(restoredMenu).Single(item => item.Uid == "SideRight");
        Require(IsOn(restoredRight)
            && ((FrameworkElement)restored.FindName("HistorySidebar")).Visibility == Visibility.Collapsed,
            "A new window restores the chosen side and keeps the established closed startup state.");
        restoredToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        restoredRight.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Layout(restored);
        Require(Math.Abs(((ColumnDefinition)restored.FindName("SidebarCol")).ActualWidth - (savedWidth - 4)) < 1,
            "A new window restores the saved panel width with the classic theme's thinner rail.");
        Console.WriteLine("PASS: sidebar placement, resizing, persistence, classic geometry and independent tabs (1 selected).");
    }

    /// <summary>The choice tiles on a card, in order: Left and Right, or Top and Bottom (tile Ids from ScanCards).</summary>
    private static Button[] Segments(ContextMenu menu) =>
        CardMenuTestHelpers.CardButtons(menu).OfType<Button>().Where(button => button.Uid.Length > 0).ToArray();

    /// <summary>A checked tile wears the selected-row fill (CardBuilder.ShowTileState).</summary>
    private static bool IsOn(Button tile) => tile.ReadLocalValue(Control.BackgroundProperty) != DependencyProperty.UnsetValue;

    private static ContextMenu CheckEmptyRailMenu(MainWindow window)
    {
        var host = (FrameworkElement)window.FindName("ScaleHost");
        var toggle = (Button)window.FindName("SidebarToggleBtn");
        var point = toggle.TranslatePoint(new Point(toggle.ActualWidth / 2, 120), host);
        var hit = VisualTreeHelper.HitTest(host, point)?.VisualHit;
        ContextMenu? menu = null;
        for (var node = hit; node != null && !ReferenceEquals(node, host); node = VisualTreeHelper.GetParent(node))
        {
            Require(node is not Button, "The empty rail point does not hit a button.");
            if (node is FrameworkElement element && element.ContextMenu != null) { menu = element.ContextMenu; break; }
        }
        Require(menu != null, "Right-clicking the empty space between rail buttons reaches the sidebar placement menu.");
        // WPF creates these event args internally before dispatching its public routed event.
        var opening = (ContextMenuEventArgs)Activator.CreateInstance(typeof(ContextMenuEventArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, new object[] { hit!, true }, null)!;
        ((UIElement)hit!).RaiseEvent(opening);
        Require(!opening.Handled, "The empty rail's context menu opening is not canceled.");
        menu!.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Require(Segments(menu).Count(item => item.Uid.StartsWith("Side")) == 2,
            "The empty rail menu offers the same Left and Right placement choices.");
        Require(menu.Items.Count == 2 && Segments(menu).Count(IsOn) == 1
            && Segments(menu).Single(IsOn).Uid == "Side" + App.GetSetting("SidebarSide"),
            "Repeated rail menu openings keep one current checked side without duplicating choices.");
        return menu;
    }

    private static void CheckFlyout(MainWindow window, FrameworkElement pane, bool right)
    {
        var button = (Button)window.FindName("HistoryButton");
        var menu = button.ContextMenu;
        FlyoutPlacement.Attach(menu, button);
        var root = (FrameworkElement)menu.PlacementTarget;
        var bounds = pane.TransformToAncestor(root).TransformBounds(new Rect(pane.RenderSize));
        var dpi = VisualTreeHelper.GetDpi(root);
        var halo = ((FrameworkElement)menu.Template.FindName("MenuRoot", menu)).Margin;
        var origin = menu.CustomPopupPlacementCallback(new Size(240, 180), root.RenderSize, new Point())[0].Point;
        double cardLeft = origin.X + halo.Left * dpi.DpiScaleX;
        double cardRight = origin.X + 240 - halo.Right * dpi.DpiScaleX;
        Require(bounds.Width > 500 && bounds.Height > 300, "The flyout callback uses a fully measured pane.");
        Require(right ? Math.Abs(cardRight - (bounds.Right - 8) * dpi.DpiScaleX) < 0.1
            : Math.Abs(cardLeft - (bounds.Left + 8) * dpi.DpiScaleX) < 0.1,
            "The visible flyout card follows the sidebar's edge with the established eight DIP inset.");
        Require(origin.Y + halo.Top * dpi.DpiScaleY >= bounds.Top * dpi.DpiScaleY - 0.1
            && origin.Y + 180 - halo.Bottom * dpi.DpiScaleY <= bounds.Bottom * dpi.DpiScaleY + 0.1,
            "The flyout card stays inside the pane above the footer.");
    }

    private static void Layout(MainWindow window)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(1200, 800));
        content.Arrange(new Rect(0, 0, 1200, 800));
        content.UpdateLayout();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
