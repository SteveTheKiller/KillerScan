using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;

internal static class ReleaseUiTests
{
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    private sealed class TestApplication : KillerScan.App
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }

    public static Task Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Capture(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(120)), "Offscreen UI checks finish within two minutes.");
        if (failure != null) throw new InvalidOperationException("Release UI check failed.", failure);
        return Task.CompletedTask;
    }

    private static void Capture()
    {
        var assembly = typeof(KillerScan.App).Assembly;
        typeof(Application).GetField("_resourceAssembly", Static)!.SetValue(null, assembly);
        var app = new TestApplication();
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "KillerScan.csproj"))) directory = directory.Parent;
        Require(directory != null, "App resource source exists.");
        var xml = new XmlDocument();
        xml.Load(Path.Combine(directory!.FullName, "App.xaml"));
        string dictionary = xml.DocumentElement!.FirstChild!.FirstChild!.OuterXml
            .Replace("clr-namespace:KillerScan.Controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/KillerScan;component/") };
        context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        context.XmlnsDictionary.Add("controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary, context);
        var manager = assembly.GetType("KillerScan.Services.ThemeManager", true)!;
        var themeType = assembly.GetType("KillerScan.Services.Theme", true)!;
        var accentType = assembly.GetType("KillerScan.Services.Accent", true)!;
        var localeManager = assembly.GetType("KillerScan.Services.LocaleManager", true)!;
        var localeType = assembly.GetType("KillerScan.Services.Locale", true)!;
        assembly.GetType("KillerScan.Services.DemoData", true)!.GetField("Enabled", Static)!.SetValue(null, true);
        void Theme(string theme, string accent)
        {
            manager.GetField("_current", Static)!.SetValue(null, Enum.Parse(themeType, theme));
            manager.GetMethod("LoadDict", Static)!.Invoke(null,
                new[] { Enum.Parse(themeType, theme), Enum.Parse(accentType, accent) });
        }
        void Locale(string locale)
        {
            object value = Enum.Parse(localeType, locale);
            localeManager.GetField("_current", Static)!.SetValue(null, value);
            localeManager.GetMethod("ApplyInternal", Static)!.Invoke(null, new[] { value });
            ((Action?)localeManager.GetField("LocaleChanged", Static)!.GetValue(null))?.Invoke();
        }
        Theme("Black", "Orange");
        Locale("EnUS");
        var window = new KillerScan.Shell.MainWindow();
        var root = (FrameworkElement)window.Content;
        root.Opacity = 1;
        ((UIElement)window.FindName("RootGrid")).Opacity = 1;
        var output = Path.Combine(Path.GetTempPath(), "KillerScan-release-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var samples = new List<(string Label, BitmapSource Image)>();
        int states = 0;
        foreach (string theme in Enum.GetNames(themeType))
        {
            string defaultAccent = theme == "SE98" ? "Blue" : "Orange";
            foreach (string accent in new[] { defaultAccent }.Concat(
                new[] { "Dark", "Light", "Black", "SE98" }.Contains(theme) ? Enum.GetNames(accentType) : Array.Empty<string>()))
            {
                Theme(theme, accent);
                window.GetType().GetMethod("ApplyFlatChrome", Instance)!.Invoke(window, null);
                bool flat = theme == "SE98";
                var panel = new StackPanel { Background = (Brush)app.FindResource("BackgroundBrush"), Width = 370 };
                foreach (string style in new[] { "PrimaryButton", "OutlineButton" })
                {
                    foreach (string state in new[] { "normal", "hover", "pressed", "disabled" })
                    {
                        var button = new Button { Content = style + " " + state, Style = (Style)app.FindResource(style), Margin = new Thickness(8, 3, 8, 3) };
                        panel.Children.Add(button);
                        button.ApplyTemplate();
                        if (state is "hover" or "pressed")
                            button.SetValue((DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", Static)!.GetValue(null)!, true);
                        if (state == "pressed")
                            button.SetValue((DependencyPropertyKey)typeof(ButtonBase).GetField("IsPressedPropertyKey", Static)!.GetValue(null)!, true);
                        if (state == "disabled") button.IsEnabled = false;
                        if (style == "PrimaryButton")
                            Require(flat || button.Background is LinearGradientBrush, theme + "/" + accent + ": primary fill uses a gradient.");
                        if (style == "OutlineButton" && state == "hover")
                        {
                            var border = (Border)button.Template.FindName("border", button);
                            Require(flat || border.Background is LinearGradientBrush, theme + "/" + accent + ": outline hover uses a gradient.");
                        }
                        if (style == "OutlineButton" && state == "pressed")
                            Require(Equals(button.Foreground, app.FindResource("SelectionFg")), "Pressed outline text follows selection contrast.");
                    }
                }
                var image = Render(panel, 370, 340);
                samples.Add((theme + "/" + accent, image));
                states++;
                if (accent == defaultAccent)
                    Save(Render(root, 1200, 780), Path.Combine(output, theme + ".png"));
            }
        }
        Require(states == 45, "All 45 default and explicit theme/accent states render.");
        Theme("Black", "Orange");
        foreach (string locale in Enum.GetNames(localeType))
        {
            Locale(locale);
            window.GetType().GetMethod("RefreshWorkspaceLocale", Instance)!.Invoke(window, null);
            Require(app.FindResource("Str_Btn_Scan") is string text && text.Length > 0, locale + ": scan label resolves.");
            if (locale is "UkUA" or "NbNO" or "PtBR")
                foreach (double scale in new[] { 1.0, 1.5 })
                {
                    window.GetType().GetMethod("ApplyAppScale", Instance)!.Invoke(window, new object[] { scale, false });
                    Save(Render(root, 1200, 780), Path.Combine(output, locale + "-" + scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".png"));
                }
        }
        window.GetType().GetMethod("ApplyAppScale", Instance)!.Invoke(window, new object[] { 1.0, false });
        var menu = new ContextMenu();
        window.GetType().GetMethod("BuildLanguageMenu", Instance)!.Invoke(window, new object[] { menu });
        var columnsPanel = (StackPanel)menu.Items[0];
        var leftColumn = (StackPanel)columnsPanel.Children[0];
        var rightColumn = (StackPanel)columnsPanel.Children[2];
        var languages = leftColumn.Children.Cast<RadioButton>().Concat(rightColumn.Children.Cast<RadioButton>()).ToArray();
        Require(leftColumn.Children.Count == 10 && rightColumn.Children.Count == 9, "Language menu has two balanced columns.");
        foreach (string locale in new[] { "UkUA", "NbNO", "PtBR" })
            Require(languages.Any(radio => Equals(radio.Tag, locale)), locale + ": language is selectable in the menu.");
        Require(languages.Count(radio => radio.IsChecked == true) == 1, "Language menu marks exactly one current locale.");
        menu.Items.Clear();
        var menuSurface = new Border { Child = columnsPanel, Background = (Brush)app.FindResource("MenuBackgroundBrush") };
        Save(Render(menuSurface, 450, 420), Path.Combine(output, "LanguageMenu.png"));
        var workspace = (KillerScan.Controls.ScanWorkspace)window.GetType().GetField("_scanWorkspace", Instance)!.GetValue(window)!;
        workspace.SetView("services");
        Render(root, 1200, 780);
        CheckServiceMenu(workspace, root, output);
        workspace.SetView("topology");
        Render(root, 1200, 780);
        string svg = (string)workspace.GetType().GetMethod("BuildTopologySvg", Instance)!.Invoke(workspace, null)!;
        Require(svg.Contains("<svg") && svg.Contains("</svg>"), "Topology export contains the full SVG drawing.");
        File.WriteAllText(Path.Combine(output, "topology.html"), svg);
        Save(Render(root, 1200, 780), Path.Combine(output, "Topology.png"));
        var canvas = (Canvas)workspace.FindName("TopologyCanvas");
        Require(canvas.Width > 0 && canvas.Height > 0 && canvas.Children.Count > 0, "Topology has arranged device content.");
        CheckTopologySettings(workspace, root, canvas, output, Locale);
        var formatType = workspace.GetType().GetNestedType("SnapshotFormat", BindingFlags.NonPublic)!;
        foreach (string format in new[] { "PngTransparent", "Jpeg" })
        {
            string path = Path.Combine(output, "Topology-export." + (format == "Jpeg" ? "jpg" : "png"));
            workspace.GetType().GetMethod("WriteTopologyRaster", Instance)!.Invoke(workspace,
                new object[] { path, Enum.Parse(formatType, format) });
            using var imageStream = File.OpenRead(path);
            var image = BitmapDecoder.Create(imageStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            Require(image.PixelWidth == (int)Math.Ceiling(canvas.Width) && image.PixelHeight == (int)Math.Ceiling(canvas.Height),
                format + ": export covers the entire topology canvas.");
        }
        window.GetType().GetMethod("DisposeWorkspace", Instance)!.Invoke(window, null);
        int columns = 5, rows = (samples.Count + columns - 1) / columns;
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
            for (int i = 0; i < samples.Count; i++)
            {
                var rect = new Rect(i % columns * 370, i / columns * 370, 370, 370);
                dc.DrawRectangle(Brushes.White, null, rect);
                dc.DrawText(new FormattedText(samples[i].Label, System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.Black, 1), new Point(rect.X + 8, rect.Y + 5));
                dc.DrawImage(samples[i].Image, new Rect(rect.X, rect.Y + 28, 370, 340));
            }
        var sheet = new RenderTargetBitmap(columns * 370, rows * 370, 96, 96, PixelFormats.Pbgra32);
        sheet.Render(drawing);
        Save(sheet, Path.Combine(output, "ButtonStates.png"));
        Console.WriteLine("PASS: 45 theme/accent states, 19 locales, new-language scaling, services and topology rendered.");
        Console.WriteLine("Offscreen release UI: " + output);
    }

    private static void CheckServiceMenu(KillerScan.Controls.ScanWorkspace workspace, FrameworkElement root, string output)
    {
        var type = workspace.GetType();
        var grid = (DataGrid)workspace.FindName("ServicesGrid");
        var devicesGrid = (DataGrid)workspace.FindName("ResultsGrid");
        Require(!ReferenceEquals(grid.ContextMenu, devicesGrid.ContextMenu), "Services have their own context menu.");
        var actions = grid.ContextMenu.Items.OfType<MenuItem>().ToDictionary(item => (string)item.Tag);
        Require(actions.Keys.OrderBy(value => value).SequenceEqual(new[] { "Browser", "CopyEndpoint", "CopyHost", "CopyIp", "CopyPort",
            "CopyService", "Export", "Rdp", "SelectAll", "Ssh", "SshAs" }.OrderBy(value => value)),
            "The service menu contains service actions and excludes device trust, renaming, MAC copying and type overrides.");
        var original = ((IEnumerable<KillerScan.Engine.NetworkDevice>)type.GetProperty("ScannedDevices", Instance)!.GetValue(workspace)!).ToArray();
        string originalTarget = workspace.Targets;
        var actionField = type.GetField("DeviceAction", Instance)!;
        object? originalHandler = actionField.GetValue(workspace);
        actionField.SetValue(workspace, null);
        KillerScan.Controls.ScanDeviceActionEventArgs? raised = null;
        workspace.DeviceAction += (_, e) => raised = e;
        var host = new KillerScan.Engine.NetworkDevice
        {
            IpAddress = "192.0.2.40", Hostname = "service-test", DeviceType = "Server",
            OpenPorts = new List<int> { 53, 80, 8443, 22, 3389 }
        };
        var unnamed = new KillerScan.Engine.NetworkDevice
        {
            IpAddress = "192.0.2.41", MacAddress = "00:11:22:33:44:55", OpenPorts = new List<int> { 53 }
        };
        var load = type.GetMethod("LoadSnapshot", Instance)!;
        var refresh = type.GetMethod("RefreshServices", Instance)!;
        var prepare = type.GetMethod("ServicesGrid_ContextMenuOpening", Instance)!;
        var copy = type.GetMethod("ServiceCopyText", Instance)!;
        object Row(int port, string ip = "192.0.2.40") => grid.Items.Cast<object>().Single(row =>
            (int)row.GetType().GetProperty("Port")!.GetValue(row)! == port && (string)row.GetType().GetProperty("IpAddress")!.GetValue(row)! == ip);
        void Select(object row)
        {
            grid.SelectedItems.Clear(); grid.SelectedItem = row; grid.CurrentItem = row;
            prepare.Invoke(workspace, new object?[] { grid, null });
        }
        try
        {
            load.Invoke(workspace, new object[] { "192.0.2.0/24", new[] { host, unnamed } });
            refresh.Invoke(workspace, null);
            Render(root, 1200, 780);
            void RightClick(object item)
            {
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(item);
                Require(row != null, "The service row is rendered for right-click selection checks.");
                var click = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                    System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.PreviewMouseRightButtonDownEvent, Source = row };
                type.GetMethod("ServicesGrid_RightClick", Instance)!.Invoke(workspace, new object[] { grid, click });
            }
            Select(Row(53));
            grid.SelectedItems.Add(Row(8443));
            RightClick(Row(8443));
            Require(grid.SelectedItems.Count == 2 && ReferenceEquals(grid.CurrentItem, Row(8443)),
                "Right-click preserves a selected group and targets the clicked service.");
            RightClick(Row(22));
            Require(grid.SelectedItems.Count == 1 && ReferenceEquals(grid.CurrentItem, Row(22)),
                "Right-clicking a different service replaces the selection.");
            Select(Row(53));
            Require(new[] { "Browser", "Ssh", "SshAs", "Rdp" }.All(action => actions[action].Visibility == Visibility.Collapsed),
                "DNS services do not show web, SSH or RDP actions.");
            Require((string)copy.Invoke(workspace, new object[] { "CopyEndpoint" })! == "192.0.2.40:53" &&
                (string)copy.Invoke(workspace, new object[] { "CopyService" })! == "DNS" &&
                (string)copy.Invoke(workspace, new object[] { "CopyPort" })! == "53", "Copy actions use the selected service endpoint.");
            grid.SelectedItems.Add(Row(53, "192.0.2.41"));
            Require(((string)copy.Invoke(workspace, new object[] { "CopyEndpoint" })!).Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length == 2,
                "Copy endpoint includes all selected services.");
            Select(Row(53, "192.0.2.41"));
            Require(!actions["CopyHost"].IsEnabled && (string)copy.Invoke(workspace, new object[] { "CopyHost" })! == "",
                "Copy hostname never substitutes a MAC address.");
            foreach (var entry in new[] { (80, "Browser"), (8443, "Browser"), (22, "Ssh"), (3389, "Rdp") })
            {
                Select(Row(entry.Item1));
                Require(actions[entry.Item2].Visibility == Visibility.Visible, "Matching connection action is visible.");
                raised = null;
                actions[entry.Item2].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(raised != null && raised.Device == host && raised.Action == entry.Item2 && raised.ServicePort == entry.Item1,
                    "Connection actions carry the selected service's host and port.");
            }
            Select(Row(53));
            raised = null;
            type.GetMethod("RaiseDeviceAction", Instance)!.Invoke(workspace, new object[] { "Browser", false });
            Require(raised == null, "The browser keyboard action cannot open an unrelated service or a stale selected device.");
            Select(Row(8443));
            type.GetMethod("RaiseDeviceAction", Instance)!.Invoke(workspace, new object[] { "Browser", false });
            Require(raised?.ServicePort == 8443, "The browser keyboard action uses the selected service port.");
            var browserUri = type.GetMethod("ServiceBrowserUri", Static)!;
            var https = (Uri)browserUri.Invoke(null, new object[] { "192.0.2.40", 8443 })!;
            var ipv6 = (Uri)browserUri.Invoke(null, new object[] { "2001:db8::40", 8080 })!;
            Require(https.Scheme == "https" && https.Port == 8443 && ipv6.Scheme == "http" && ipv6.Port == 8080,
                "Web service URLs preserve HTTP/HTTPS, alternate ports and IPv6 addresses.");
            Require((string)type.GetMethod("ServiceEndpoint", Static)!.Invoke(null, new object[] { "2001:db8::40", 3389 })! == "[2001:db8::40]:3389",
                "IPv6 service endpoints are unambiguous.");
            grid.SelectedItems.Clear();
            prepare.Invoke(workspace, new object?[] { grid, null });
            Require(actions.Where(pair => pair.Key.StartsWith("Copy", StringComparison.Ordinal)).All(pair => !pair.Value.IsEnabled),
                "Empty selections cannot copy a stale service.");
            Select(Row(8443));
            var surface = new StackPanel { Background = (Brush)Application.Current.FindResource("MenuBackgroundBrush") };
            var entries = grid.ContextMenu.Items.Cast<UIElement>().ToArray();
            grid.ContextMenu.Items.Clear();
            foreach (var item in entries) surface.Children.Add(item);
            Save(Render(surface, 350, 340), Path.Combine(output, "ServiceMenu.png"));
            surface.Children.Clear();
            foreach (var item in entries) grid.ContextMenu.Items.Add(item);
            Console.WriteLine("PASS: service-specific menu, selection, copy data and protocol/port actions.");
        }
        finally
        {
            actionField.SetValue(workspace, originalHandler);
            load.Invoke(workspace, new object[] { originalTarget, original });
            refresh.Invoke(workspace, null);
        }
    }

    private static void CheckTopologySettings(KillerScan.Controls.ScanWorkspace workspace, FrameworkElement root,
        Canvas canvas, string output, Action<string> locale)
    {
        var type = workspace.GetType();
        var options = (Dictionary<string, string>)type.GetField("_topologyOptions", Instance)!.GetValue(workspace)!;
        var settings = ((Button)workspace.FindName("TopologyOrderButton")).ContextMenu;
        var reset = type.GetMethod("TopologyReset_Click", Instance)!;
        reset.Invoke(workspace, new object[] { workspace, new RoutedEventArgs() });
        Require(settings.Items.Count == 11 && settings.Items.OfType<MenuItem>().Any(item => item.InputGestureText == "Ctrl+Wheel"),
            "Topology settings expose grouping, appearance, zoom, reset and export.");
        settings.Visibility = Visibility.Hidden;
        try
        {
            var click = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonDownEvent };
            canvas.RaiseEvent(click);
            Require(click.Handled && settings.IsOpen && settings.PlacementTarget == canvas && settings.Placement == PlacementMode.MousePoint,
                "Right-clicking the topology background opens settings at the pointer.");
        }
        finally { settings.IsOpen = false; settings.Visibility = Visibility.Visible; }
        var deviceNode = canvas.Children.OfType<Border>().First(node => node.Tag != null);
        var deviceClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
            System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonDownEvent, Source = deviceNode };
        type.GetMethod("TopologyBackground_RightClick", Instance)!.Invoke(workspace, new object[] { canvas, deviceClick });
        Require(!settings.IsOpen && !deviceClick.Handled, "Background settings leave device right-clicks to the device menu.");
        var grouping = (MenuItem)settings.Items[0];
        foreach (MenuItem item in grouping.Items)
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            Require(item.IsChecked && grouping.Items.Cast<MenuItem>().Count(choice => choice.IsChecked) == 1,
                "Topology grouping choices apply independently of appearance settings.");
        }
        var optionItems = settings.Items.OfType<MenuItem>().SelectMany(group => group.Items.OfType<MenuItem>())
            .Where(item => item.Tag is string tag && tag.Contains("=")).ToArray();
        foreach (var item in optionItems)
        {
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            string[] parts = ((string)item.Tag).Split('=');
            Require(options[parts[0]] == parts[1] && item.IsChecked, "Topology choice applies and stays checked: " + item.Tag);
            Require(optionItems.Count(other => ((string)other.Tag).StartsWith(parts[0] + "=") && other.IsChecked) == 1,
                "Each topology setting has one selected choice.");
        }
        var links = settings.Items.OfType<MenuItem>().Single(item => Equals(item.Tag, "Links"));
        var positions = (Dictionary<string, Point>)type.GetField("_topologyPositions", Instance)!.GetValue(workspace)!;
        string ip = (string)deviceNode.Tag.GetType().GetProperty("IpAddress")!.GetValue(deviceNode.Tag)!;
        positions[ip] = new Point(100, 100);
        optionItems.Single(item => Equals(item.Tag, "Font=Segoe UI")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(positions.ContainsKey(ip), "Changing font family preserves manually positioned devices.");
        links.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, links));
        Require(canvas.Children.OfType<System.Windows.Shapes.Line>().All(line => line.Opacity == 0), "Connections can be hidden.");
        links.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, links));
        Require(canvas.Children.OfType<System.Windows.Shapes.Line>().All(line => line.Opacity > 0), "Connections can be restored.");
        Require(positions.ContainsKey(ip), "Toggling connections preserves manually positioned devices.");
        reset.Invoke(workspace, new object[] { workspace, new RoutedEventArgs() });
        int combinations = 0;
        foreach (string pattern in new[] { "Hierarchy", "Radial" })
            foreach (string orientation in new[] { "Vertical", "Horizontal" })
                foreach (string scale in new[] { "75", "100", "125", "150", "200" })
                    foreach (string size in new[] { "10", "11", "12", "14", "16", "18" })
                    {
                        options["Pattern"] = pattern;
                        options["Orientation"] = orientation;
                        options["NodeScale"] = scale;
                        options["FontSize"] = size;
                        type.GetMethod("RefreshTopology", Instance)!.Invoke(workspace, null);
                        root.Measure(new Size(1200, 780));
                        root.Arrange(new Rect(0, 0, 1200, 780));
                        root.UpdateLayout();
                        var bounds = canvas.Children.OfType<Border>()
                            .Select(node => new Rect(Canvas.GetLeft(node), Canvas.GetTop(node), node.Width, node.Height)).ToArray();
                        for (int i = 0; i < bounds.Length; i++)
                        {
                            Require(bounds[i].Left >= 0 && bounds[i].Top >= 0 && bounds[i].Right <= canvas.Width + 0.1 &&
                                bounds[i].Bottom <= canvas.Height + 0.1, "All topology boxes stay within the canvas.");
                            for (int j = i + 1; j < bounds.Length; j++)
                                Require(!bounds[i].IntersectsWith(bounds[j]), "Automatic topology boxes do not overlap.");
                        }
                        foreach (var node in canvas.Children.OfType<Border>())
                        {
                            var content = (StackPanel)node.Child;
                            Require(content.DesiredSize.Height <= node.Height, "Larger topology fonts fit inside the boxes.");
                        }
                        if (scale == "100" && size == "11")
                            Save(Render(root, 1200, 780), Path.Combine(output, "Topology-" + pattern + "-" + orientation + ".png"));
                        combinations++;
                    }
        foreach (string language in Enum.GetNames(typeof(KillerScan.App).Assembly.GetType("KillerScan.Services.Locale", true)!))
        {
            locale(language);
            foreach (string key in new[] { "Settings", "Pattern", "Orientation", "NodeScale", "Font", "FontSize", "Hierarchy",
                "Radial", "Vertical", "Horizontal", "Default", "Links", "Reset" })
                Require(Application.Current.FindResource("Str_Topology_" + key) is string text && text.Length > 0,
                    language + ": topology setting label resolves.");
        }
        locale("EnUS");
        var surface = new StackPanel { Background = (Brush)Application.Current.FindResource("MenuBackgroundBrush") };
        var entries = settings.Items.Cast<object>().ToArray();
        settings.Items.Clear();
        foreach (UIElement entry in entries) surface.Children.Add(entry);
        Save(Render(surface, 300, 360), Path.Combine(output, "TopologySettings.png"));
        surface.Children.Clear();
        foreach (var entry in entries) settings.Items.Add(entry);
        options["Font"] = "Consolas";
        type.GetMethod("RefreshTopology", Instance)!.Invoke(workspace, null);
        Render(root, 1200, 780);
        string svg = (string)type.GetMethod("BuildTopologySvg", Instance)!.Invoke(workspace, null)!;
        Require(svg.Contains("font-family=\"Consolas\"") && svg.Contains("font-size=\"18\""), "Topology SVG export follows the selected fonts.");
        reset.Invoke(workspace, new object[] { workspace, new RoutedEventArgs() });
        Require(options["Pattern"] == "Hierarchy" && options["Orientation"] == "Vertical" && options["Font"] == "Default" &&
            options["FontSize"] == "11" && options["NodeScale"] == "100" && options["Links"] == "On", "Topology settings reset to the original appearance.");
        Render(root, 1200, 780);
        Console.WriteLine("PASS: " + combinations + " topology layout/size/font combinations and all menu choices.");
    }

    private static BitmapSource Render(FrameworkElement element, int width, int height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(element);
        var pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        int painted = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i + 3] > 0 && (pixels[i] > 0 || pixels[i + 1] > 0 || pixels[i + 2] > 0)) painted++;
        Require(painted > width * height / 100, "Rendered content must contain visible colored pixels.");
        return image;
    }

    private static void Save(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
