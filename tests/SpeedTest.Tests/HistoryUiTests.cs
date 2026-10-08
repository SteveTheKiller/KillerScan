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

    public static Task Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CheckUi(); } catch (Exception ex) { failure = ex; }
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

    private static void CheckUi()
    {
        // Settings writes are redirected for this isolated test process, never to the user's app key.
        string scratch = "Software\\KillerScan-HistoryTests-" + Guid.NewGuid().ToString("N");
        using var registry = Registry.CurrentUser.CreateSubKey(scratch);
        var currentUser = new IntPtr(unchecked((int)0x80000001));
        Require(RegOverridePredefKey(currentUser, registry.Handle.DangerousGetHandle()) == 0, "Isolate registry settings.");
        try { Capture(); }
        finally
        {
            RegOverridePredefKey(currentUser, IntPtr.Zero);
            registry.Dispose(); Registry.CurrentUser.DeleteSubKeyTree(scratch);
        }
    }

    private static void Capture()
    {
        var assembly = typeof(KillerScan.App).Assembly;
        typeof(Application).GetField("_resourceAssembly", Static)!.SetValue(null, assembly);
        var app = new TestApplication();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerScan.csproj"))) directory = directory.Parent;
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
        var title = (TextBlock)history.FindName("HistoryTitle");
        var metadata = (TextBlock)history.FindName("HistoryEntryContext");
        var comparison = (TextBlock)history.FindName("HistoryComparisonContext");
        var root = (FrameworkElement)window.Content;
        root.Opacity = 1; ((UIElement)window.FindName("RootGrid")).Opacity = 1;
        string output = Path.Combine(Path.GetTempPath(), "KillerScan-history-ui-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        Require(grid.Items.Count == 3, "Added, missing and changed devices are shown.");
        Require(title.Text == (string)app.FindResource("Str_History_Title") && title.Visibility == Visibility.Visible, "The history heading identifies the workspace.");
        Require(metadata.Text.Contains("192.0.2.0/24") && metadata.Text.Contains(time.AddHours(1).ToLocalTime().ToString("g")), "The target and saved timestamp are identified.");
        Require(comparison.Text.Contains(time.ToLocalTime().ToString("g")), "The comparison names its previous scan timestamp.");
        Call(window, "OpenSidebar"); Call(window, "ToggleSidebar"); Call(window, "ApplySidebarState", false);
        Render(root, 1200, 780);
        Require((bool)Field(window, "_sidebarCollapsed")! && history.Visibility == Visibility.Visible && title.ActualHeight > 0, "Closing the sidebar preserves the history heading and snapshot.");
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
        Save(Render(root, 1200, 780), Path.Combine(output, "History-Black-sidebar-closed.png"));
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
        Require(history.HandleShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift) && all.Visibility == Visibility.Visible && all.Items.Count == 2, "History's view shortcut shows the saved snapshot.");
        Require(!((string)Call(history, "BuildCsv")!).Contains("192.0.2.40"), "All-devices export excludes devices only present in the older snapshot.");
        history.HandleShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift);
        foreach (string theme in Enum.GetNames(themeType))
        {
            Theme(theme); Call(window, "ApplyFlatChrome");
            Save(Render(root, 1200, 780), Path.Combine(output, "History-" + theme + ".png"));
            Require(title.ActualHeight > 0 && metadata.ActualHeight > 0 && grid.ActualHeight > 0, theme + ": history header and rows render.");
        }
        Theme("Black");
        foreach (string locale in Enum.GetNames(localeType))
        {
            Locale(locale); history.RefreshLocale(); menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            Require(title.Text == (string)app.FindResource("Str_History_Title") && !comparison.Text.StartsWith("Str_"), locale + ": history text is localized.");
            Require(Equals(grid.Columns[0].Header, app.FindResource("Str_History_Change")) && Equals(all.Columns[0].Header, app.FindResource("Str_Col_Ip")), locale + ": both grid headings change language.");
            Require(menu.Items.OfType<MenuItem>().All(item => item.Header is string text && text.Length > 0 && !text.StartsWith("Str_")), locale + ": menu labels resolve.");
            foreach (double scale in new[] { 1.0, 1.5, 2.5 })
            {
                Call(window, "ApplyAppScale", scale, false); Render(root, 1200, 780);
                Require(title.ActualHeight > 0 && metadata.ActualHeight > 0, locale + ": heading survives app scaling.");
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
        Console.WriteLine("RENDERED: " + output);
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
