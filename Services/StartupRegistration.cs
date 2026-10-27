#nullable enable
using System;
using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

// ============================================================
// START WITH WINDOWS
//
// One value per app under the per-user Run key, named after the app, holding the quoted path of
// the running exe plus a start argument. Off until the user turns it on from the About card, and
// nothing is written until then. KillerShot starts with --tray, which opens no window; the other
// apps start with --minimized, which opens the main window minimized on the taskbar.
//
// ---- Wiring it up -------------------------------------------------------------------------
//   1. Copy this file unchanged. The namespace is shared on purpose, like InstallerExitCodes.
//   2. About card: a checkbox under "Check for updates on startup" that calls Set and shows
//      IsEnabled when the card opens.
//   3. App startup (a normal launch, not /silent, /uninstall or --cli): call Refresh, so an
//      entry written by a portable copy follows the app once it is installed or moved.
//   4. If HasMinimizedArgument(e.Args), call StartMinimized(window) after the window is built
//      and before Show.
//   5. Uninstall: call Remove.
// ============================================================
namespace KillerFamily.Deployment
{
    internal static class StartupRegistration
    {
        /// <summary>Start argument that opens the main window minimized on the taskbar.</summary>
        internal const string MinimizedArgument = "--minimized";

        /// <summary>The per-user Run key. A field so the family contract checks can point it at a
        /// scratch key instead of the real one.</summary>
        internal static string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>True when the app has a Run value.</summary>
        internal static bool IsEnabled(string appName)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(appName) is string value && value.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>Writes or removes the app's Run value. False when the registry refused.</summary>
        internal static bool Set(string appName, bool enabled, string arguments = MinimizedArgument)
        {
            try
            {
                if (!enabled)
                {
                    using var existing = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                    existing?.DeleteValue(appName, throwOnMissingValue: false);
                    return true;
                }
                string exe = RunningExe();
                if (exe.Length == 0) return false;
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                key.SetValue(appName, Command(exe, arguments), RegistryValueKind.String);
                return true;
            }
            catch { return false; }
        }

        /// <summary>When the app starts with Windows, points the Run value at the exe that is
        /// running now, so a value written by a portable copy follows an install or a move.</summary>
        internal static void Refresh(string appName, string arguments = MinimizedArgument)
        {
            try
            {
                string exe = RunningExe();
                if (exe.Length == 0) return;
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key?.GetValue(appName) is not string current || current.Length == 0) return;
                string wanted = Command(exe, arguments);
                if (!string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(appName, wanted, RegistryValueKind.String);
            }
            catch { /* best-effort: the old value still starts something or nothing */ }
        }

        /// <summary>Removes the app's Run value. Called by the uninstaller.</summary>
        internal static void Remove(string appName) => Set(appName, false);

        /// <summary>The Run value's command line.</summary>
        internal static string Command(string exe, string arguments)
            => string.IsNullOrEmpty(arguments) ? "\"" + exe + "\"" : "\"" + exe + "\" " + arguments;

        /// <summary>True when the app was started from its Run value.</summary>
        internal static bool HasMinimizedArgument(string[] args)
            => Array.Exists(args, a => string.Equals(a, MinimizedArgument, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Opens the window minimized on the taskbar. Call after the window is built (so its saved
        /// size and maximized state are restored) and before Show. The first restore brings back
        /// a saved maximized window maximized, and fits a floating one to its monitor, since
        /// WorkAreaFit skips a window that opens minimized.
        /// </summary>
        internal static void StartMinimized(Window window)
        {
            bool maximized = window.WindowState == WindowState.Maximized;
            window.WindowState = WindowState.Minimized;

            void OnStateChanged(object? sender, EventArgs e)
            {
                if (window.WindowState == WindowState.Minimized) return;
                window.StateChanged -= OnStateChanged;
                if (window.WindowState != WindowState.Normal) return;
                if (maximized) window.WindowState = WindowState.Maximized;
                else KillerFamily.Windowing.WorkAreaFit.Fit(window, keepMargin: false);
            }
            window.StateChanged += OnStateChanged;
        }

        private static string RunningExe()
        {
            try { return Process.GetCurrentProcess().MainModule?.FileName ?? ""; }
            catch { return ""; }
        }
    }
}
