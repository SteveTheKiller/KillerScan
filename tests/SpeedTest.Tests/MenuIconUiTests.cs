using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml;
using KillerScan.Controls;
using KillerScan.Shell;

internal static partial class HistoryUiTests
{
    private static string PreserveMenuGlyphReferences(string markup)
    {
        // The .NET Framework loose parser loses private-use characters in a raw string.
        // Retain the numeric references used by the compiled application XAML.
        foreach (char glyph in markup.Where(c => c >= 0xE000 && c <= 0xF8FF).Distinct().ToArray())
            markup = markup.Replace(glyph.ToString(), "&#x" + ((int)glyph).ToString("X4") + ";");
        return markup;
    }

    private static void CheckMenuIcons(MainWindow window, Assembly assembly, string source, Action<string> theme)
    {
        // DemoData and the caller's redirected registry isolate these checks from saved settings
        // and targets. Constructing a terminal or an empty Keep Alive view starts no process or scan.
        var app = Application.Current;
        var menus = new Dictionary<string, ContextMenu>();
        var history = new HistoryWorkspace();
        menus.Add("History", ((DataGrid)history.FindName("HistoryChangesGrid")).ContextMenu);
        var terminal = (FrameworkElement)Activator.CreateInstance(assembly.GetType("KillerScan.Terminal.TerminalControl", true)!)!;
        menus.Add("Terminal", terminal.ContextMenu);
        var watch = (FrameworkElement)Activator.CreateInstance(assembly.GetType("KillerScan.Controls.NetworkToolsWindow", true)!,
            Instance, null, new object[] { "", 1.0 }, null)!;
        menus.Add("KeepAlive", ((FrameworkElement)((DataTemplate)watch.FindResource("WatchCardTemplate")).LoadContent()).ContextMenu);
        var scan = (ScanWorkspace)Field(window, "_scanWorkspace")!;
        var topology = ((Button)scan.FindName("TopologyOrderButton")).ContextMenu;
        if (topology.Items.Count == 1) Call(scan, "LoadTopologySettings");
        Require(topology.Items.OfType<MenuItem>().Count() >= 10, "Topology includes its Loaded-built settings, zoom and export groups.");
        menus.Add("Topology", topology);
        menus.Add("Devices", ((DataGrid)scan.FindName("ResultsGrid")).ContextMenu);
        menus.Add("Services", ((DataGrid)scan.FindName("ServicesGrid")).ContextMenu);
        menus.Add("Export", ((Button)scan.FindName("ExportButton")).ContextMenu);

        // Load the actual Sort menu without navigating user directories; its styles are the family Controls.xaml.
        var xml = new XmlDocument(); xml.Load(Path.Combine(source, "Controls", "FileDialog.xaml"));
        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("p", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        ns.AddNamespace("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        var sort = (XmlElement)xml.SelectSingleNode("//p:ContextMenu[@x:Name='SortMenu']", ns)!;
        foreach (XmlElement node in sort.SelectNodes(".//*", ns)!)
        {
            node.RemoveAttribute("Click"); node.RemoveAttribute("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        }
        sort.RemoveAttribute("Name", "http://schemas.microsoft.com/winfx/2006/xaml");
        var parser = new ParserContext();
        parser.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        parser.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        // FileDialog.xaml maps its glyph namespace without an assembly; the parser needs it named.
        string sortXaml = sort.OuterXml
            .Replace("clr-namespace:KillerFamily.Accessibility\"", "clr-namespace:KillerFamily.Accessibility;assembly=KillerScan\"")
            .Replace("clr-namespace:KillerScan.Controls\"", "clr-namespace:KillerScan.Controls;assembly=KillerScan\"");
        menus.Add("Sort", (ContextMenu)XamlReader.Parse(PreserveMenuGlyphReferences(sortXaml), parser));

        // Build the actual dynamic menus without needing a visible desktop popup or any action.
        menus.Add("HistoryExport", (ContextMenu)Call(history, "CreateExportMenu", (FrameworkElement)scan.FindName("ExportButton"))!);
        var root = (FrameworkElement)window.Content; Render(root, 640, 780); Call(window, "FitToolbarViews");
        foreach (var shortcut in new[] { (Key.Apps, ModifierKeys.None), (Key.F10, ModifierKeys.Shift) })
        {
            Require(history.HandleShortcut(shortcut.Item1, shortcut.Item2), "History supports keyboard menu opening.");
            var menu = ((DataGrid)history.FindName("HistoryChangesGrid")).ContextMenu;
            Require(menu.IsOpen && menu.PlacementTarget != null, "Keyboard opening anchors the actual menu.");
            menu.IsOpen = false;
        }

        string output = Path.Combine(Path.GetTempPath(), "KillerScan-menu-icons-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var font = new GlyphTypeface(new Uri(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "segmdl2.ttf")));
        foreach (string name in Enum.GetNames(assembly.GetType("KillerScan.Services.Theme", true)!))
        {
            theme(name);
            foreach (var pair in menus)
                CheckMenuLevel(pair.Value, pair.Value.Items.Cast<UIElement>().ToArray(), pair.Key, name, output, font);
        }
        (terminal as IDisposable)?.Dispose(); (watch as IDisposable)?.Dispose();
        Console.WriteLine("RENDERED: " + output);
    }

    private static void CheckMenuLevel(ItemsControl owner, UIElement[] rows, string name, string theme, string output, GlyphTypeface font)
    {
        var app = Application.Current;
        var surface = new StackPanel { Background = (Brush)app.FindResource("MenuBackgroundBrush") };
        owner.Items.Clear();
        var checks = rows.OfType<MenuItem>().ToDictionary(item => item, item => item.IsChecked);
        var enabled = rows.OfType<MenuItem>().ToDictionary(item => item, item => item.IsEnabled);
        var visibility = rows.OfType<MenuItem>().ToDictionary(item => item, item => item.Visibility);
        try
        {
            foreach (var row in rows)
            {
                if (row is MenuItem item)
                {
                    if (owner.ItemContainerStyle != null) item.Style = owner.ItemContainerStyle;
                    item.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
                    item.SetCurrentValue(MenuItem.IsCheckedProperty, false);
                }
                surface.Children.Add(row);
            }
            Render(surface, 440, Math.Max(60, rows.Length * 34));
            foreach (var item in rows.OfType<MenuItem>())
            {
                string label = name + "/" + item.Header + "/" + theme;
                Require(item.Icon is FrameworkElement, label + " has an icon.");
                var icon = (FrameworkElement)item.Icon;
                if (icon is TextBlock glyph)
                {
                    Require(glyph.FontFamily.Source == "Segoe MDL2 Assets" && glyph.FontSize == 12, label + " uses the family font and size.");
                    int codepoint = char.ConvertToUtf32(glyph.Text, 0);
                    Require(font.CharacterToGlyphMap.ContainsKey(codepoint), label + " exists in the installed font: U+" + codepoint.ToString("X4"));
                }
                // Every menu, the file picker's sort menu included since the KillerUI sync, uses the
                // family MenuItem, whose icon presenter is IconPresenter (KillerScan's old one was "ico").
                var presenter = (ContentPresenter)(item.Template.FindName("IconPresenter", item) ?? item.Template.FindName("ico", item));
                Require(presenter.Visibility == Visibility.Visible && icon.ActualWidth > 0 && icon.ActualHeight > 0, label + " renders its icon.");
                var grid = (Grid)presenter.Parent;
                Require(grid.ColumnDefinitions[0].ActualWidth == 16, label + " keeps a fixed icon gutter.");
                if (item.HasItems)
                {
                    var arrow = (TextBlock)(item.Template.FindName("SubArrow", item) ?? item.Template.FindName("arrow", item));
                    Require(arrow.Text == "\uE76C" && arrow.Visibility == Visibility.Visible, label + " renders the submenu chevron.");
                }
                string gesture = item.InputGestureText;
                double rowHeight = item.ActualHeight;
                if (item.IsCheckable)
                {
                    item.SetCurrentValue(MenuItem.IsCheckedProperty, true); surface.UpdateLayout();
                    // Family MenuItem: "Check". The picker's own items: "CheckGlyph".
                    var check = (TextBlock)(item.Template.FindName("Check", item) ?? item.Template.FindName("CheckGlyph", item));
                    Require(check.Text == "\uE73E" && check.Visibility == Visibility.Visible && presenter.Visibility == Visibility.Collapsed,
                        label + " replaces the icon with a checkmark.");
                    Require(item.ActualHeight == rowHeight && grid.ColumnDefinitions[0].ActualWidth == 16,
                        label + " preserves spacing when checked.");
                    item.SetCurrentValue(MenuItem.IsCheckedProperty, false);
                }
                item.SetCurrentValue(UIElement.IsEnabledProperty, true);
                typeof(MenuItem).GetProperty("IsHighlighted")!.SetValue(item, true);
                surface.UpdateLayout();
                var expected = (Brush)app.FindResource("MenuHoverTextBrush");
                if (icon is TextBlock highlighted)
                    Require(highlighted.Foreground.ToString() == expected.ToString(), label + " follows the highlighted foreground.");
                typeof(MenuItem).GetProperty("IsHighlighted")!.SetValue(item, false);
                item.SetCurrentValue(UIElement.IsEnabledProperty, false); surface.UpdateLayout();
                // The family MenuItem dims its row border (MenuBd, which holds the icon); the picker
                // dims the whole item. Either way the glyph dims with its row.
                double dimmed = item.Opacity * ((item.Template.FindName("MenuBd", item) as UIElement)?.Opacity ?? 1);
                Require(Math.Abs(dimmed - 0.4) < 0.001, label + " dims disabled glyphs with its row.");
                Require(item.InputGestureText == gesture, label + " retains its shortcut text.");
                item.SetCurrentValue(UIElement.IsEnabledProperty, enabled[item]);
                item.SetCurrentValue(MenuItem.IsCheckedProperty, checks[item]);
            }
            if (theme is "Black" or "Light" or "SE98")
                Save(Render(surface, 440, Math.Max(60, rows.Length * 34)), Path.Combine(output, name + "-" + theme + ".png"));
        }
        finally
        {
            foreach (var item in rows.OfType<MenuItem>()) item.SetCurrentValue(UIElement.VisibilityProperty, visibility[item]);
            surface.Children.Clear(); foreach (var row in rows) owner.Items.Add(row);
        }
        int index = 0;
        foreach (var item in rows.OfType<MenuItem>().Where(item => item.HasItems))
            CheckMenuLevel(item, item.Items.Cast<UIElement>().ToArray(), name + "-" + ++index, theme, output, font);
    }
}
