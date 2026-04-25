using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace KillerScan
{
    public partial class App : Application
    {
        // ============================================================
        // Paths
        // ============================================================

        private static readonly string AppName    = "KillerScan";
        private static readonly string ExeName    = "KillerScan.exe";
        private static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", AppName);
        private static readonly string InstallExe = Path.Combine(InstallDir, ExeName);

        private static readonly string StartMenuDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName);
        private static readonly string StartMenuLnk = Path.Combine(StartMenuDir, $"{AppName}.lnk");
        private static readonly string DesktopLnk   = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk");

        // ============================================================
        // Shell interop
        // ============================================================

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(uint wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST       = 0x0000;

        // ============================================================
        // Startup
        // ============================================================

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Prevent WPF from auto-shutting down when the launcher dialog closes.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Handle uninstall flag (called by Add/Remove Programs)
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Uninstall();
                Shutdown();
                return;
            }

            string currentExe = Process.GetCurrentProcess().MainModule!.FileName;
            bool isInstalledCopy = string.Equals(
                currentExe, InstallExe, StringComparison.OrdinalIgnoreCase);

            // Show the Install / Run launcher only when running from outside the install location
            if (!isInstalledCopy)
            {
                bool alreadyInstalled = IsInstalled();
                var (cancelled, doInstall, wantDesktop) = ShowLauncher(alreadyInstalled);

                if (cancelled)
                {
                    Shutdown();
                    return;
                }

                if (doInstall)
                {
                    DoInstall(wantDesktop);

                    MessageBox.Show(
                        "KillerScan has been installed.",
                        AppName, MessageBoxButton.OK, MessageBoxImage.Information);

                    // Relaunch from the installed location then exit this process
                    Process.Start(new ProcessStartInfo(InstallExe));
                    Shutdown();
                    return;
                }
                // else: user chose Run -- fall through and launch normally
            }

            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new MainWindow().Show();
        }

        // ============================================================
        // Registry helpers
        // ============================================================

        private static bool IsInstalled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\KillerScan");
            if (key is null) return false;
            return key.GetValue("Installed") is int i && i == 1;
        }

        // ============================================================
        // Launcher dialog
        // ============================================================

        private static Style MakeLauncherButtonStyle(
            SolidColorBrush normal, SolidColorBrush hover, SolidColorBrush fg)
        {
            var template = new ControlTemplate(typeof(Button));
            var border   = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            border.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.MarginProperty,              new Thickness(0, 6, 0, 6));
            border.AppendChild(cp);
            template.VisualTree = border;

            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Button.BackgroundProperty,      normal));
            style.Setters.Add(new Setter(Button.ForegroundProperty,      fg));
            style.Setters.Add(new Setter(Button.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Button.TemplateProperty,        template));
            style.Setters.Add(new Setter(Button.CursorProperty,          Cursors.Hand));

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Button.BackgroundProperty, hover));
            style.Triggers.Add(hoverTrigger);

            return style;
        }

        private static (bool cancelled, bool install, bool desktop) ShowLauncher(bool alreadyInstalled)
        {
            bool cancelled = true;
            bool install   = false;
            bool desktop   = true;

            var bg      = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x1c));
            var dimBg   = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x23));
            var accent  = new SolidColorBrush(Color.FromRgb(0x1e, 0xa5, 0x4c));  // #1ea54c
            var accentH = new SolidColorBrush(Color.FromRgb(0x36, 0xad, 0x6a));  // #36AD6A hover
            var dimText = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));

            var win = new Window
            {
                Title                 = AppName,
                Width                 = 400,
                Height                = 280,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode            = ResizeMode.NoResize,
                WindowStyle           = WindowStyle.None,
                Background            = bg
            };

            // Root grid: title bar row + content row
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Title bar
            var titleBar = new DockPanel { Background = dimBg };
            Grid.SetRow(titleBar, 0);
            titleBar.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed) win.DragMove();
            };

            // Close button
            var closeBtnTemplate = new ControlTemplate(typeof(Button));
            var closeBorder = new FrameworkElementFactory(typeof(Border));
            closeBorder.SetBinding(Border.BackgroundProperty,
                new System.Windows.Data.Binding("Background")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.TemplatedParent)
                });
            var closeContent = new FrameworkElementFactory(typeof(ContentPresenter));
            closeContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            closeContent.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
            closeBorder.AppendChild(closeContent);
            closeBtnTemplate.VisualTree = closeBorder;

            var redHover = new SolidColorBrush(Color.FromRgb(0xc4, 0x2b, 0x1c));
            var closeBtnStyle = new Style(typeof(Button));
            closeBtnStyle.Setters.Add(new Setter(Button.BackgroundProperty,      Brushes.Transparent));
            closeBtnStyle.Setters.Add(new Setter(Button.ForegroundProperty,      dimText));
            closeBtnStyle.Setters.Add(new Setter(Button.TemplateProperty,        closeBtnTemplate));
            var closeHover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            closeHover.Setters.Add(new Setter(Button.BackgroundProperty, redHover));
            closeHover.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            closeBtnStyle.Triggers.Add(closeHover);

            var closeBtn = new Button
            {
                Content                  = "",
                FontFamily               = new FontFamily("Segoe MDL2 Assets"),
                FontSize                 = 11,
                Width                    = 46,
                BorderThickness          = new Thickness(0),
                VerticalAlignment        = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor                   = Cursors.Arrow,
                Style                    = closeBtnStyle
            };
            closeBtn.Click += (_, _) => win.Close();
            DockPanel.SetDock(closeBtn, Dock.Right);
            titleBar.Children.Add(closeBtn);

            titleBar.Children.Add(new TextBlock
            {
                Text              = AppName,
                Foreground        = dimText,
                FontSize          = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(12, 0, 0, 0)
            });

            root.Children.Add(titleBar);

            // Content
            var content = new StackPanel { Margin = new Thickness(36, 22, 36, 28) };
            Grid.SetRow(content, 1);

            content.Children.Add(new TextBlock
            {
                Text       = AppName,
                FontSize   = 26,
                FontWeight = FontWeights.Bold,
                Foreground = accent
            });

            var version = Assembly.GetExecutingAssembly().GetName().Version;
            content.Children.Add(new TextBlock
            {
                Text       = $"Version {version?.ToString(3)}",
                Foreground = dimText,
                FontSize   = 12,
                Margin     = new Thickness(0, 2, 0, 18)
            });

            content.Children.Add(new TextBlock
            {
                Text         = alreadyInstalled
                    ? "A newer version is available. Install it or run without updating."
                    : "Install KillerScan on this computer, or run it without installing.",
                Foreground   = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 16)
            });

            var desktopChk = new CheckBox
            {
                IsChecked = true,
                Margin    = new Thickness(0, 0, 0, 22),
                Content   = new TextBlock { Text = "Create desktop shortcut", Foreground = Brushes.White }
            };
            content.Children.Add(desktopChk);

            var btnRow = new StackPanel
            {
                Orientation         = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var runBtn = new Button
            {
                Content = "Run",
                Width   = 88,
                Margin  = new Thickness(0, 0, 8, 0),
                Style   = MakeLauncherButtonStyle(
                    normal: new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)),
                    hover:  new SolidColorBrush(Color.FromRgb(0x16, 0x63, 0x34)),
                    fg:     Brushes.White)
            };
            var installBtn = new Button
            {
                Content    = alreadyInstalled ? "Update" : "Install",
                Width      = 110,
                Style      = MakeLauncherButtonStyle(
                    normal: accent,
                    hover:  accentH,
                    fg:     new SolidColorBrush(Color.FromRgb(0x0a, 0x0a, 0x0a))),
                FontWeight = FontWeights.SemiBold
            };

            runBtn.Click += (_, _) =>
            {
                cancelled = false; install = false;
                win.Close();
            };
            installBtn.Click += (_, _) =>
            {
                cancelled = false; install = true;
                desktop = desktopChk.IsChecked == true;
                win.Close();
            };

            btnRow.Children.Add(runBtn);
            btnRow.Children.Add(installBtn);
            content.Children.Add(btnRow);

            root.Children.Add(content);
            win.Content = root;
            win.ShowDialog();

            return (cancelled, install, desktop);
        }

        // ============================================================
        // Installation
        // ============================================================

        private static void DoInstall(bool wantDesktop)
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                string src = Process.GetCurrentProcess().MainModule!.FileName;
                File.Copy(src, InstallExe, overwrite: true);

                Directory.CreateDirectory(StartMenuDir);
                CreateShortcut(StartMenuLnk, InstallExe);
                if (wantDesktop)
                    CreateShortcut(DesktopLnk, InstallExe);

                using (var key = Registry.CurrentUser.CreateSubKey(@"Software\KillerScan"))
                {
                    key.SetValue("Installed",   1);
                    key.SetValue("InstallPath", InstallExe);
                    key.SetValue("Version",
                        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");
                }

                using (var key = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan"))
                {
                    key.SetValue("DisplayName",          AppName);
                    key.SetValue("DisplayVersion",
                        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "");
                    key.SetValue("Publisher",            "Steve / thekiller.net");
                    key.SetValue("InstallLocation",      InstallDir);
                    key.SetValue("DisplayIcon",          $"{InstallExe},0");
                    key.SetValue("UninstallString",      $"\"{InstallExe}\" /uninstall");
                    key.SetValue("QuietUninstallString", $"\"{InstallExe}\" /uninstall");
                    key.SetValue("NoModify",             1);
                    key.SetValue("NoRepair",             1);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Installation failed:\n{ex.Message}", AppName,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void CreateShortcut(string lnkPath, string targetPath)
        {
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType is null) return;
                dynamic shell    = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                shortcut.TargetPath       = targetPath;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
                shortcut.Save();
            }
            catch { /* best-effort */ }
        }

        // ============================================================
        // Uninstall
        // ============================================================

        private static void Uninstall()
        {
            var res = MessageBox.Show(
                "Uninstall KillerScan from this computer?",
                $"{AppName} Uninstall",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            try { File.Delete(StartMenuLnk); } catch { }
            try { Directory.Delete(StartMenuDir, recursive: false); } catch { }
            try { File.Delete(DesktopLnk); } catch { }

            try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\KillerScan"); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan"); } catch { }

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            // Self-delete: deferred via cmd batch so the EXE can exit first
            string bat = Path.Combine(Path.GetTempPath(), "killerscan_uninstall.bat");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                "ping -n 3 127.0.0.1 >nul\r\n" +
                $"rmdir /s /q \"{InstallDir}\"\r\n" +
                "del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                WindowStyle     = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            });

            MessageBox.Show("KillerScan has been uninstalled.", AppName,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
