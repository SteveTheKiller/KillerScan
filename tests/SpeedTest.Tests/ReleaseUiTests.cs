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

    public static Task Run(bool scanOptionsOnly = false)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Capture(scanOptionsOnly); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(120)), "Offscreen UI checks finish within two minutes.");
        if (failure != null) throw new InvalidOperationException("Release UI check failed.", failure);
        return Task.CompletedTask;
    }

    private static void Capture(bool scanOptionsOnly)
    {
        var assembly = typeof(KillerScan.App).Assembly;
        typeof(Application).GetField("_resourceAssembly", Static)!.SetValue(null, assembly);
        var app = new TestApplication();
        KillerScan.Services.AppTheme.Configure();
        var directory = TestRepository.Find();
        Require(directory != null, "App resource source exists.");
        var xml = new XmlDocument();
        xml.Load(Path.Combine(directory!.FullName, "App.xaml"));
        string dictionary = xml.DocumentElement!.FirstChild!.FirstChild!.OuterXml
            .Replace("clr-namespace:KillerScan.Controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan")
            .Replace("clr-namespace:KillerFamily.Layout\"", "clr-namespace:KillerFamily.Layout;assembly=KillerScan\"")
            .Replace("clr-namespace:KillerFamily.Controls\"", "clr-namespace:KillerFamily.Controls;assembly=KillerScan\"");
        // Preserve compiled-XAML glyph references through the .NET Framework loose parser.
        foreach (char glyph in dictionary.Where(c => c >= 0xE000 && c <= 0xF8FF).Distinct().ToArray())
            dictionary = dictionary.Replace(glyph.ToString(), "&#x" + ((int)glyph).ToString("X4") + ";");
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/KillerScan;component/") };
        context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        context.XmlnsDictionary.Add("controls", "clr-namespace:KillerScan.Controls;assembly=KillerScan");
        context.XmlnsDictionary.Add("snap", "clr-namespace:KillerFamily.Windowing;assembly=KillerScan");
        context.XmlnsDictionary.Add("kl", "clr-namespace:KillerFamily.Layout;assembly=KillerScan");
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
        if (scanOptionsOnly)
        {
            var scanWorkspace = (KillerScan.Controls.ScanWorkspace)window.GetType().GetField("_scanWorkspace", Instance)!.GetValue(window)!;
            Render(root, 1200, 780);
            ScanOptionsUiChecks.Run(scanWorkspace);
            var settingsButton = (Button)scanWorkspace.FindName("ScanOptionsButton");
            void Pump()
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                    new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
            }
            void CaptureSettings(string name, int windowWidth = 1200)
            {
                Pump();
                Save(Render(root, windowWidth, 780), Path.Combine(output, name + ".png"));
                var buttonBounds = settingsButton.TransformToAncestor(root).TransformBounds(new Rect(settingsButton.RenderSize));
                Require(buttonBounds.Width > 0 && buttonBounds.Left >= 0 && buttonBounds.Right <= windowWidth,
                    name + ": the complete scan settings button remains inside the window.");
                settingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                var settingsMenu = settingsButton.ContextMenu;
                settingsMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
                Pump();
                settingsMenu.Measure(new Size(650, 900));
                // The family menu fades in from 0 when it opens; render its settled state.
                if (settingsMenu.Template.FindName("MenuRoot", settingsMenu) is UIElement menuRoot)
                {
                    menuRoot.BeginAnimation(UIElement.OpacityProperty, null);
                    menuRoot.Opacity = 1;
                }
                int width = (int)Math.Ceiling(settingsMenu.DesiredSize.Width);
                int height = (int)Math.Ceiling(settingsMenu.DesiredSize.Height);
                Require(width > 100 && height > 100, "Scan settings menu measures visible content.");
                Save(Render(settingsMenu, width, height), Path.Combine(output, name + "-menu.png"));
                settingsMenu.IsOpen = false;
                Pump();
            }
            foreach (string theme in Enum.GetNames(themeType))
            {
                Theme(theme, theme == "SE98" ? "Blue" : "Orange");
                window.GetType().GetMethod("ApplyFlatChrome", Instance)!.Invoke(window, null);
                CaptureSettings(theme);
            }
            Theme("Black", "Orange");
            foreach (string locale in Enum.GetNames(localeType))
            {
                Locale(locale);
                Pump();
                Render(root, 1200, 780);
                Require(System.Windows.Automation.AutomationProperties.GetName(settingsButton)
                    == (string)app.FindResource("Str_Options_Title"), locale + ": scan settings accessible label follows the language.");
                settingsButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                foreach (var item in settingsButton.ContextMenu.Items.OfType<MenuItem>().Where(item => item.IsCheckable))
                    // Untagged is the unknown-port choice, which sits outside ScanOptions.Features.
                    Require(item.Header is string label && label == (string)app.FindResource("Str_Options_" + (item.Tag as string ?? "ProbeUnknownPorts")),
                        locale + ": every scan setting has a translated label.");
                settingsButton.ContextMenu.IsOpen = false;
                if (locale is "SlSI" or "De") CaptureSettings(locale);
            }
            Locale("SlSI");
            CaptureSettings("SlSI-narrow", 800);
            window.GetType().GetMethod("ApplyAppScale", Instance)!.Invoke(window, new object[] { 1.5, false });
            CaptureSettings("SlSI-scale150");
            Console.WriteLine("PASS: scan settings button, menu, profile JSON, defaults, dependencies, 14 themes and 20 locales.");
            Console.WriteLine("Offscreen scan settings UI: " + output);
            return;
        }
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
                if (flat)
                {
                    var maximize = (Button)window.FindName("MaximizeBtn");
                    var classicImage = Render(root, 1200, 780);
                    var toolbarPixel = new byte[4];
                    classicImage.CopyPixels(new Int32Rect(1100, 32, 1, 1), toolbarPixel, 4, 0);
                    Require(toolbarPixel[0] == 192 && toolbarPixel[1] == 192 && toolbarPixel[2] == 192,
                        "The classic client toolbar uses the Windows 98 gray face, without a black gap.");
                    var captionRoot = (Grid)window.FindName("RootGrid");
                    var captionBox = maximize.TransformToAncestor(captionRoot).TransformBounds(new Rect(maximize.RenderSize));
                    var tools = (FrameworkElement)window.FindName("TitleToolbarHost");
                    Require(tools.TransformToAncestor(captionRoot).TransformBounds(new Rect(tools.RenderSize)).Top >= 18,
                        "The scan toolbar sits below the original Windows 98 caption instead of being clipped inside it.");
                    Require(Math.Abs(captionRoot.RowDefinitions[0].ActualHeight - 18) < 0.1 &&
                        Math.Abs(captionBox.Top) < 0.1 && Math.Abs(captionBox.Bottom - 18) < 0.1 && Math.Abs(captionBox.Width - 20) < 0.1,
                        "Windows 98 has an eighteen-pixel caption whose 20 by 18 kit caption buttons fill it top to bottom: caption "
                            + captionRoot.RowDefinitions[0].ActualHeight + ", button " + captionBox.Top + " to " + captionBox.Bottom + ", width " + captionBox.Width + ".");
                    Require(maximize.Content is FrameworkElement,
                        "98SE caption buttons use the established drawn classic glyphs, not modern font glyphs.");
                    Require(((UIElement)window.FindName("ClassicMinimizeGlyph")).Visibility == Visibility.Visible &&
                        ((UIElement)window.FindName("ClassicMaximizeGlyph")).Visibility == Visibility.Visible &&
                        ((UIElement)window.FindName("ClassicCloseGlyph")).Visibility == Visibility.Visible &&
                        ((UIElement)window.FindName("MaximizeGlyph")).Visibility == Visibility.Collapsed,
                        "98SE displays its drawn minimize, maximize and close symbols and hides modern glyphs.");
                }
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
                            Require(Equals(button.Foreground, app.FindResource("OutlineHoverTextBrush")), "Pressed outline keeps the accent fill and its text color.");
                    }
                }
                var image = Render(panel, 370, 340);
                samples.Add((theme + "/" + accent, image));
                states++;
                if (accent == defaultAccent)
                    Save(Render(root, 1200, 780), Path.Combine(output, theme + ".png"));
            }
        }
        // Every theme once with its default accent, plus each accent on the four themes that offer the full set.
        int expectedStates = Enum.GetNames(themeType).Length + 4 * Enum.GetNames(accentType).Length;
        Require(states == expectedStates, "All " + expectedStates + " default and explicit theme/accent states render (" + states + " did).");
        Theme("Black", "Orange");
        foreach (string locale in Enum.GetNames(localeType))
        {
            Locale(locale);
            window.GetType().GetMethod("RefreshWorkspaceLocale", Instance)!.Invoke(window, null);
            Require(app.FindResource("Str_Btn_Scan") is string text && text.Length > 0, locale + ": scan label resolves.");
            if (locale is "UkUA" or "NbNO" or "PtBR" or "SlSI")
                foreach (double scale in new[] { 1.0, 1.5 })
                {
                    window.GetType().GetMethod("ApplyAppScale", Instance)!.Invoke(window, new object[] { scale, false });
                    Save(Render(root, 1200, 780), Path.Combine(output, locale + "-" + scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ".png"));
                }
        }
        window.GetType().GetMethod("ApplyAppScale", Instance)!.Invoke(window, new object[] { 1.0, false });
        var menu = new ContextMenu();
        window.GetType().GetMethod("BuildLanguageMenu", Instance)!.Invoke(window, new object[] { menu });
        // The columns ride in a PanelMenuItem, the gutter-free container for custom menu panels.
        var columnsItem = (MenuItem)menu.Items[0];
        var columnsPanel = (StackPanel)columnsItem.Header;
        var leftColumn = (StackPanel)columnsPanel.Children[0];
        var rightColumn = (StackPanel)columnsPanel.Children[2];
        var languages = leftColumn.Children.Cast<RadioButton>().Concat(rightColumn.Children.Cast<RadioButton>()).ToArray();
        Require(leftColumn.Children.Count == 10 && rightColumn.Children.Count == 10, "Language menu has two balanced columns.");
        foreach (string locale in new[] { "UkUA", "NbNO", "PtBR", "SlSI" })
            Require(languages.Any(radio => Equals(radio.Tag, locale)), locale + ": language is selectable in the menu.");
        Require(languages.Count(radio => radio.IsChecked == true) == 1, "Language menu marks exactly one current locale.");
        columnsItem.Header = null;
        menu.Items.Clear();
        var menuSurface = new Border { Child = columnsPanel, Background = (Brush)app.FindResource("MenuBackgroundBrush") };
        Save(Render(menuSurface, 450, 420), Path.Combine(output, "LanguageMenu.png"));
        var workspace = (KillerScan.Controls.ScanWorkspace)window.GetType().GetField("_scanWorkspace", Instance)!.GetValue(window)!;
        ScanOptionsUiChecks.Run(workspace);
        workspace.SetView("services");
        Render(root, 1200, 780);
        CheckServiceMenu(workspace, root, output);
        CheckDeviceCard(workspace);
        workspace.SetView("topology");
        Render(root, 1200, 780);
        string svg = (string)workspace.GetType().GetMethod("BuildTopologySvg", Instance)!.Invoke(workspace, null)!;
        Require(svg.Contains("<svg") && svg.Contains("</svg>"), "Topology export contains the full SVG drawing.");
        File.WriteAllText(Path.Combine(output, "topology.html"), svg);
        Save(Render(root, 1200, 780), Path.Combine(output, "Topology.png"));
        var canvas = (Canvas)workspace.FindName("TopologyCanvas");
        Require(canvas.Width > 0 && canvas.Height > 0 && canvas.Children.Count > 0, "Topology has arranged device content.");
        CheckTopologySettings(workspace, root, canvas, output, Locale);
        foreach (string view in new[] { "devices", "services", "topology" })
        {
            workspace.SetView(view);
            CheckFilmGrainOff(root, view);
        }
        workspace.SetView("topology");
        Render(root, 1200, 780);
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
        Console.WriteLine("PASS: 45 theme/accent states, 20 locales, new-language scaling, services and topology rendered.");
        Console.WriteLine("Offscreen release UI: " + output);
    }

    // The device card: its header names the right-clicked device (or counts a selection), every
    // quick button and chip shows its shortcut, nothing is live with no device selected, and a
    // button runs the same action its old menu row did.
    private static void CheckDeviceCard(KillerScan.Controls.ScanWorkspace workspace)
    {
        var type = workspace.GetType();
        var grid = (DataGrid)workspace.FindName("ResultsGrid");
        var menu = grid.ContextMenu;
        var prepare = type.GetMethod("PrepareDeviceContextMenu", Instance)!;
        var load = type.GetMethod("LoadSnapshot", Instance)!;
        var original = ((IEnumerable<KillerScan.Engine.NetworkDevice>)type.GetProperty("ScannedDevices", Instance)!.GetValue(workspace)!).ToArray();
        string originalTarget = workspace.Targets;
        var actionField = type.GetField("DeviceAction", Instance)!;
        object? originalHandler = actionField.GetValue(workspace);
        actionField.SetValue(workspace, null);
        KillerScan.Controls.ScanDeviceActionEventArgs? raised = null;
        workspace.DeviceAction += (_, e) => raised = e;
        var named = new KillerScan.Engine.NetworkDevice { IpAddress = "192.0.2.50", Hostname = "card-test", MacAddress = "00:11:22:33:44:66" };
        var other = new KillerScan.Engine.NetworkDevice { IpAddress = "192.0.2.51" };
        try
        {
            load.Invoke(workspace, new object?[] { "192.0.2.0/24", new[] { named, other }, null });
            // The tiles a single device offers, each carrying its shortcut; the lists open from flyout tiles.
            string[] keyed = ["Browser", "Ping", "Diagnose", "Watch", "Rdp", "Ssh", "SshAs", "CopyIp", "CopyMac", "CopyHost",
                "Rename", "ClearName", "Trust", "ClearOverride", "WhyType"];
            List<Button> Tiles() => CardMenuTestHelpers.CardButtons(menu).OfType<Button>().Where(b => b.Uid.Length > 0).ToList();

            grid.SelectedItems.Clear();
            prepare.Invoke(workspace, null);
            Require(keyed.All(id => Tiles().Any(b => b.Uid == id && !string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetAcceleratorKey(b)))),
                "The device card has a tile with its shortcut for every device action.");
            Require(Tiles().Where(b => keyed.Contains(b.Uid)).All(b => !b.IsEnabled), "With no device selected, no device tile is live.");

            grid.SelectedItem = named; grid.CurrentItem = named;
            prepare.Invoke(workspace, null);
            Require(HeaderText(menu) == "card-test" && Tiles().Where(b => keyed.Contains(b.Uid)).All(b => b.IsEnabled),
                "The card is titled with the device's name and its tiles are live.");
            raised = null;
            CardMenuTestHelpers.Click(CardMenuTestHelpers.Actions(menu)["Ping"]);
            Require(raised?.Device == named && raised.Action == "Ping", "The Ping button runs Ping on the selected device.");

            grid.SelectedItems.Add(other);
            prepare.Invoke(workspace, null);
            Require(HeaderText(menu) == string.Format((string)Application.Current.FindResource("Str_Card_Devices"), 2),
                "A multiple selection is counted in the card's title.");
            Console.WriteLine("PASS: device card header, shortcuts, enabled state and actions.");
        }
        finally
        {
            actionField.SetValue(workspace, originalHandler);
            load.Invoke(workspace, new object?[] { originalTarget, original, null });
        }
    }

    /// <summary>The title on a tile card's header (ScanCards.Header: the first TextBlock in the header slot).</summary>
    private static string HeaderText(ContextMenu menu)
    {
        if (menu.Items.Count == 0 || menu.Items[0] is not MenuItem head || head.Header is not DependencyObject root) return "";
        var pending = new Stack<object>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is TextBlock text) return text.Text;
            if (current is DependencyObject d)
                foreach (var child in LogicalTreeHelper.GetChildren(d).Cast<object>().Reverse()) pending.Push(child);
        }
        return "";
    }

    private static void CheckServiceMenu(KillerScan.Controls.ScanWorkspace workspace, FrameworkElement root, string output)
    {
        var type = workspace.GetType();
        var grid = (DataGrid)workspace.FindName("ServicesGrid");
        var devicesGrid = (DataGrid)workspace.FindName("ResultsGrid");
        Require(!ReferenceEquals(grid.ContextMenu, devicesGrid.ContextMenu), "Services have their own context menu.");
        workspace.PrepareServiceCard();
        var actions = CardMenuTestHelpers.Actions(grid.ContextMenu);
        Require(actions.Keys.OrderBy(value => value).SequenceEqual(new[] { "Browser", "CopyEndpoint", "CopyHost", "CopyIp", "CopyPort",
            "Export", "Rdp", "SelectAll", "Ssh" }.OrderBy(value => value)),
            "The service card shows its default tiles (SSH as and Copy service name start in the drawer) and no device trust, renaming, MAC copying or type overrides.");
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
        // The card is rebuilt from the selection as it opens, so its tiles are looked up after each Select.
        Dictionary<string, FrameworkElement> A() => CardMenuTestHelpers.Actions(grid.ContextMenu);
        void Select(object row)
        {
            grid.SelectedItems.Clear(); grid.SelectedItem = row; grid.CurrentItem = row;
            prepare.Invoke(workspace, new object?[] { grid, null });
        }
        try
        {
            // LoadSnapshot's optional scan time must be passed explicitly through reflection.
            load.Invoke(workspace, new object?[] { "192.0.2.0/24", new[] { host, unnamed }, null });
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
            Require(new[] { "Browser", "Ssh", "Rdp" }.All(action => !A()[action].IsEnabled),
                "DNS services dim the web, SSH and RDP tiles.");
            Require((string)copy.Invoke(workspace, new object[] { "CopyEndpoint" })! == "192.0.2.40:53" &&
                (string)copy.Invoke(workspace, new object[] { "CopyService" })! == "DNS" &&
                (string)copy.Invoke(workspace, new object[] { "CopyPort" })! == "53", "Copy actions use the selected service endpoint.");
            grid.SelectedItems.Add(Row(53, "192.0.2.41"));
            Require(((string)copy.Invoke(workspace, new object[] { "CopyEndpoint" })!).Split(new[] { Environment.NewLine }, StringSplitOptions.None).Length == 2,
                "Copy endpoint includes all selected services.");
            Select(Row(53, "192.0.2.41"));
            Require(!A()["CopyHost"].IsEnabled && (string)copy.Invoke(workspace, new object[] { "CopyHost" })! == "",
                "Copy hostname never substitutes a MAC address.");
            foreach (var entry in new[] { (80, "Browser"), (8443, "Browser"), (22, "Ssh"), (3389, "Rdp") })
            {
                Select(Row(entry.Item1));
                Require(A()[entry.Item2].IsEnabled, "Matching connection tile is live.");
                raised = null;
                CardMenuTestHelpers.Click(A()[entry.Item2]);
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
            load.Invoke(workspace, new object?[] { originalTarget, original, null });
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
        Require(new[] { "OrderRole", "OrderType", "OrderIp", "OrderVendor", "Pattern", "Orientation", "NodeScale", "Font", "FontSize", "Zoom", "Links", "Reset", "Png", "Jpeg", "Svg" }
                .All(id => CardMenuTestHelpers.Tile(settings, id) != null)
            && System.Windows.Automation.AutomationProperties.GetAcceleratorKey(CardMenuTestHelpers.Tile(settings, "Zoom")!) == "Ctrl+Wheel",
            "Topology settings expose grouping, appearance, zoom, reset and export.");
        settings.Visibility = Visibility.Hidden;
        try
        {
            var click = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
                System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonDownEvent };
            canvas.RaiseEvent(click);
            var viewport = (ScrollViewer)workspace.FindName("TopologyScrollViewer");
            Require(click.Handled && settings.IsOpen && settings.PlacementTarget == viewport &&
                settings.Placement == PlacementMode.RelativePoint && settings.PlacementRectangle.TopLeft == click.GetPosition(viewport),
                "Right-clicking the topology background opens settings at the pointer.");
        }
        finally { settings.IsOpen = false; settings.Visibility = Visibility.Visible; }
        var deviceNode = canvas.Children.OfType<Border>().First(node => node.Tag != null);
        var deviceClick = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0,
            System.Windows.Input.MouseButton.Right) { RoutedEvent = UIElement.MouseRightButtonDownEvent, Source = deviceNode };
        type.GetMethod("TopologyBackground_RightClick", Instance)!.Invoke(workspace, new object[] { canvas, deviceClick });
        Require(!settings.IsOpen && !deviceClick.Handled, "Background settings leave device right-clicks to the device menu.");
        // The card is built as it opens, so each check rebuilds it and reads the tiles and flyout rows afresh.
        var orderField = type.GetField("_topologyOrder", Instance)!;
        foreach (string order in new[] { "Type", "Ip", "Vendor", "Role" })
        {
            workspace.PrepareTopologyCard();
            CardMenuTestHelpers.Click(CardMenuTestHelpers.Tile(settings, "Order" + order)!);
            Require(orderField.GetValue(workspace)!.ToString() == order,
                "Topology grouping choices apply independently of appearance settings.");
        }
        MenuItem[] OptionRows()
        {
            workspace.PrepareTopologyCard();
            return new[] { "Pattern", "Orientation", "NodeScale", "Font", "FontSize" }
                .SelectMany(id => KillerScan.Controls.ScanCards.GetFlyoutRows(CardMenuTestHelpers.Tile(settings, id)!)!.OfType<MenuItem>()).ToArray();
        }
        foreach (string tag in OptionRows().Select(item => (string)item.Tag).ToArray())
        {
            var item = OptionRows().Single(row => (string)row.Tag == tag);
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
            string[] parts = tag.Split('=');
            var rows = OptionRows();
            Require(options[parts[0]] == parts[1] && rows.Single(row => (string)row.Tag == tag).IsChecked, "Topology choice applies and stays checked: " + tag);
            Require(rows.Count(other => ((string)other.Tag).StartsWith(parts[0] + "=") && other.IsChecked) == 1,
                "Each topology setting has one selected choice.");
        }
        void ToggleLinks() { workspace.PrepareTopologyCard(); CardMenuTestHelpers.Click(CardMenuTestHelpers.Tile(settings, "Links")!); }
        var positions = (Dictionary<string, Point>)type.GetField("_topologyPositions", Instance)!.GetValue(workspace)!;
        string ip = (string)deviceNode.Tag.GetType().GetProperty("IpAddress")!.GetValue(deviceNode.Tag)!;
        positions[ip] = new Point(100, 100);
        OptionRows().Single(item => Equals(item.Tag, "Font=Segoe UI")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Require(positions.ContainsKey(ip), "Changing font family preserves manually positioned devices.");
        ToggleLinks();
        Require(canvas.Children.OfType<System.Windows.Shapes.Line>().All(line => line.Opacity == 0), "Connections can be hidden.");
        ToggleLinks();
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
                                Require(!bounds[i].IntersectsWith(bounds[j]), "Automatic topology boxes do not overlap: " + pattern + "/" + orientation + "/" + scale + "/" + size + " " + bounds[i] + " and " + bounds[j] + ".");
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
        workspace.PrepareTopologyCard();
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

    /// <summary>Turns Film Grain off without saving the choice and requires every tiled grain layer in
    /// the window to stop drawing, then turns it back on.</summary>
    private static void CheckFilmGrainOff(FrameworkElement root, string view)
    {
        var grain = typeof(KillerScan.App).Assembly.GetType("KillerScan.Services.FilmGrain", true)!;
        var enabled = grain.GetProperty("Enabled", Static)!;
        var publish = grain.GetMethod("Publish", Static)!;
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        // IsVisible is false throughout an offscreen window that is never shown, so walk the
        // layer's own Visibility and Opacity up to the root instead.
        bool Drawn(Border border)
        {
            for (DependencyObject? node = border; node != null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement element && (element.Visibility != Visibility.Visible || element.Opacity <= 0))
                    return false;
            return true;
        }
        try
        {
            enabled.SetValue(null, false);
            publish.Invoke(null, null);
            Render(root, 1200, 780);
            var drawn = Descendants(root).OfType<Border>()
                .Where(border => border.Background is ImageBrush { TileMode: TileMode.Tile } && Drawn(border))
                .Select(border => string.IsNullOrEmpty(border.Name) ? border.TemplatedParent?.GetType().Name ?? "Border" : border.Name)
                .ToList();
            Require(drawn.Count == 0, view + ": with Film Grain off no grain layer is drawn (" + string.Join(", ", drawn) + ").");
        }
        finally
        {
            enabled.SetValue(null, true);
            publish.Invoke(null, null);
        }
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
