using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KillerScan.Services
{
    /// <summary>
    /// Puts an installed copy on PATH so a new terminal can run the app by name from any folder. The
    /// install folder is added to the current user's PATH, or to the machine PATH for an
    /// all-users install, and the same folder is taken out again on uninstall. Portable copies
    /// register nothing.
    /// </summary>
    internal static class CommandLinePath
    {
        private const string UserEnvironmentKey = "Environment";
        private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
        private const string PathValueName = "Path";

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
            uint flags, uint timeout, out IntPtr result);
        private static readonly IntPtr HwndBroadcast = new IntPtr(0xffff);
        private const uint WmSettingChange = 0x001A;
        private const uint SmtoAbortIfHung = 0x0002;

        /// <summary>Adds the install folder to the current user's PATH, or to the machine PATH for
        /// an all-users install. Best effort: an install goes ahead even when PATH cannot be
        /// written.</summary>
        internal static void Register(string installDirectory, bool machine)
        {
            try
            {
                using (RegistryKey key = OpenEnvironment(machine))
                {
                    if (AddTo(key, installDirectory)) BroadcastEnvironmentChange();
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException
                || ex is IOException || ex is InvalidOperationException) { }
        }

        /// <summary>Takes the install folder back out of PATH. Best effort: uninstall and the
        /// move to an all-users install go ahead even when the entry cannot be removed.</summary>
        internal static void Unregister(string installDirectory, bool machine)
        {
            try
            {
                using (RegistryKey key = OpenEnvironment(machine))
                {
                    if (RemoveFrom(key, installDirectory)) BroadcastEnvironmentChange();
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException
                || ex is IOException || ex is InvalidOperationException) { }
        }

        /// <summary>Unregister that lets a failure through, for an uninstall that must not leave
        /// a stale PATH entry behind silently.</summary>
        internal static void UnregisterRequired(string installDirectory, bool machine)
        {
            using (RegistryKey key = OpenEnvironment(machine))
                if (RemoveFrom(key, installDirectory)) BroadcastEnvironmentChange();
        }

        private static RegistryKey OpenEnvironment(bool machine) => machine
            ? Registry.LocalMachine.OpenSubKey(MachineEnvironmentKey, writable: true)
                ?? throw new InvalidOperationException("The machine environment is unavailable.")
            : Registry.CurrentUser.CreateSubKey(UserEnvironmentKey, writable: true);

        /// <summary>Adds the folder to the key's Path value once, keeping unexpanded entries such
        /// as %SystemRoot% and the value's registry type. Returns true when the value changed.</summary>
        internal static bool AddTo(RegistryKey environment, string directory)
        {
            (string current, RegistryValueKind kind) = ReadPath(environment);
            string updated = WithEntry(current, directory);
            if (string.Equals(updated, current, StringComparison.Ordinal)) return false;
            environment.SetValue(PathValueName, updated, kind);
            return true;
        }

        /// <summary>Removes every spelling of the folder from the key's Path value without touching
        /// other entries. Returns true when the value changed.</summary>
        internal static bool RemoveFrom(RegistryKey environment, string directory)
        {
            if (environment.GetValue(PathValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) == null)
                return false;
            (string current, RegistryValueKind kind) = ReadPath(environment);
            string updated = WithoutEntry(current, directory);
            if (string.Equals(updated, current, StringComparison.Ordinal)) return false;
            environment.SetValue(PathValueName, updated, kind);
            return true;
        }

        private static (string Value, RegistryValueKind Kind) ReadPath(RegistryKey environment)
        {
            object? raw = environment.GetValue(PathValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw == null) return (string.Empty, RegistryValueKind.ExpandString);
            RegistryValueKind kind = environment.GetValueKind(PathValueName);
            return (raw as string ?? string.Empty,
                kind == RegistryValueKind.String || kind == RegistryValueKind.ExpandString
                    ? kind : RegistryValueKind.ExpandString);
        }

        /// <summary>The PATH text with the folder appended, unless some entry already names it.</summary>
        internal static string WithEntry(string path, string directory)
        {
            List<string> entries = Entries(path);
            if (entries.Any(entry => SameDirectory(entry, directory))) return path;
            entries.Add(Normalize(directory));
            return string.Join(";", entries);
        }

        /// <summary>The PATH text without any entry naming the folder; unchanged when none does.</summary>
        internal static string WithoutEntry(string path, string directory)
        {
            List<string> entries = Entries(path);
            List<string> kept = entries.Where(entry => !SameDirectory(entry, directory)).ToList();
            return kept.Count == entries.Count ? path : string.Join(";", kept);
        }

        private static List<string> Entries(string path) =>
            path.Split(';').Where(entry => entry.Trim().Length > 0).ToList();

        private static string Normalize(string directory) =>
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        internal static bool SameDirectory(string entry, string directory)
        {
            try
            {
                // Only a fully qualified entry can name the install folder. A relative one such
                // as "." or "C:bin" would resolve against whatever folder setup runs from and
                // belongs to the user.
                string expanded = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                if (!IsFullyQualified(expanded)) return false;
                return string.Equals(Normalize(expanded), Normalize(directory), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        private static bool IsFullyQualified(string path) =>
            path.StartsWith(@"\\", StringComparison.Ordinal)
            || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':'
                && (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar));

        internal static void BroadcastEnvironmentChange()
        {
            // Explorer rereads the environment on this broadcast, so terminals opened from the
            // desktop after setup see the new PATH without signing out.
            try
            {
                SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment",
                    SmtoAbortIfHung, 2000, out _);
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException) { }
        }
    }
}
