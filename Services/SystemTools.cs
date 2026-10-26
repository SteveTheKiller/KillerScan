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
        internal static string Path(string exe) => Path(exe, File.Exists);

        internal static string Path(string exe, Func<string, bool> exists)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (string.Equals(exe, "ssh.exe", StringComparison.OrdinalIgnoreCase))
            {
                // Windows' OpenSSH client lives in its own folder. Without it, a separately installed
                // client on PATH is the only one there is, so the bare name is kept.
                string openSsh = System.IO.Path.Combine(system, "OpenSSH", "ssh.exe");
                return exists(openSsh) ? openSsh : exe;
            }
            return System.IO.Path.Combine(system, exe);
        }

        /// <summary>The folder those tools start in, so they never inherit a download folder.</summary>
        internal static string WorkingDirectory => Environment.GetFolderPath(Environment.SpecialFolder.System);
    }
}
