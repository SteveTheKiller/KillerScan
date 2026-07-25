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

        // Machine-wide ("all users") install target. Used by the /silent path that winget, choco
        // and RMM call, and by the "Install for all users" checkbox in the confirm dialog.
        private static readonly string MachineInstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
        private static readonly string MachineInstallExe = Path.Combine(MachineInstallDir, ExeName);

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

            // Force CPU (software) rendering. WPF composites through the GPU by default, and
            // console-session screen scrapers (ScreenConnect, LiveConnect, VNC, TeamViewer) can't
            // capture GPU-composited surfaces, so the window renders black in the remote view while
            // looking fine on the physical machine. Those tools aren't Terminal Services sessions,
            // so SM_REMOTESESSION is false and WPF never auto-falls-back on its own. Software
            // rendering lands every surface in the normal desktop framebuffer they can capture.
            // Cost is negligible for an app this light (static shadows/grain, short opacity fades).
            System.Windows.Media.RenderOptions.ProcessRenderMode =
                System.Windows.Interop.RenderMode.SoftwareOnly;

            // Silent install: KillerScan.exe /silent
            // Installs machine-wide to Program Files, no UI. Used by winget/choco/RMM.
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/silent", StringComparison.OrdinalIgnoreCase))
            {
                DoSilentInstall();
                Shutdown(0);
                return;
            }

            // Handle uninstall flag (called by Add/Remove Programs)
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
            {
                Uninstall();
                Shutdown();
                return;
            }

            // Demo / screenshot mode: KillerScan.exe --demo fills the grid with fabricated
            // devices so screenshots carry no real network data.
            KillerScan.MainWindow.DemoMode = Array.Exists(e.Args, a =>
                string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/demo",  StringComparison.OrdinalIgnoreCase));

            // Restore the saved theme + locale before the window is built (no first-paint flash).
            Services.ThemeManager.Initialize();
            Services.LocaleManager.Initialize();

            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new MainWindow().Show();
        }

        // ============================================================
        // Public surface used by MainWindow (portable badge / install)
        // ============================================================

        /// <summary>
        /// True when running from outside EITHER installed location (i.e. portable mode).
        /// Must check the machine-wide path as well as the per-user one: a /silent install from
        /// winget, choco or an RMM lands in Program Files, and comparing only against the
        /// per-user path made those properly-installed copies show the PORTABLE badge and the
        /// Install button.
        /// </summary>
        internal static bool IsPortable()
        {
            string currentExe = Process.GetCurrentProcess().MainModule!.FileName;
            return !string.Equals(currentExe, InstallExe, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(currentExe, MachineInstallExe, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when a machine-wide copy is already present on disk.</summary>
        internal static bool MachineInstallExists() => File.Exists(MachineInstallExe);

        /// <summary>
        /// Installs KillerScan, then relaunches from the installed location.
        /// For an all-users install the app re-runs itself elevated with /silent - the same
        /// machine-wide path winget and choco already use - so UAC only appears when the user
        /// actually ticked the box. Returns false if that elevation was declined or failed,
        /// leaving the app running as it was.
        /// </summary>
        internal static bool InstallAndRelaunch(bool wantDesktop, bool allUsers)
        {
            if (allUsers)
            {
                if (!RunElevatedSilentInstall()) return false;

                // One install only: drop the per-user copy now that a machine-wide one exists.
                // Done from THIS (unelevated) process so it removes the invoking user's profile
                // copy, which an elevated process might not resolve to.
                RemovePerUserInstall();

                Process.Start(new ProcessStartInfo(MachineInstallExe));
                Application.Current.Shutdown();
                return true;
            }

            DoInstall(wantDesktop);
            Process.Start(new ProcessStartInfo(InstallExe));
            Application.Current.Shutdown();
            return true;
        }

        /// <summary>Re-run this exe elevated with /silent and wait for it to finish.</summary>
        private static bool RunElevatedSilentInstall()
        {
            try
            {
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule!.FileName, "/silent")
                {
                    UseShellExecute = true,
                    Verb = "runas",          // triggers the UAC prompt
                };
                using var p = Process.Start(psi);
                p?.WaitForExit();
                return p is not null && p.ExitCode == 0 && File.Exists(MachineInstallExe);
            }
            catch
            {
                // Declining the UAC prompt throws Win32Exception 1223 (ERROR_CANCELLED).
                return false;
            }
        }

        /// <summary>Remove a per-user install: files, shortcuts, and its HKCU marker.
        /// Settings under Software\KillerScan\Settings are deliberately left alone so theme,
        /// accent, locale and window placement survive the move to a machine-wide install.</summary>
        private static void RemovePerUserInstall()
        {
            try { if (File.Exists(StartMenuLnk)) File.Delete(StartMenuLnk); } catch { }
            try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true); } catch { }
            try { if (File.Exists(DesktopLnk)) File.Delete(DesktopLnk); } catch { }
            try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, true); } catch { }
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KillerScan", writable: true);
                key?.DeleteValue("Installed", throwOnMissingValue: false);
                key?.DeleteValue("InstallPath", throwOnMissingValue: false);
            }
            catch { }
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan", throwOnMissingSubKey: false);
            }
            catch { }
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
        // Preference store  (Software\KillerScan\Settings)
        // Mirrors KillerPDF: simple per-user string settings, used by
        // ThemeManager / LocaleManager to persist theme, accent, and locale.
        // ============================================================

        internal static string? GetSetting(string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KillerScan\Settings");
                return key?.GetValue(name) as string;
            }
            catch { return null; }
        }

        internal static void SetSetting(string name, string value)
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"Software\KillerScan\Settings");
                key?.SetValue(name, value);
            }
            catch { /* best-effort */ }
        }

        internal static void RemoveSetting(string name)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\KillerScan\Settings", writable: true);
                key?.DeleteValue(name, throwOnMissingValue: false);
            }
            catch { /* best-effort */ }
        }

        // ============================================================
        // Installation
        // ============================================================

        // ============================================================
        // Silent (machine-wide) install -- used by winget / choco / RMM
        // ============================================================

        private static void DoSilentInstall()
        {
            try
            {
                string installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
                string installExe = Path.Combine(installDir, ExeName);
                string startMenuDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
                string startMenuLnk = Path.Combine(startMenuDir, $"{AppName}.lnk");

                Directory.CreateDirectory(installDir);
                string src = Process.GetCurrentProcess().MainModule!.FileName;
                File.Copy(src, installExe, overwrite: true);

                Directory.CreateDirectory(startMenuDir);
                CreateShortcut(startMenuLnk, installExe);

                string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "";

                using (var key = Registry.LocalMachine.CreateSubKey(@"Software\KillerScan"))
                {
                    key.SetValue("Installed",   1);
                    key.SetValue("InstallPath", installExe);
                    key.SetValue("Version",     version);
                }

                using (var key = Registry.LocalMachine.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan"))
                {
                    key.SetValue("DisplayName",          AppName);
                    key.SetValue("DisplayVersion",       version);
                    key.SetValue("Publisher",            "Steve / thekiller.net");
                    key.SetValue("InstallLocation",      installDir);
                    key.SetValue("DisplayIcon",          $"{installExe},0");
                    key.SetValue("UninstallString",      $"\"{installExe}\" /uninstall");
                    key.SetValue("QuietUninstallString", $"\"{installExe}\" /uninstall");
                    key.SetValue("NoModify",             1);
                    key.SetValue("NoRepair",             1);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Silent install failed: {ex.Message}");
                Environment.Exit(1);
            }
        }

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
