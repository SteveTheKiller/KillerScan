using System;
using System.IO;

namespace KillerScan.Services
{
    /// <summary>
    /// Full paths for the Windows tools that device actions start in their own window. A bare name
    /// makes Windows look in the application folder and the current folder first, so a portable copy
    /// run from Downloads would start any mstsc.exe, ssh.exe or ping.exe sitting beside it.
    /// </summary>
    internal static class SystemTools
    {
        internal static string Path(string exe) => Path(exe, File.Exists,
            Environment.GetEnvironmentVariable("PATH") ?? string.Empty, AppDomain.CurrentDomain.BaseDirectory);

        internal static string Path(string exe, Func<string, bool> exists) => Path(exe, exists, string.Empty, string.Empty);

        internal static string Path(string exe, Func<string, bool> exists, string searchPath, string appDirectory)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (string.Equals(exe, "ssh.exe", StringComparison.OrdinalIgnoreCase))
            {
                // Windows' OpenSSH client lives in its own folder. Without it, a separately installed
                // client is looked up on PATH here, by full path, skipping the app's own folder and
                // relative entries. A bare name is never returned: Windows would search beside the
                // exe first. With no client anywhere, the System32 path fails plainly instead.
                string openSsh = System.IO.Path.Combine(system, "OpenSSH", "ssh.exe");
                if (exists(openSsh)) return openSsh;
                string app = appDirectory.TrimEnd('\\', '/');
                foreach (string entry in searchPath.Split(';'))
                {
                    string folder = entry.Trim().Trim('"').TrimEnd('\\', '/');
                    if (folder.Length == 0 || !System.IO.Path.IsPathRooted(folder) || folder.StartsWith(".", StringComparison.Ordinal)
                        || string.Equals(folder, app, StringComparison.OrdinalIgnoreCase)) continue;
                    string candidate = System.IO.Path.Combine(folder, exe);
                    if (exists(candidate)) return candidate;
                }
                return openSsh;
            }
            return System.IO.Path.Combine(system, exe);
        }

        /// <summary>The folder those tools start in, so they never inherit a download folder.</summary>
        internal static string WorkingDirectory => Environment.GetFolderPath(Environment.SpecialFolder.System);
    }
}
