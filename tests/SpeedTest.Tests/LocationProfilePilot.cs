using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;
using KillerScan.Controls;
using KillerScan.Engine;
using KillerScan.Services;
using KillerScan.Shell;

// Real GUI handlers and real files, across two fresh processes. Never invokes Scan/Run.
internal static class LocationProfilePilot
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string ProfileName = "Dnevni pregled";
    private const string SiteA = "Preizkus A";
    private const string SiteB = "Preizkus B";
    private const string RenamedB = "Preizkus B preimenovan";
    private static int _checks;
    [DllImport("advapi32.dll")] private static extern int RegOverridePredefKey(IntPtr key, IntPtr replacement);
    private static object? Call(object target, string name, params object?[] arguments) => target.GetType().GetMethod(name, Instance)!.Invoke(target, arguments);
    private static void Require(bool condition, string message) { _checks++; if (!condition) throw new InvalidOperationException(message); }

    internal static async Task RunIsolated()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerScan-location-profiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "pilot-root.txt"), "Fabricated location/profile GUI test; no scans.");
        foreach (string phase in new[] { "create", "restart" })
        {
            using var process = Process.Start(new ProcessStartInfo(typeof(LocationProfilePilot).Assembly.Location,
                "--location-profiles-phase " + phase + " \"" + root + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!await Task.Run(() => process.WaitForExit(120000))) { process.Kill(); throw new InvalidOperationException("Location/profile GUI test timed out."); }
            Console.Write(await output);
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        }
        Console.WriteLine("PASS: two locations and same-name profiles survive a fresh application process without cross-location changes or scans.");
        Console.WriteLine("Location/profile pilot artifacts: " + root);
    }

    internal static void RunPhase(string phase, string root)
    {
        root = Path.GetFullPath(root);
        Require(root.StartsWith(Path.Combine(Path.GetTempPath(), "KillerScan-location-profiles-"), StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(root, "pilot-root.txt")), "The GUI test only accepts its own temporary fixture root.");
        Require(phase is "create" or "restart", "Known pilot phase.");
        Environment.SetEnvironmentVariable("KILLERSCAN_DATA_ROOT", Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable("KS_LOCATION_ID", null);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            string scratch = "Software\\KillerScan-LocationProfiles-" + Guid.NewGuid().ToString("N");
            var hkcu = new IntPtr(unchecked((int)0x80000001));
            using var registry = Registry.CurrentUser.CreateSubKey(scratch);
            bool overridden = false;
            try
            {
                Require(RegOverridePredefKey(hkcu, registry.Handle.DangerousGetHandle()) == 0, "Registry settings are isolated.");
                overridden = true;
                RunUi(phase, root);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                if (overridden) RegOverridePredefKey(hkcu, IntPtr.Zero);
                registry.Close(); Registry.CurrentUser.DeleteSubKeyTree(scratch, false);
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Require(thread.Join(TimeSpan.FromSeconds(100)), "GUI phase finishes within the bounded timeout.");
        if (failure != null) throw new InvalidOperationException("Location/profile " + phase + " failed.", failure);
        Console.WriteLine("PASS: " + phase + " - " + _checks + " GUI, persistence and locale assertions.");
    }

    private static void RunUi(string phase, string root)
    {
        var app = ReleaseUiTests.CreateApplication();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DemoData.Enabled = false;
        ThemeManager.Initialize(); LocaleManager.Initialize();
        ThemeManager.Apply(Theme.Black);
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            // Keep the modal test windows off the user's desktop too.
            if (sender is Window testWindow) { testWindow.Left = -30000; testWindow.Top = -30000; }
        }));
        if (phase == "create") LocaleManager.Apply(Locale.EnUS);
        else Require(LocaleManager.Current == Locale.EnUS, "The selected UI language survives a fresh process.");
        var window = new MainWindow("192.0.2.10", startScanOnLoad: false);
        window.ShowInTaskbar = false; window.ShowActivated = false;
        window.Left = -30000; window.Top = -30000;
        window.Show(); window.Hide(); // WPF requires an owner to have been shown once.
        var workspace = (ScanWorkspace)typeof(MainWindow).GetField("_scanWorkspace", Instance)!.GetValue(window)!;
        void Choose(string id) => Require((bool)Call(window, "ChangeLocation", id)!, "The location changes through the shell handler.");
        void SaveProfile() => Answer(window, () => Call(window, "SaveProfile_Click", window.FindName("SaveProfileButton"), new RoutedEventArgs()), ProfileName);
        ScanProfile CheckProfile(string id, string disabled)
        {
            Call(window, "ProfilesButton_Click", window, new RoutedEventArgs());
            var list = (ListBox)window.FindName("ProfilesList");
            Require(list.Items.Count == 1 && ScanProfiles.Items.Count == 1, "Only this location's profile is shown.");
            var profile = (ScanProfile)list.Items[0];
            Require(profile.LocationId == id && profile.Name == ProfileName && profile.Options.DisabledFeatures == disabled,
                "Same-name profiles retain their own location and options.");
            Call(window, "ProfileLoad_Click", new MenuItem { DataContext = profile }, new RoutedEventArgs());
            Require(workspace.Targets == "192.0.2.10" && workspace.Options.DisabledFeatures == disabled && !workspace.IsScanning,
                "Loading a profile restores choices without starting a scan.");
            return profile;
        }

        if (phase == "create")
        {
            Require(Locations.Catalog.Items.Count == 1, "The fixture starts with an empty legacy group.");
            Answer(window, () => Call(window, "EditLocation", true), SiteA);
            string a = Locations.Current.Id;
            workspace.Targets = "192.0.2.10"; workspace.Session.Options = new ScanOptions { Mdns = false };
            SaveProfile(); var profileA = CheckProfile(a, "mdns");
            Capture(window, workspace, Path.Combine(root, "A-created.png"));
            Answer(window, () => Call(window, "EditLocation", true), SiteB);
            string b = Locations.Current.Id;
            Require(a != b && ScanProfiles.Items.Count == 0 && workspace.Targets == "", "A new location has its own empty profile list and cleared target.");
            workspace.Targets = "192.0.2.10"; workspace.Session.Options = new ScanOptions { Http = false };
            SaveProfile(); CheckProfile(b, "http");
            Call(window, "ProfileLoad_Click", new MenuItem { DataContext = profileA }, new RoutedEventArgs());
            workspace.RunProfile(profileA);
            Require(workspace.Options.DisabledFeatures == "http" && !workspace.IsScanning && workspace.Session.Devices.Count == 0,
                "A stale profile from A cannot load or run in B.");
            using (Locations.BeginRun()) Require(!(bool)Call(window, "ChangeLocation", a)!, "Changing location is blocked during a run.");
            Answer(window, () => Call(window, "EditLocation", false), RenamedB);
            Require(Locations.Current.Id == b && Locations.Current.Name == RenamedB, "Renaming preserves the location ID.");
            Answer(window, () => Call(window, "EditLocation", true), "", cancel: true);
            Require(Locations.Catalog.Items.Count == 3 && Locations.Current.Id == b, "Cancel does not create or select a location.");
            Choose(a); CheckProfile(a, "mdns");
            Choose(b); CheckProfile(b, "http");
            CheckLocales(app, window, workspace, root);
            Call(window, "Lang_Checked", new RadioButton { Tag = Locale.EnUS.ToString() }, new RoutedEventArgs());
            Capture(window, workspace, Path.Combine(root, "B-before-restart.png"));
        }
        else
        {
            Require(Locations.Current.Name == RenamedB && Locations.Catalog.Items.Count == 3, "The last selected location and its renamed identity survive restart.");
            var a = Locations.Catalog.Items.Single(item => item.Name == SiteA);
            string b = Locations.Current.Id;
            var profileB = CheckProfile(b, "http");
            Call(window, "ProfileDelete_Click", new MenuItem { DataContext = profileB }, new RoutedEventArgs());
            Require(ScanProfiles.Items.Count == 0, "Deleting B's profile changes B only.");
            Choose(a.Id); CheckProfile(a.Id, "mdns");
            Capture(window, workspace, Path.Combine(root, "A-after-restart.png"));
            Choose(b); Require(ScanProfiles.Items.Count == 0, "Deleting B's profile persists after switching back.");
            workspace.Targets = "192.0.2.10"; workspace.Session.Options = new ScanOptions { Http = false };
            SaveProfile(); CheckProfile(b, "http");
            Capture(window, workspace, Path.Combine(root, "B-after-restart.png"));
        }
        Require(!workspace.IsScanning && workspace.Session.Devices.Count == 0, "The complete pilot starts no scans and creates no device results.");
        typeof(MainWindow).GetField("_closeFaded", Instance)!.SetValue(window, true);
        window.Close();
    }

    private static void Answer(MainWindow owner, Action action, string value, bool cancel = false)
    {
        bool answered = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.OfType<InputDialog>().FirstOrDefault(item => item.Owner == owner && item.IsVisible);
            if (dialog == null) return;
            timer.Stop(); answered = true;
            ((TextBox)dialog.FindName("ValueBox")).Text = value;
            ((Button)dialog.FindName(cancel ? "CancelButton" : "OkButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        timer.Start();
        try { action(); }
        finally { timer.Stop(); }
        Require(answered, "The real create/rename/save input dialog is answered through its button.");
    }

    private static void CheckLocales(Application app, MainWindow window, ScanWorkspace workspace, string root)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string source = Path.GetDirectoryName(typeof(LocationProfilePilot).Assembly.Location)!;
        while (!File.Exists(Path.Combine(source, "KillerScan.csproj"))) source = Directory.GetParent(source)!.FullName;
        var required = XDocument.Load(Path.Combine(source, "Strings", "en-US.xaml")).Root!.Elements()
            .Select(item => (string?)item.Attribute(x + "Key"))
            .Where(key => key != null && (key.StartsWith("Str_Location_") || key.StartsWith("Str_Profiles_"))).Cast<string>().ToArray();
        foreach (Locale locale in Enum.GetValues(typeof(Locale)))
        {
            Call(window, "Lang_Checked", new RadioButton { Tag = locale.ToString() }, new RoutedEventArgs());
            var own = app.Resources.MergedDictionaries[locale == Locale.EnUS ? 1 : 2];
            foreach (string key in required)
                Require(own.Contains(key) && own[key] is string value && !string.IsNullOrWhiteSpace(value), locale + ": own translation, without English fallback: " + key);
            string title = (string)app.FindResource("Str_Location_Title");
            Require(((TextBlock)window.FindName("SubtitleText")).Text == title + ": " + Locations.Current.Name, locale + ": location title updates immediately.");
            Require(Equals(((Button)window.FindName("SaveProfileButton")).Content, app.FindResource("Str_Profiles_Save")), locale + ": profile button updates immediately.");
            var picker = (Button)typeof(MainWindow).GetField("_locationButton", Instance)!.GetValue(window)!;
            picker.ContextMenu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            foreach (string key in new[] { "Str_Location_New", "Str_Location_Rename", "Str_Location_Legacy" })
                Require(picker.ContextMenu.Items.OfType<MenuItem>().Any(item => Equals(item.Header, app.FindResource(key))), locale + ": picker menu uses the current translation: " + key);
            Require(Locations.ErrorText(new LocationNameException("technical", "Str_Location_InvalidName")) == (string)app.FindResource("Str_Location_InvalidName"), locale + ": invalid-name error is localized.");
            Require(Locations.ErrorText(new LocationNameException("technical", "Str_Location_DuplicateName")) == (string)app.FindResource("Str_Location_DuplicateName"), locale + ": duplicate-name error is localized.");
            Require(Locations.ErrorText(new IOException("technical")) == (string)app.FindResource("Str_Location_DataError"), locale + ": storage error is localized.");
            Require(Locations.ErrorText(new ArgumentException("technical")) == (string)app.FindResource("Str_Location_SelectionError"), locale + ": selection error is localized.");
            Capture(window, workspace, Path.Combine(root, "locale-" + locale + ".png"));
            ThemeManager.Apply(Theme.SE98);
            foreach (string headingKey in new[] { "Str_Location_New", "Str_Profiles_Save" })
            {
                string heading = (string)app.FindResource(headingKey);
                var dialog = new InputDialog(heading, "", (string)app.FindResource("Str_Rename_Label"), "Fixture",
                    (string)app.FindResource("Str_Rename_Save"), (string)app.FindResource("Str_Btn_Cancel"));
                var content = (FrameworkElement)dialog.Content;
                content.Opacity = 1; content.Measure(new Size(360, double.PositiveInfinity));
                content.Arrange(new Rect(new Point(), content.DesiredSize)); content.UpdateLayout();
                Require(((TextBlock)dialog.FindName("PlainTitleText")).Text == heading, locale + ": classic dialog title describes its location/profile action.");
                if (locale is Locale.EnUS or Locale.Ja or Locale.De)
                    SaveImage(content, (int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), Path.Combine(root, "dialog-" + locale + "-" + headingKey + ".png"));
                dialog.Close();
            }
            ThemeManager.Apply(Theme.Black);
        }
        Require(Enum.GetValues(typeof(Locale)).Length == 19, "All 19 supported languages are checked.");
    }

    private static void Capture(MainWindow window, ScanWorkspace workspace, string path)
    {
        // Network inventory labels are replaced before rendering; artifacts contain only fabricated data.
        Call(workspace, "ShowNetworkInfo", new LocalNet { LocalIp = "192.0.2.20", Gateway = "192.0.2.1", Dns = "192.0.2.53", InterfaceLabel = "Ethernet", AdapterName = "Fixture adapter" });
        Call(window, "SetSidebarSection", "profiles", false);
        Call(window, "OpenSidebar"); Call(window, "ApplySidebarState", false);
        var profiles = (ListBox)window.FindName("ProfilesList");
        profiles.BeginAnimation(UIElement.OpacityProperty, null); profiles.Opacity = 1;
        var root = (FrameworkElement)window.Content;
        root.BeginAnimation(UIElement.OpacityProperty, null); root.Opacity = 1;
        var grid = (UIElement)window.FindName("RootGrid"); grid.BeginAnimation(UIElement.OpacityProperty, null); grid.Opacity = 1;
        root.Measure(new Size(1200, 780)); root.Arrange(new Rect(0, 0, 1200, 780)); root.UpdateLayout();
        SaveImage(root, 1200, 780, path);
    }

    private static void SaveImage(FrameworkElement root, int width, int height, string path)
    {
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
}
