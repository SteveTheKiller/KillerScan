using System;
using System.IO;

namespace KillerScan.Services
{
    /// <summary>
    /// The folder KillerScan keeps its data in: the app-wide stores (history, devices, logins,
    /// overrides, saved scans) and, by default, the Locations root. Test runners point it at a
    /// scratch folder before anything else runs, so no test can reach the user's real data.
    /// </summary>
    internal static class AppDataFolder
    {
        internal static string Path { get; set; } = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KillerScan");

        internal static string File(string name) => System.IO.Path.Combine(Path, name);
    }
}
