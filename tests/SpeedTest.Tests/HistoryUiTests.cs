using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using Microsoft.Win32;
using KillerScan.Controls;
using KillerScan.Shell;

internal static class HistoryUiTests
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private static int _checks;
    [DllImport("advapi32.dll")] private static extern int RegOverridePredefKey(IntPtr key, IntPtr replacement);
    private sealed class TestApplication : KillerScan.App { protected override void OnStartup(StartupEventArgs e) { } }
    private static object? Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Instance)!.Invoke(target, args);
    private static object? Field(object target, string name) => target.GetType().GetField(name, Instance)!.GetValue(target);
    private static void Require(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }

    public static Task Run(bool settingsOnly = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckUi(settingsOnly); } catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(120)), "History checks finish within two minutes.");
        if (failure != null) throw new InvalidOperationException("History UI regression failed.", failure);
        Console.WriteLine("PASS: " + _checks + " history, context-menu, shortcut and rendering assertions.");
        return Task.CompletedTask;
    }

    public static async Task RunIsolated()
    {
        using var process = Process.Start(new ProcessStartInfo(typeof(HistoryUiTests).Assembly.Location, "--history-ui")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        Require(await Task.Run(() => process.WaitForExit(120000)), "Isolated history suite finishes within two minutes.");
        Console.Write(await output); Require(process.ExitCode == 0, "Isolated history suite: " + await error);
    }

    private static void CheckUi(bool settingsOnly)
    {
        // Settings writes are redirected for this isolated test process, never to the user's app key.
        string scratch = "Software\\KillerScan-HistoryTests-" + Guid.NewGuid().ToString("N");
        using var registry = Registry.CurrentUser.CreateSubKey(scratch);
        var currentUser = new IntPtr(unchecked((int)0x80000001));
        Require(RegOverridePredefKey(currentUser, registry.Handle.DangerousGetHandle()) == 0, "Isolate registry settings.");
        try { Capture(settingsOnly); }
        finally
        {
            RegOverridePredefKey(currentUser, IntPtr.Zero);
            registry.Dispose(); Registry.CurrentUser.DeleteSubKeyTree(scratch);
        }
    }

    private static void Capture(bool settingsOnly)
    {
        var assembly = typeof(KillerScan.App).Assembly;
        typeof(Application).GetField("_resourceAssembly", Static)!.SetValue(null, assembly);
        var app = new TestApplication();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerScan.csproj"))) directory = directory.Parent;
        if (directory == null && Environment.GetEnvironmentVariable("KILLERSCAN_TEST_ROOT") is string sourceRoot)
            directory = new DirectoryInfo(sourceRoot);
        Require(directory != null, "Application resource source exists.");
        var xml = new XmlDocument(); xml.Load(Path.Combine(directory!.FullName, "App.xaml"));
        var dictionary = xml.DocumentElement!.FirstChild!.FirstChild!.OuterXml.Replace("clr-namespace:KillerScan.Controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/KillerScan;component/") };
        context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        context.XmlnsDictionary.Add("controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary, context);
        assembly.GetType("KillerScan.Services.DemoData", true)!.GetField("Enabled", Static)!.SetValue(null, true);
        var themeManager = assembly.GetType("KillerScan.Services.ThemeManager", true)!;
        var themeType = assembly.GetType("KillerScan.Services.Theme", true)!;
        var accentType = assembly.GetType("KillerScan.Services.Accent", true)!;
        var localeManager = assembly.GetType("KillerScan.Services.LocaleManager", true)!;
        var localeType = assembly.GetType("KillerScan.Services.Locale", true)!;
        void Theme(string name)
        {
            var value = Enum.Parse(themeType, name); themeManager.GetField("_current", Static)!.SetValue(null, value);
            themeManager.GetMethod("LoadDict", Static)!.Invoke(null, new[] { value, Enum.Parse(accentType, name == "SE98" ? "Blue" : "Orange") });
        }
        void Locale(string name)
        {
            var value = Enum.Parse(localeType, name); localeManager.GetField("_current", Static)!.SetValue(null, value);
            localeManager.GetMethod("ApplyInternal", Static)!.Invoke(null, new[] { value });
            ((Action?)localeManager.GetField("LocaleChanged", Static)!.GetValue(null))?.Invoke();
        }
        Theme("Black"); Locale("EnUS");
        if (settingsOnly)
        {
            typeof(MainWindow).GetMethod("PublishGrainTile", Static)!.Invoke(null, null);
            string dialogOutput = Path.Combine(Path.GetTempPath(), "KillerScan-history-dialog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dialogOutput);
            CheckSettingsDialog(assembly, Theme, Locale, dialogOutput);
            HistoryRetentionTests.Run();
            Console.WriteLine("RENDERED: " + dialogOutput);
            return;
        }
        var window = new MainWindow();
        var historyType = assembly.GetType("KillerScan.Services.ScanHistory", true)!;
        var entryType = assembly.GetType("KillerScan.Services.ScanHistoryEntry", true)!;
        var deviceType = assembly.GetType("KillerScan.Services.HistoricalDevice", true)!;
        object Device(string identity, string ip, string name)
        {
            var device = Activator.CreateInstance(deviceType)!;
            foreach (var pair in new[] { ("Identity", identity), ("IpAddress", ip), ("Hostname", name), ("MacAddress", "00:11:22:33:44:55"), ("Vendor", "Fixture"), ("DeviceType", "Server") })
                deviceType.GetProperty(pair.Item1)!.SetValue(device, pair.Item2);
            deviceType.GetProperty("OpenPorts")!.SetValue(device, new List<int> { 22, 8443 });
            return device;
        }
        object Entry(DateTimeOffset time, params object[] devices)
        {
            var entry = Activator.CreateInstance(entryType)!;
            entryType.GetProperty("ScannedAt")!.SetValue(entry, time);
            entryType.GetProperty("Target")!.SetValue(entry, "192.0.2.0/24");
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(deviceType))!;
            foreach (var device in devices) list.Add(device);
            entryType.GetProperty("Devices")!.SetValue(entry, list); return entry;
        }
        var time = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        var missing = Device("missing", "192.0.2.40", "missing, \"quoted\"\nline");
        var before = Entry(time, missing, Device("common", "192.0.2.41", "server-old"));
        var after = Entry(time.AddHours(1), Device("common", "192.0.2.41", "server-new"), Device("added", "192.0.2.42", "new-device"));
        var entries = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
        entries.Add(before); entries.Add(after); historyType.GetMethod("SeedDemo", Static)!.Invoke(null, new object[] { entries });
        Call(window, "RefreshHistoryList"); Call(window, "ShowHistoryEntry");
        var history = (HistoryWorkspace)Field(window, "_historyWorkspace")!;
        Call(history, "SetView", false);
        var grid = (DataGrid)history.FindName("HistoryChangesGrid");
        var all = (DataGrid)history.FindName("HistoryAllGrid");
        var title = (TextBlock)history.FindName("HistoryIdentity");
        string Identity() => string.Concat(title.Inlines.OfType<Run>().Select(run => run.Text));
        var header = (TextBlock)history.FindName("HistorySummary");
        var metadata = (TextBlock)history.FindName("HistoryEntryContext");
        var comparison = (TextBlock)history.FindName("HistoryComparisonContext");
        var root = (FrameworkElement)window.Content;
        BitmapSource RenderWindow(int width, int height)
        {
            Render(root, width, height);
            Call(window, "FitToolbarViews");
            return Render(root, width, height);
        }
        root.Opacity = 1; ((UIElement)window.FindName("RootGrid")).Opacity = 1;
        string output = Path.Combine(Path.GetTempPath(), "KillerScan-history-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        Require(grid.Items.Count == 3, "Added, missing and changed devices are shown.");
        Require(Identity().Contains("192.0.2.0/24") && Identity().Contains(time.ToString("g")) && Identity().Contains(time.AddHours(1).ToString("g")), "The outer toolbar retains the target and both compared timestamps.");
        Require(metadata.Text.Contains("192.0.2.0/24") && metadata.Text.Contains(time.AddHours(1).ToString("g")), "The target and saved timestamp are identified.");
        Require(comparison.Text.Contains(time.ToString("g")), "The comparison names its previous scan timestamp.");
        Call(window, "OpenSidebar"); Call(window, "ToggleSidebar"); Call(window, "ApplySidebarState", false);
        Render(root, 1200, 780);
        Require((bool)Field(window, "_sidebarCollapsed")! && history.Visibility == Visibility.Visible && header.ActualHeight > 0, "Closing the sidebar preserves the compact history title and snapshot.");
        Require(title.ToolTip is StackPanel tooltip && tooltip.Children.Contains(metadata) && tooltip.Children.Contains(comparison), "Scan metadata belongs to the outer toolbar tooltip.");
        CheckOriginalGeometry(window, history, root, directory.FullName, output);
        Require((string)Field(window, "_workspaceView")! == "history", "The selected snapshot remains active.");
        Require(((FrameworkElement)window.FindName("DeviceCountFooter")).Visibility == Visibility.Collapsed, "History does not show the hidden live scan's device count.");
        Require(Call(window, "GetSelectedDevice") == null, "History cannot act on a hidden live selection.");
        Require(((ScanWorkspace)Field(window, "_scanWorkspace")!).ExportContext == "history", "The rail export targets history.");
        object removed = grid.Items.Cast<object>().Single(row => ReferenceEquals(row.GetType().GetProperty("Device")!.GetValue(row), missing));
        grid.SelectedItem = removed; grid.CurrentItem = removed;
        Require((string)Call(history, "CopyValue", "ip")! == "192.0.2.40", "Missing-device copy uses the saved address.");
        Require(((string)Call(history, "CopyValue", "details")!).Contains("22, 8443"), "Copy details includes saved ports.");
        Require(((string)Call(history, "BuildCsv")!).Contains("\"missing, \"\"quoted\"\"\nline\""), "CSV quotes embedded commas, quotes and newlines.");
        var menu = grid.ContextMenu;
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Require(menu.Items.OfType<MenuItem>().Where(item => item.Tag is string action && new[] { "ip", "mac", "host", "details" }.Contains(action)).All(item => item.IsEnabled), "Saved row copy actions are available.");
        Require(menu.Items.OfType<MenuItem>().All(item => !string.IsNullOrWhiteSpace(item.InputGestureText)), "Every history menu action shows its shortcut.");
        Require(menu.Items.OfType<MenuItem>().All(item => !new[] { "Ping", "Ssh", "Rdp", "Rescan", "Trust" }.Contains(item.Tag as string)), "Historical menus contain no live commands.");
        Save(RenderWindow(1200, 780), Path.Combine(output, "History-Black-sidebar-closed.png"));
        SaveMenu(menu, app, Path.Combine(output, "History-menu.png"));
        history.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Require(history.ContextMenu.Items.OfType<MenuItem>().Where(item => item.Tag is string action && new[] { "ip", "mac", "host", "details" }.Contains(action)).All(item => !item.IsEnabled), "Pane background cannot copy a previously selected row.");
        typeof(HistoryWorkspace).GetMethod("SelectContextRow", Static)!.Invoke(null, new object?[] { grid, null });
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Require(grid.SelectedItem == null && menu.Items.OfType<MenuItem>().Where(item => Equals(item.Tag, "ip")).All(item => !item.IsEnabled), "Grid background clears the selected historical target.");
        var rowContainer = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0);
        typeof(HistoryWorkspace).GetMethod("SelectContextRow", Static)!.Invoke(null, new object[] { grid, rowContainer });
        Require(ReferenceEquals(grid.SelectedItem, rowContainer.Item), "Right-click selects its own row.");
        foreach (var shortcut in new[] { (Key.F3, ModifierKeys.None), (Key.Enter, ModifierKeys.None), (Key.R, ModifierKeys.Control), (Key.S, ModifierKeys.Control), (Key.H, ModifierKeys.Control | ModifierKeys.Alt), (Key.G, ModifierKeys.Control | ModifierKeys.Shift) })
            Require(history.HandleShortcut(shortcut.Item1, shortcut.Item2), "Historical network or wrong-format shortcuts cannot fall through: " + shortcut.Item1);
        Require(!history.HandleShortcut(Key.C, ModifierKeys.Control, true), "Text inputs retain normal copy behavior.");
        double comparisonTableY = grid.TranslatePoint(new Point(), root).Y;
        Require(history.HandleShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift) && all.Visibility == Visibility.Visible && all.Items.Count == 2, "History's view shortcut shows the saved snapshot.");
        RenderWindow(1200, 780);
        Require(Identity() == metadata.Text && Identity().Contains("192.0.2.0/24"),
            "All devices identifies the selected scan target and saved date.");
        Require(Math.Abs(all.TranslatePoint(new Point(), root).Y - comparisonTableY) <= 0.5,
            "Switching to all devices preserves the whole-window table position.");
        Save(RenderWindow(1200, 780), Path.Combine(output, "History-All-Black.png"));
        Require(!((string)Call(history, "BuildCsv")!).Contains("192.0.2.40"), "All-devices export excludes devices only present in the older snapshot.");
        history.HandleShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift);
        foreach (string theme in Enum.GetNames(themeType))
        {
            Theme(theme); Call(window, "ApplyFlatChrome");
            Save(Render(root, 1200, 780), Path.Combine(output, "History-" + theme + ".png"));
            Require(header.ActualHeight > 0 && grid.ActualHeight > 0, theme + ": compact history title and rows render.");
            Require(Equals(((Run)history.FindName("HistoryCurrentTimestamp")).Foreground, app.FindResource("PrimaryBrush")) &&
                Equals(((Run)history.FindName("HistoryPreviousTimestamp")).Foreground, app.FindResource("PrimaryBrush")), theme + ": both outer timestamps use the current accent.");
            Require(Equals(((Button)history.FindName("HistoryChangesViewButton")).Foreground, app.FindResource("PrimaryBrush")) &&
                ((Button)history.FindName("HistoryChangesViewButton")).Opacity == 1, theme + ": selected view is fully visible in the accent.");
        }
        Theme("Black");
        foreach (string accent in Enum.GetNames(accentType))
        {
            themeManager.GetMethod("LoadDict", Static)!.Invoke(null, new[] { Enum.Parse(themeType, "Black"), Enum.Parse(accentType, accent) });
            Render(root, 1200, 780);
            Require(Equals(((Run)history.FindName("HistoryCurrentTimestamp")).Foreground, app.FindResource("PrimaryBrush")) &&
                Equals(((Button)history.FindName("HistoryChangesViewButton")).Foreground, app.FindResource("PrimaryBrush")),
                accent + ": timestamps and selected view follow an accent change.");
        }
        Theme("Black");
        foreach (string locale in Enum.GetNames(localeType))
        {
            Locale(locale); history.RefreshLocale(); menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Require(Identity().Contains("192.0.2.0/24") && Identity().Contains(time.ToString("g")) && Identity().Contains(time.AddHours(1).ToString("g")) && !comparison.Text.StartsWith("Str_"), locale + ": target and both dates remain identified.");
            Require(Equals(grid.Columns[0].Header, app.FindResource("Str_History_Change")) && Equals(all.Columns[0].Header, app.FindResource("Str_Col_Ip")), locale + ": both grid headings change language.");
            Require(menu.Items.OfType<MenuItem>().All(item => item.Header is string text && text.Length > 0 && !text.StartsWith("Str_")), locale + ": menu labels resolve.");
            foreach (double scale in new[] { 1.0, 1.5, 2.5 })
            {
                Call(window, "ApplyAppScale", scale, false); Render(root, 1200, 780);
                Require(header.ActualHeight > 0, locale + ": compact title survives app scaling.");
                if (locale == "UkUA" && scale == 1.5) Save(Render(root, 1200, 780), Path.Combine(output, "History-Ukrainian-150.png"));
            }
        }
        Locale("EnUS"); Call(window, "ApplyAppScale", 1.0, false);
        Call(history, "ShowEntry", before); Require(((TextBlock)history.FindName("HistorySummary")).Text == (string)app.FindResource("Str_History_FirstScan") && comparison.Text.Length == 0, "First snapshots have no invented comparison.");
        Call(history, "ShowEntry", new object?[] { null }); menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
        Require(metadata.Text.Length == 0 && grid.Items.Count == 0 && !menu.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "export")).IsEnabled, "Empty history clears stale context and export availability.");
        CheckCatalog(window, app);
        ((FrameworkElement)window.FindName("ShortcutsOverlay")).Visibility = Visibility.Visible;
        Call(window, "ApplyShortcutView", false, false, false);
        Save(Render(root, 1200, 780), Path.Combine(output, "Shortcut-list.png"));
        Call(window, "ApplyShortcutView", true, false, false);
        Save(Render(root, 1200, 780), Path.Combine(output, "Shortcut-map.png"));
        ((FrameworkElement)window.FindName("ShortcutsOverlay")).Visibility = Visibility.Collapsed;
        CheckServices(assembly);
        CheckSettingsDialog(assembly, Theme, Locale, output);
        HistoryRetentionTests.Run();
        CheckHistorySettingsMenu(window, app, output);
        Call(history, "ShowEntry", after);
        Call(window, "OpenSidebar"); Call(window, "ApplySidebarState", false);
        var dense = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
        for (int i = 0; i < 30; i++) dense.Add(Entry(time.AddHours(i)));
        historyType.GetMethod("SeedDemo", Static)!.Invoke(null, new object[] { dense });
        Call(window, "RefreshHistoryList"); Call(window, "ShowHistoryEntry");
        Save(RenderWindow(1200, 780), Path.Combine(output, "History-compact-sidebar.png"));
        var heading = (TextBlock)window.FindName("SidebarHeading");
        var sidebar = (FrameworkElement)window.FindName("HistorySidebar");
        var sidebarParent = (FrameworkElement)sidebar.Parent;
        Require(heading.Text == "Scan History" && heading.TextAlignment == TextAlignment.Center,
            "The English sidebar heading is capitalized and centered.");
        var headingGroup = (StackPanel)window.FindName("HistorySidebarHeader");
        Require(Math.Abs(headingGroup.TranslatePoint(new Point(), sidebarParent).X + headingGroup.ActualWidth / 2 - sidebarParent.ActualWidth / 2) < 1,
            "The glyph and heading are centered together across the sidebar.");
        Require(headingGroup.Children[0] is TextBlock glyph && glyph.Text == (string)((Button)window.FindName("HistoryButton")).Content,
            "The centered heading has the existing history glyph immediately to its left.");
        var historyList = (ListBox)window.FindName("HistoryList");
        var firstRow = (ListBoxItem)historyList.ItemContainerGenerator.ContainerFromIndex(0);
        Require(firstRow.ActualHeight <= 38 && firstRow.ActualHeight >= 32,
            "Date-first rows have small padding and readable spacing.");
        var texts = new List<TextBlock>();
        void Collect(DependencyObject parent)
        {
            if (parent is TextBlock text) texts.Add(text);
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) Collect(VisualTreeHelper.GetChild(parent, i));
        }
        var secondRow = (ListBoxItem)historyList.ItemContainerGenerator.ContainerFromIndex(1);
        Collect(secondRow);
        Require(texts.Count == 2 && texts[0].Text.Contains(":") && texts[0].FontSize == 13 &&
            Equals(texts[0].Foreground, app.FindResource("PrimaryBrush")) && texts[1].Text == "192.0.2.0/24" && texts[1].FontSize == 11 &&
            Equals(texts[1].Foreground, app.FindResource("MutedTextBrush")),
            "The larger accented date is first and the smaller muted target is below it.");
        Console.WriteLine("SIDEBAR: rowHeight=" + firstRow.ActualHeight + " visibleRows=" + Math.Floor(historyList.ActualHeight / firstRow.ActualHeight));
        historyType.GetMethod("SeedDemo", Static)!.Invoke(null, new object[] { entries });
        Call(window, "RefreshHistoryList"); Call(window, "ShowHistoryEntry");
        Console.WriteLine("RENDERED: " + output);
    }

    private static void CheckOriginalGeometry(MainWindow window, HistoryWorkspace history, FrameworkElement root, string repo, string output)
    {
        // Exact XAML from fe699c3's parent, with code-behind hooks removed for loose loading.
        var xml = new XmlDocument();
        xml.Load(Path.Combine(repo, "tests", "SpeedTest.Tests", "Fixtures", "HistoryWorkspace-before-header.xml"));
        xml.DocumentElement!.RemoveAttribute("Class", "http://schemas.microsoft.com/winfx/2006/xaml");
        foreach (XmlElement element in xml.SelectNodes("//*")!) element.RemoveAttribute("Click");
        var baseline = (UserControl)XamlReader.Parse(xml.OuterXml);
        baseline.FontFamily = history.FontFamily;
        baseline.FontSize = history.FontSize;
        TextOptions.SetTextFormattingMode(baseline, TextOptions.GetTextFormattingMode(history));
        TextOptions.SetTextRenderingMode(baseline, TextOptions.GetTextRenderingMode(history));
        ((TextBlock)baseline.FindName("HistorySummary")).Text = ((TextBlock)history.FindName("HistorySummary")).Text;
        var grid = (DataGrid)history.FindName("HistoryChangesGrid");
        var oldGrid = (DataGrid)baseline.FindName("HistoryChangesGrid");
        oldGrid.ItemsSource = grid.ItemsSource;
        var header = (TextBlock)history.FindName("HistorySummary");
        BitmapSource RenderWindow(int width, int height)
        {
            Render(root, width, height);
            Call(window, "FitToolbarViews");
            return Render(root, width, height);
        }
        foreach (bool open in new[] { false, true })
        {
            if (open) Call(window, "OpenSidebar");
            else if (!(bool)Field(window, "_sidebarCollapsed")!) Call(window, "ToggleSidebar");
            Call(window, "ApplySidebarState", false);
            foreach (int width in new[] { 1200, 800, 640 })
            {
                RenderWindow(width, 780);
                Render(baseline, (int)Math.Round(history.ActualWidth), (int)Math.Round(history.ActualHeight));
                double actual = grid.TranslatePoint(new Point(), history).Y;
                double original = oldGrid.TranslatePoint(new Point(), baseline).Y;
                Console.WriteLine("GEOMETRY: width=" + width + " sidebar=" + (open ? "open" : "closed") + " tableY=" + actual + " originalY=" + original + " headerHeight=" + header.ActualHeight + " oldHeaderHeight=" + ((TextBlock)baseline.FindName("HistorySummary")).ActualHeight);
                Save(RenderWindow(width, 780), Path.Combine(output, "Compact-" + width + "-sidebar-" + (open ? "open" : "closed") + ".png"));
                Save(Render(baseline, (int)Math.Round(history.ActualWidth), (int)Math.Round(history.ActualHeight)), Path.Combine(output, "Original-pane-" + width + "-sidebar-" + (open ? "open" : "closed") + ".png"));
                var identity = (TextBlock)history.FindName("HistoryIdentity");
                var toggle = (Button)history.FindName("HistoryAllViewButton");
                var bar = (FrameworkElement)history.FindName("HistoryToolbar");
                var navigation = (FrameworkElement)Field(window, "_workspaceNavigation")!;
                var paneTop = history.TranslatePoint(new Point(), root).Y;
                Rect Bounds(FrameworkElement item) => new Rect(item.TranslatePoint(new Point(), root), item.RenderSize);
                var identityBounds = Bounds(identity);
                var toggleBounds = Bounds(toggle);
                var navigationBounds = Bounds(navigation);
                Require(identityBounds.Bottom <= paneTop && toggleBounds.Top >= paneTop && toggleBounds.Bottom <= grid.TranslatePoint(new Point(), root).Y,
                    "Identity is above the panel and the toggle is inside its original summary row: " + width + "/" + open);
                Require(identityBounds.Right <= navigationBounds.Left && Math.Abs(toggleBounds.Right - history.TranslatePoint(new Point(history.ActualWidth - 12, 0), root).X) < 1,
                    "Identity clears navigation and the view switch stays at the panel's right inset: " + width + "/" + open);
                Require(identityBounds.Width > 0, "Scan identity remains readable at narrow widths: " + width + "/" + open);
                double tableWindowY = grid.TranslatePoint(new Point(), root).Y;
                var body = (Panel)Field(window, "_workspaceBody")!;
                int childIndex = body.Children.IndexOf(history);
                body.Children.Remove(history);
                body.Children.Insert(childIndex, baseline);
                baseline.Visibility = Visibility.Visible;
                bar.Visibility = Visibility.Collapsed;
                RenderWindow(width, 780);
                double originalWindowY = oldGrid.TranslatePoint(new Point(), root).Y;
                body.Children.Remove(baseline);
                body.Children.Insert(childIndex, history);
                bar.Visibility = Visibility.Visible;
                RenderWindow(width, 780);
                Require(Math.Abs(tableWindowY - originalWindowY) <= 0.5,
                    "Whole-window table position matches the original pane and empty top area: " + width + "/" + open);
                Require(actual <= original + 0.5, "The table never moves below the original position: " + width + "/" + open);
                Require(Math.Abs(actual - original) <= 0.5, "Original table position is exact at normal and narrow widths: " + width + "/" + open);
                Require(header.ActualHeight <= ((TextBlock)baseline.FindName("HistorySummary")).ActualHeight + 0.5, "The title adds no vertical header height.");
            }
        }
        Call(window, "ToggleSidebar"); Call(window, "ApplySidebarState", false); Render(root, 1200, 780);
    }

    private static int _settingsApplyCalls;
    private static bool RejectSettings(object policy) { _settingsApplyCalls++; return false; }

    private static void CheckSettingsDialog(Assembly assembly, Action<string> theme, Action<string> locale, string output)
    {
        var policyType = assembly.GetType("KillerScan.Services.HistoryRetention", true)!;
        var modeType = assembly.GetType("KillerScan.Services.HistoryRetentionMode", true)!;
        var dialogType = assembly.GetType("KillerScan.Controls.HistorySettingsDialog", true)!;
        var policy = Activator.CreateInstance(policyType, Enum.Parse(modeType, "Count"), 100, 30)!;
        var callback = Delegate.CreateDelegate(typeof(Func<,>).MakeGenericType(policyType, typeof(bool)),
            typeof(HistoryUiTests).GetMethod(nameof(RejectSettings), Static)!);
        Window NewDialog() => (Window)Activator.CreateInstance(dialogType, Instance, null, new object[] { policy, callback }, null)!;
        BitmapSource Draw(Window dialog)
        {
            ((UIElement)dialog.FindName("RootBorder")).Opacity = 1;
            var surface = (FrameworkElement)dialog.Content;
            surface.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            int width = (int)Math.Ceiling(Math.Max(dialog.MinWidth, surface.DesiredSize.Width));
            surface.Measure(new Size(width, double.PositiveInfinity));
            return Render(surface, width, (int)Math.Ceiling(surface.DesiredSize.Height));
        }
        void CheckLayout(Window dialog, string context)
        {
            var surface = (FrameworkElement)dialog.Content;
            var caption = (TextBlock)dialog.FindName("SettingsCaption");
            var rows = (Grid)dialog.FindName("RetentionRows");
            var days = (TextBox)dialog.FindName("DaysBox");
            var countField = (TextBox)dialog.FindName("CountBox");
            var timeOption = (RadioButton)dialog.FindName("TimeMode");
            var countOption = (RadioButton)dialog.FindName("CountMode");
            Rect Bounds(FrameworkElement item) => new Rect(item.TranslatePoint(new Point(), surface), item.RenderSize);
            Require(caption.FontFamily.Equals((FontFamily)dialog.FindResource("WordmarkFont")) && caption.FontSize == 18,
                context + ": the caption uses the family typewriter font beside the wordmark.");
            Require(Math.Abs(Bounds(days).X - Bounds(countField).X) < 0.5 &&
                Math.Abs(Bounds(days).Top + days.ActualHeight / 2 - Bounds(timeOption).Top - timeOption.ActualHeight / 2) <= 0.5 &&
                Math.Abs(Bounds(countField).Top + countField.ActualHeight / 2 - Bounds(countOption).Top - countOption.ActualHeight / 2) <= 0.5,
                context + ": options, values and units share aligned rows.");
            Require(Bounds(days).Right <= Bounds(rows).Right && Bounds(countField).Right <= Bounds(rows).Right &&
                Bounds(rows).Bottom <= Bounds((Button)dialog.FindName("OkButton")).Top,
                context + ": content stays within the compact card without overlapping buttons.");
            foreach (var selection in new[] { timeOption, countOption, (RadioButton)dialog.FindName("AllMode") })
            {
                Require(ReferenceEquals(selection.Style, dialog.FindResource("ThemeRadio")) &&
                    selection.Template.FindName("label", selection) is ContentPresenter label && label.Effect == null,
                    context + ": selection uses the existing family template without a label bitmap effect.");
            }
            Require(dialog.UseLayoutRounding && dialog.SnapsToDevicePixels &&
                TextOptions.GetTextFormattingMode(dialog) == TextFormattingMode.Display &&
                TextOptions.GetTextRenderingMode(dialog) == TextRenderingMode.ClearType &&
                ((UIElement)dialog.FindName("DialogCard")).Effect == null,
                context + ": sharp family text configuration is local to the dialog and the shadow is a sibling.");
            if (Equals(dialog.FindResource("InputDialogWordmarkVisibility"), Visibility.Visible))
                Require(Bounds(caption).Bottom <= Bounds(rows).Top && Bounds(caption).Right <= Bounds((Button)dialog.FindName("CaptionCloseButton")).Left,
                    context + ": the localized inline caption clears the close control and body.");
        }
        // These dialog fixtures open no history files and write no settings.
        var input = NewDialog();
        var count = (RadioButton)input.FindName("CountMode");
        var time = (RadioButton)input.FindName("TimeMode");
        var all = (RadioButton)input.FindName("AllMode");
        var countBox = (TextBox)input.FindName("CountBox");
        var daysBox = (TextBox)input.FindName("DaysBox");
        Require(count.IsChecked == true && countBox.Text == "100", "New-history dialog defaults to 100 scans.");
        foreach (string value in new[] { "", "0", "-1", "word", "2147483648" })
        {
            countBox.Text = value;
            Require(Call(input, "ReadPolicy") == null, "Invalid count is rejected: " + value);
        }
        foreach (string value in new[] { "1", "100", "2147483647" })
        {
            countBox.Text = value;
            Require(Call(input, "ReadPolicy") != null, "Positive count boundary is accepted: " + value);
        }
        time.IsChecked = true;
        Require(count.IsChecked == false && all.IsChecked == false && !countBox.IsEnabled && daysBox.IsEnabled,
            "Time retention is mutually exclusive and enables only its duration.");
        foreach (string value in new[] { "", "0", "-1", "36501", "1.5" })
        {
            daysBox.Text = value;
            Require(Call(input, "ReadPolicy") == null, "Invalid duration is rejected: " + value);
        }
        foreach (string value in new[] { "1", "30", "36500" })
        {
            daysBox.Text = value;
            Require(Call(input, "ReadPolicy") != null, "Duration boundary is accepted: " + value);
        }
        all.IsChecked = true;
        Require(time.IsChecked == false && count.IsChecked == false && !daysBox.IsEnabled && !countBox.IsEnabled &&
            Call(input, "ReadPolicy") != null, "Save all ignores inactive editor values.");
        int calls = _settingsApplyCalls;
        var save = (Button)input.FindName("OkButton");
        var cancel = (Button)input.FindName("CancelButton");
        Require(save.IsDefault && cancel.IsCancel, "Enter saves and Escape cancels through the dialog's keyboard buttons.");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(_settingsApplyCalls == calls + 1, "The actual Save button passes the selected policy to its application callback.");
        calls = _settingsApplyCalls;
        Call(input, "Cancel_Click", input, new RoutedEventArgs());
        Require(_settingsApplyCalls == calls, "Cancel invokes no settings or history mutation.");
        var entryType = assembly.GetType("KillerScan.Services.ScanHistoryEntry", true)!;
        var fixture = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(entryType))!;
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        foreach (var stamp in new[] { now.AddDays(1), now.AddDays(-30).AddTicks(-1), now.AddDays(-30), now.AddDays(-1) })
        {
            var entry = Activator.CreateInstance(entryType)!;
            entryType.GetProperty("ScannedAt")!.SetValue(entry, stamp);
            fixture.Add(entry);
        }
        object Policy(string mode, int countValue = 100, int daysValue = 30) =>
            Activator.CreateInstance(policyType, Enum.Parse(modeType, mode), countValue, daysValue)!;
        IList Plan(object selected) => (IList)Call(selected, "Retained", fixture, now)!;
        var latest = Plan(Policy("Count", 2));
        Require(latest.Count == 2 && ReferenceEquals(latest[0], fixture[0]) && ReferenceEquals(latest[1], fixture[3]),
            "Count retention plans the latest timestamps while preserving archive order.");
        var timed = Plan(Policy("Time"));
        Require(timed.Count == 3 && ReferenceEquals(timed[1], fixture[2]),
            "Time retention includes its exact boundary and future timestamps.");
        Require(Plan(Policy("SaveAll")).Count == 4 && fixture.Count == 4,
            "Save all preserves every scan and retention plans never mutate the source fixture.");
        Require(Plan(Policy("Count", int.MaxValue)).Count == 4, "A count larger than history preserves the entire archive.");
        foreach (string name in Enum.GetNames(assembly.GetType("KillerScan.Services.Theme", true)!))
        {
            theme(name);
            var dialog = NewDialog();
            Save(Draw(dialog), Path.Combine(output, "History-settings-" + name + ".png"));
            CheckLayout(dialog, name);
            Require(((TextBox)dialog.FindName("CountBox")).ActualHeight > 0, name + ": themed history settings render.");
            dialog.Close();
        }
        theme("Black");
        foreach (string name in Enum.GetNames(assembly.GetType("KillerScan.Services.Locale", true)!))
        {
            locale(name);
            var dialog = NewDialog();
            Draw(dialog);
            CheckLayout(dialog, name);
            Require(((RadioButton)dialog.FindName("AllMode")).Content is string label && !label.StartsWith("Str_"),
                name + ": retention labels resolve.");
            Require(((Button)dialog.FindName("OkButton")).Content is string text && !text.StartsWith("Str_"),
                name + ": save label resolves.");
            if (name == "UkUA") Save(Draw(dialog), Path.Combine(output, "History-settings-Ukrainian.png"));
            dialog.Close();
        }
        locale("EnUS");
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        {
            var dialog = NewDialog();
            var image = Draw(dialog);
            Require(image.PixelHeight <= 250 && image.PixelWidth <= 440,
                "English settings uses a compact whole-window footprint.");
            var surface = (FrameworkElement)dialog.Content;
            var scaled = new RenderTargetBitmap((int)Math.Ceiling(image.PixelWidth * scale),
                (int)Math.Ceiling(image.PixelHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            scaled.Render(surface);
            Save(scaled, Path.Combine(output, "History-settings-Black-" + (int)(scale * 100) + ".png"));
            CheckLayout(dialog, "Scale " + scale);
            dialog.Close();
        }
    }

    private static void CheckCatalog(MainWindow window, Application app)
    {
        var rows = (Array)typeof(MainWindow).GetField("ShortcutRows", Static)!.GetValue(null)!;
        foreach (object row in rows)
        {
            var type = row.GetType(); string gesture = (string)type.GetField("Item1")!.GetValue(row)!;
            string key = (string)type.GetField("Item2")!.GetValue(row)!;
            var args = new object[] { gesture, Activator.CreateInstance(typeof(MainWindow).GetNestedType("KbLayer", BindingFlags.NonPublic)!)!, "" };
            Require((bool)typeof(MainWindow).GetMethod("TryParseGesture", Static)!.Invoke(null, args)!, "Keyboard map recognizes " + gesture);
            Require(app.FindResource(key) is string text && text.Length > 0, "Shortcut description resolves: " + key);
        }
        Call(window, "BuildShortcutRows"); Call(window, "BuildKeyboardMap");
        Require(rows.Length >= 70, "The list and map include the added contextual shortcuts.");
    }

    private static void CheckHistorySettingsMenu(MainWindow window, Application app, string output)
    {
        var icon = (Button)window.FindName("HistoryButton");
        var menu = icon.ContextMenu;
        Call(window, "HistorySettingsMenu_Opening", icon, null);
        Require(ReferenceEquals(menu.PlacementTarget, icon) && menu.CustomPopupPlacementCallback != null && menu.HorizontalOffset == 0 && menu.VerticalOffset == 0,
            "Mouse and native keyboard menu opening use the history button anchor, never the pointer.");
        var placement = typeof(MainWindow).Assembly.GetType("KillerScan.Shell.FlyoutPlacement", true)!.GetMethod("PlaceBesideButton", Static)!;
        foreach (double dpi in new[] { 1.0, 1.5, 2.0 })
        foreach (double zoom in new[] { 1.0, 1.5, 2.5 })
        {
            double scale = dpi * zoom;
            var candidates = (System.Windows.Controls.Primitives.CustomPopupPlacement[])placement.Invoke(null,
                new object[] { new Size(320 * scale, 80 * scale), new Size(24 * scale, 24 * scale), new Thickness(22, 18, 22, 26), scale, scale, dpi })!;
            Require(candidates.Length == 2 && candidates.All(candidate => Math.Abs(candidate.Point.X + 22 * scale - 24 * scale - 8 * dpi) < 0.01),
                "Visible menu card stays eight logical pixels right of the rail button at DPI=" + dpi + " zoom=" + zoom);
            Require(Math.Abs(candidates[0].Point.Y + 18 * scale) < 0.01 && Math.Abs(candidates[1].Point.Y + 80 * scale - 26 * scale - 24 * scale) < 0.01,
                "Top and bottom candidates let WPF fit screen edges without covering the rail.");
        }
        var item = menu.Items.OfType<MenuItem>().Single();
        Require(Equals(item.Header, app.FindResource("Str_History_Settings")) && item.InputGestureText == "Ctrl+Alt+R",
            "The history icon offers localized settings with its visible shortcut.");
        Require(item.Icon is TextBlock clock && clock.Text == (string)icon.Content,
            "History settings carries the same clock glyph as the history icon.");
        var rows = (Array)typeof(MainWindow).GetField("ShortcutRows", Static)!.GetValue(null)!;
        Require(rows.Cast<object>().Count(row => ((string)row.GetType().GetField("Item1")!.GetValue(row)!).Replace(" ", "") == item.InputGestureText) == 1,
            "The new gesture is unique in the app catalogue.");
        var match = typeof(MainWindow).GetMethod("IsHistorySettingsShortcut", Static)!;
        Require((bool)match.Invoke(null, new object[] { Key.R, ModifierKeys.Control | ModifierKeys.Alt })!,
            "The bound chord is recognized by the live shortcut handler.");
        foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Control, ModifierKeys.Control | ModifierKeys.Shift, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift })
            Require(!(bool)match.Invoke(null, new object[] { Key.R, modifiers })!, "Other R chords retain their existing scope.");
        var eventStore = typeof(UIElement).GetProperty("EventHandlersStore", Instance)!.GetValue(item)!;
        var handlers = (Array)eventStore.GetType().GetMethod("GetRoutedEventHandlers", Instance)!.Invoke(eventStore, new object[] { MenuItem.ClickEvent })!;
        Require(handlers.Cast<object>().Any(info => ((Delegate)info.GetType().GetProperty("Handler", Instance)!.GetValue(info)!).Method.Name == "HistorySettings_Click"),
            "The actual history menu click is wired to the settings-dialog handler.");
        SaveMenu(menu, app, Path.Combine(output, "History-settings-menu.png"));
        var confirmation = (Window)Call(window, "CreateHistoryRemovalConfirmation", 7, null)!;
        Require(((TextBlock)confirmation.FindName("DetailText")).Text == string.Format((string)app.FindResource("Str_History_RemoveConfirm"), 7),
            "The actual removal dialog names its deletion count and permanent effect.");
        ((UIElement)confirmation.FindName("RootBorder")).Opacity = 1;
        var surface = (FrameworkElement)confirmation.Content;
        surface.Measure(new Size(400, double.PositiveInfinity));
        Save(Render(surface, 400, (int)Math.Ceiling(surface.DesiredSize.Height)), Path.Combine(output, "History-removal-confirmation.png"));
        ((Button)confirmation.FindName("CancelButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require(!(bool)Property(confirmation, "Confirmed")!, "The actual confirmation Cancel declines deletion.");
        var accepted = (Window)Call(window, "CreateHistoryRemovalConfirmation", 7, null)!;
        ((Button)accepted.FindName("OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Require((bool)Property(accepted, "Confirmed")!, "The actual Delete scans button explicitly confirms.");
    }

    private static object? Property(object value, string name) => value.GetType().GetProperty(name, Instance)!.GetValue(value);

    private static void CheckServices(Assembly assembly)
    {
        var workspace = new ScanWorkspace("192.0.2.0/24", true);
        var host = new KillerScan.Engine.NetworkDevice { IpAddress = "192.0.2.40", Hostname = "service-fixture", DeviceType = "Server", OpenPorts = new List<int> { 22, 53, 3389, 8443 } };
        Call(workspace, "LoadSnapshot", "192.0.2.0/24", new[] { host });
        Call(workspace, "RefreshServices");
        var grid = (DataGrid)workspace.FindName("ServicesGrid");
        var rows = grid.Items.Cast<object>().ToArray();
        Require(rows.Length > 0 && rows.Any(row => (int)row.GetType().GetProperty("Port")!.GetValue(row)! == 22), "Service tests include an SSH target.");
        foreach (var row in rows.Where(row => (int)row.GetType().GetProperty("Port")!.GetValue(row)! is 22 or 53 or 3389 or 8443))
        {
            grid.SelectedItem = row; grid.CurrentItem = row;
            string endpoint = (string)Call(workspace, "ServiceCopyText", "CopyEndpoint")!;
            Require(endpoint.EndsWith(":" + row.GetType().GetProperty("Port")!.GetValue(row)), "Service copy uses the selected endpoint.");
            KillerScan.Controls.ScanDeviceActionEventArgs? raised = null;
            workspace.DeviceAction += (_, e) => raised = e;
            int port = (int)row.GetType().GetProperty("Port")!.GetValue(row)!;
            Require(workspace.HandleServiceShortcut(Key.S, ModifierKeys.Control), "Services consumes the SSH chord.");
            Require(port == 22 ? raised?.ServicePort == 22 : raised == null, "SSH is available only for an SSH service.");
            raised = null;
            Require(workspace.HandleServiceShortcut(Key.Enter, ModifierKeys.None), "Services consumes the browser chord.");
            Require(port == 8443 ? raised?.ServicePort == 8443 : raised == null, "Browser shortcuts preserve the web service port and ignore unrelated services.");
            raised = null;
            Require(workspace.HandleServiceShortcut(Key.D, ModifierKeys.Control), "Services consumes the RDP chord.");
            Require(port == 3389 ? raised?.ServicePort == 3389 : raised == null, "RDP shortcuts require the selected RDP service.");
        }
        var menu = grid.ContextMenu;
        Require(menu.Items.OfType<MenuItem>().Where(item => item.Tag is string action && new[] { "CopyEndpoint", "CopyService", "CopyPort" }.Contains(action)).All(item => item.InputGestureText.Length > 0), "Service copy actions have menu hints.");
    }

    private static BitmapSource Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(element); return image;
    }
    private static void Save(BitmapSource image, string path)
    { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static void SaveMenu(ContextMenu menu, Application app, string path)
    {
        var surface = new StackPanel { Background = (Brush)app.FindResource("MenuBackgroundBrush") };
        var items = menu.Items.Cast<UIElement>().ToArray(); menu.Items.Clear();
        foreach (var item in items) surface.Children.Add(item);
        Save(Render(surface, 440, 370), path); surface.Children.Clear();
        foreach (var item in items) menu.Items.Add(item);
    }
}
