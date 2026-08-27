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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
            uint flags, uint timeout, out IntPtr result);
        private static readonly IntPtr HwndBroadcast = new(0xffff);
        private const uint WmSettingChange = 0x001A;
        private const uint SmtoAbortIfHung = 0x0002;

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

            // Elevated half of the dual-install repair below: removes the machine-wide copy.
            if (e.Args.Length > 0 &&
                string.Equals(e.Args[0], "/remove-machine-conflict", StringComparison.OrdinalIgnoreCase))
            {
                RemoveMachineInstallConflict();
                Shutdown(0);
                return;
            }

            // Headless command line: /scan, /help, /version (Features/Cli/CliRunner.cs). Handled
            // before anything builds a window, so a CLI run never shows one and works while a GUI
            // instance is already open. Arguments carrying no recognized command fall through to
            // the normal launch below.
            if (Features.Cli.CliRunner.TryRunCli(e.Args, out int cliExit))
            {
                Shutdown(cliExit);
                return;
            }

            // Demo / screenshot mode: KillerScan.exe --demo fills the grid with fabricated
            // devices so screenshots carry no real network data.
            Services.DemoData.Enabled = Array.Exists(e.Args, a =>
                string.Equals(a, "--demo", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a, "/demo",  StringComparison.OrdinalIgnoreCase));

            OfferInstallConflictRepair();

            // Restore the saved theme + locale before the window is built (no first-paint flash).
            Services.ThemeManager.Initialize();
            Services.LocaleManager.Initialize();

            ShutdownMode = ShutdownMode.OnLastWindowClose;
            new Shell.MainWindow().Show();
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

        /// <summary>Resource lookup WITH AN ENGLISH FALLBACK, for the install and uninstall
        /// prompts. Unlike the rest of the app these can run before LocaleManager.Initialize -
        /// /uninstall and /remove-machine-conflict handle their argument and exit long before a
        /// window exists - and at that point there is no dictionary to read. The fallback keeps
        /// those paths saying something rather than rendering a raw Str_ key, and once the app has
        /// started normally the translation is found and used.</summary>
        private static string L(string key, string fallback) =>
            Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>Repairs a machine that carries BOTH a per-user and a machine-wide install -
        /// the state where each Add/Remove Programs entry describes the other copy's version and
        /// launching gets whichever exe the shell resolves first. Detected at startup; offers to
        /// remove whichever copy is NOT running. Removing the machine copy needs elevation, so
        /// that path re-runs this exe with /remove-machine-conflict under UAC.</summary>
        private static void OfferInstallConflictRepair()
        {
            if (!File.Exists(InstallExe) || !File.Exists(MachineInstallExe)) return;
            string current = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            bool runningMachine = string.Equals(current, MachineInstallExe, StringComparison.OrdinalIgnoreCase);
            bool runningUser = string.Equals(current, InstallExe, StringComparison.OrdinalIgnoreCase);
            if (!runningMachine && !runningUser) return;

            // Two whole sentences rather than one with the scope substituted in: languages that
            // inflect the noun cannot take "per-user" or "all-users" as a drop-in word.
            string body = runningMachine
                ? L("Str_Conflict_RemoveUser", "KillerScan is installed twice. Remove the other per-user copy now?\n\nYour settings will not be removed.")
                : L("Str_Conflict_RemoveMachine", "KillerScan is installed twice. Remove the other all-users copy now?\n\nYour settings will not be removed.");
            if (MessageBox.Show(body,
                $"{AppName} {L("Str_Conflict_Title", "installation conflict")}",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            if (runningMachine) RemovePerUserInstall();
            else
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(current, "/remove-machine-conflict")
                    { UseShellExecute = true, Verb = "runas" });
                    p?.WaitForExit();
                }
                catch { /* declining UAC leaves both copies in place */ }
            }
        }

        private static void RemoveMachineInstallConflict()
        {
            RemoveFromPath(MachineInstallDir, EnvironmentVariableTarget.Machine);
            string common = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName);
            try { Registry.LocalMachine.DeleteSubKeyTree(@"Software\KillerScan", false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan", false); } catch { }
            try { if (Directory.Exists(common)) Directory.Delete(common, true); } catch { }
            try { if (Directory.Exists(MachineInstallDir)) Directory.Delete(MachineInstallDir, true); } catch { }
        }

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
            RemoveFromPath(InstallDir, EnvironmentVariableTarget.User);
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
                AddToPath(installDir, EnvironmentVariableTarget.Machine);

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
                    key.SetValue("Publisher",            "Steve the Killer");
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
                AddToPath(InstallDir, EnvironmentVariableTarget.User);

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
                    key.SetValue("Publisher",            "Steve the Killer");
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
                MessageBox.Show(string.Format(L("Str_Install_Failed", "Installation failed:\n{0}"), ex.Message), AppName,
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

        /// <summary>Add an install directory to PATH exactly once. The self-installer updates the
        /// current user; the elevated silent installer updates the machine, so a new terminal can
        /// invoke the app as simply "KillerScan" from any directory.</summary>
        private static void AddToPath(string directory, EnvironmentVariableTarget target)
        {
            string current = Environment.GetEnvironmentVariable("Path", target) ?? "";
            var entries = current.Split([';'], StringSplitOptions.RemoveEmptyEntries).ToList();
            if (!entries.Any(p => SamePath(p, directory)))
            {
                entries.Add(directory);
                Environment.SetEnvironmentVariable("Path", string.Join(";", entries), target);
                BroadcastEnvironmentChange();
            }
        }

        /// <summary>Remove every spelling of an install directory from PATH without disturbing
        /// entries belonging to other programs.</summary>
        private static void RemoveFromPath(string directory, EnvironmentVariableTarget target)
        {
            try
            {
                string current = Environment.GetEnvironmentVariable("Path", target) ?? "";
                var entries = current.Split([';'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => !SamePath(p, directory)).ToList();
                string updated = string.Join(";", entries);
                if (!string.Equals(current.TrimEnd(';'), updated, StringComparison.Ordinal))
                {
                    Environment.SetEnvironmentVariable("Path", updated, target);
                    BroadcastEnvironmentChange();
                }
            }
            catch { /* uninstall and install-scope migration are best-effort cleanup */ }
        }

        private static bool SamePath(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(Environment.ExpandEnvironmentVariables(left.Trim().Trim('"')))
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static void BroadcastEnvironmentChange()
        {
            try
            {
                SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment",
                    SmtoAbortIfHung, 2000, out _);
            }
            catch { /* a newly signed-in session still reads the persisted PATH */ }
        }

        // ============================================================
        // Uninstall
        // ============================================================

        private static bool RelaunchMachineUninstallElevatedIfNeeded(bool machine)
        {
            if (!machine) return false;
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                    return false;

                Process.Start(new ProcessStartInfo(
                    Process.GetCurrentProcess().MainModule!.FileName, "/uninstall")
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // UAC was declined. Leave the installation untouched.
            }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(L("Str_Uninstall_NoAdmin", "Uninstall could not request administrator access:\n{0}"), ex.Message),
                    AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            return true;
        }

        private static void Uninstall()
        {
            string currentExe = Process.GetCurrentProcess().MainModule!.FileName;
            bool machineInstall = string.Equals(currentExe, MachineInstallExe, StringComparison.OrdinalIgnoreCase);
            if (RelaunchMachineUninstallElevatedIfNeeded(machineInstall)) return;

            var confirm = new Controls.ConfirmDialog(
                L("Str_Uninstall_Confirm", "Uninstall KillerScan from this computer?"),
                string.Empty,
                L("Str_Uninstall_Title", "Uninstall"),
                L("Str_Btn_Cancel", "Cancel"));
            confirm.ShowDialog();
            if (!confirm.Confirmed) return;

            RemoveFromPath(machineInstall ? MachineInstallDir : InstallDir,
                machineInstall ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User);

            string startMenuDir = machineInstall
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), AppName)
                : StartMenuDir;
            string targetDir = machineInstall ? MachineInstallDir : InstallDir;
            try { File.Delete(Path.Combine(startMenuDir, $"{AppName}.lnk")); } catch { }
            try { Directory.Delete(startMenuDir, recursive: false); } catch { }
            if (!machineInstall) try { File.Delete(DesktopLnk); } catch { }

            var hive = machineInstall ? Registry.LocalMachine : Registry.CurrentUser;
            try { hive.DeleteSubKeyTree(@"Software\KillerScan", throwOnMissingSubKey: false); } catch { }
            try { hive.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan",
                throwOnMissingSubKey: false); } catch { }

            SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);

            // Self-delete: deferred via cmd batch so the EXE can exit first
            string bat = Path.Combine(Path.GetTempPath(), "killerscan_uninstall.bat");
            File.WriteAllText(bat,
                "@echo off\r\n" +
                "ping -n 3 127.0.0.1 >nul\r\n" +
                $"rmdir /s /q \"{targetDir}\"\r\n" +
                "del \"%~f0\"\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                WindowStyle     = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            });

        }
    }
}
