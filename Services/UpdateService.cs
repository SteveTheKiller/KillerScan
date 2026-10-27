using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KillerScan.Services
{
    /// <summary>
    /// Release checking and one-click self-update against the GitHub releases of
    /// <see cref="AppInfo.GitHubRepo"/>.
    ///
    /// Split into three steps so the caller owns every piece of UI and the shutdown:
    /// <see cref="CheckAsync"/> finds a newer tag, <see cref="DownloadAsync"/> fetches and verifies
    /// the exe, <see cref="StartSwap"/> launches the helper that replaces it.
    /// </summary>
    internal static class UpdateService
    {
        /// <summary>Latest release page, used as the fallback whenever an automatic step fails.</summary>
        internal static string ReleasesUrl =>
            $"https://github.com/{AppInfo.GitHubRepo}/releases/latest";

        /// <summary>Release page for one version.</summary>
        internal static string ReleaseUrl(string version) =>
            $"https://github.com/{AppInfo.GitHubRepo}/releases/tag/v{version}";

        private static HttpClient NewClient(int timeoutSeconds)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.DisplayName}-UpdateCheck");
            return http;
        }

        /// <summary>Asks GitHub for the latest release tag and returns it only when it is newer than
        /// <paramref name="current"/>, as "v1.2.3". Null when there is nothing newer, or on any
        /// failure: with no internet this has to stay quiet. Times out fast.</summary>
        internal static async Task<string?> CheckAsync(Version? current)
        {
            if (current is null) return null;
            try
            {
                using var http = NewClient(4);
                var json = await http.GetStringAsync(
                    $"https://api.github.com/repos/{AppInfo.GitHubRepo}/releases/latest")
                    .ConfigureAwait(false);

                var m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) return null;
                if (!Version.TryParse(m.Groups[1].Value.TrimStart('v', 'V').Trim(), out var latest))
                    return null;

                var cur = new Version(current.Major, current.Minor, current.Build < 0 ? 0 : current.Build);
                var lat = new Version(latest.Major, latest.Minor, latest.Build < 0 ? 0 : latest.Build);
                return lat > cur ? $"v{lat.ToString(3)}" : null;
            }
            catch { return null; }
        }

        /// <summary>Downloads the released exe for <paramref name="tag"/>, checks it against the
        /// published SHA256SUMS.txt and writes it to a temp file, whose path is returned. Throws if
        /// the download fails, the checksum entry is missing, or the hash does not match: an update
        /// that cannot be verified must not be installed.</summary>
        internal static async Task<string> DownloadAsync(string tag)
        {
            using var http = NewClient(90);

            var exeUrl = $"https://github.com/{AppInfo.GitHubRepo}/releases/download/{tag}/{AppInfo.ExeName}";
            // The checksums come from the release asset next to the exe, not from the repo at the
            // tag. Both files are uploaded to the release together, so the hash cannot drift from
            // the exe the way a committed file does when the tag and commit order get muddled.
            var sumsUrl = $"https://github.com/{AppInfo.GitHubRepo}/releases/download/{tag}/SHA256SUMS.txt";

            var exeBytes = await http.GetByteArrayAsync(exeUrl).ConfigureAwait(false);
            var sumsTxt  = await http.GetStringAsync(sumsUrl).ConfigureAwait(false);

            string? expected = null;
            foreach (var line in sumsTxt.Replace("\r", "").Split('\n'))
            {
                if (line.TrimStart().StartsWith(AppInfo.ExeName, StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) expected = parts[^1];
                    break;
                }
            }
            if (string.IsNullOrEmpty(expected)) throw new Exception("checksum entry not found");

            string actual;
            using (var sha = SHA256.Create())
                actual = BitConverter.ToString(sha.ComputeHash(exeBytes)).Replace("-", "");
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new Exception("checksum mismatch");

            SweepStaleDownloads(Path.GetTempPath(), DateTime.UtcNow);
            var path = Path.Combine(Path.GetTempPath(),
                                    $"{AppInfo.DisplayName}_update_{Guid.NewGuid():N}.exe");
            File.WriteAllBytes(path, exeBytes);
            // The checksum file comes from the same release as the exe, so it only proves the
            // download arrived intact. The signature, product and version prove where it came from.
            try { ReleaseIdentity.Verify(path, tag, requireInstalledSignature: false); }
            catch { DiscardDownload(path); throw; }
            UpdateImagePublication.RememberVerified(path, actual);
            return path;
        }

        /// <summary>Starts a helper that waits for this process to exit, swaps
        /// <paramref name="newExe"/> over the running exe and relaunches it. Throws if the helper
        /// cannot be started, including when the user declines the UAC prompt (Win32 1223) - the
        /// caller must only shut down once this returns.
        ///
        /// A machine-wide install (Program Files, from the /silent path, winget, choco or an RMM) is
        /// not writable by a normal user, so the swap has to run elevated. The copy result is
        /// checked: without that, an unelevated swap fails silently and relaunches the OLD exe, and
        /// the app appears to update to the same version with no error.</summary>
        internal sealed class Platform
        {
            internal Func<string, bool> CanWrite = CanWriteTo;
            internal Func<bool> IsElevated = KillerFamily.Deployment.InstallerExitCodes.IsElevated;
            internal Func<ProcessStartInfo, Process?> Start = Process.Start;
        }

        internal static void StartSwap(string newExe, string? newVersion = null, Platform? platform = null)
        {
            platform ??= new Platform();
            using var self = Process.GetCurrentProcess();
            var curExe = self.MainModule!.FileName;
            string hive = RegistryHiveFor(curExe);

            // The machine install always goes through the trusted helper, elevated or not. A per-user
            // or portable copy run as administrator can still write its own folder, and the trusted
            // helper would refuse it, so it takes the same helper an unelevated copy uses.
            bool needsElevation = !platform.CanWrite(Path.GetDirectoryName(curExe)!);
            if (needsElevation || (hive == "HKLM" && platform.IsElevated()))
            {
                TrustedUpdate.Start(newExe, newVersion, platform);
                return;
            }

            // The whole helper travels in the process command line, so no file the user account can
            // write is read after it starts. A failed publication opens the recovery report.
            string script = "$ErrorActionPreference = 'Stop'\n"
                + TrustedUpdate.Wait(self.Id, self.StartTime.ToUniversalTime().Ticks) + "\n"
                + UpdateImagePublication.Script(newExe, curExe, false, ReleasesUrl, AppInfo.DisplayName,
                    newVersion, hive, showReport: true);
            var psi = HelperStart(script);
            if (platform.Start(psi) is null)
                throw new IOException("The update helper did not start. Download retained at: " + newExe);
        }

        /// <summary>Starts Windows PowerShell directly with the whole unelevated helper in its command line.</summary>
        internal static ProcessStartInfo HelperStart(string script)
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return new ProcessStartInfo(Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe"),
                ImmutablePowerShell.Arguments(script))
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = system,
                UseShellExecute = true
            };
        }

        /// <summary>"HKCU" when <paramref name="exePath"/> is the per-user install,
        /// "HKLM" when it is the machine-wide install, "" for a portable copy - which has no
        /// install keys and must not gain any from an update.</summary>
        private static string RegistryHiveFor(string exePath)
        {
            try
            {
                string userExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs", AppInfo.DisplayName, AppInfo.ExeName);
                string machineExe = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    AppInfo.DisplayName, AppInfo.ExeName);
                if (string.Equals(exePath, userExe, StringComparison.OrdinalIgnoreCase)) return "HKCU";
                if (string.Equals(exePath, machineExe, StringComparison.OrdinalIgnoreCase)) return "HKLM";
            }
            catch { }
            return "";
        }

        /// <summary>Deletes update downloads and recovery reports from earlier attempts that were
        /// refused, declined or interrupted. Only names this app writes are touched, and only after an
        /// hour, so a download another window is installing right now is left alone.</summary>
        internal static void SweepStaleDownloads(string directory, DateTime utcNow)
        {
            string prefix = AppInfo.DisplayName + "_update_";
            string[] files;
            try { files = Directory.GetFiles(directory, prefix + "*.exe*"); }
            catch { return; }
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                string rest = name.Substring(prefix.Length);
                if (!rest.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !rest.EndsWith(".exe.recovery.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (rest.Length < 32 || !Guid.TryParseExact(rest.Substring(0, 32), "N", out _) || rest[32] != '.') continue;
                try
                {
                    if (utcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(1)) File.Delete(file);
                }
                catch { /* In use or protected: a later sweep gets it. */ }
            }
        }

        /// <summary>Deletes a downloaded update that will not be installed.</summary>
        internal static void DiscardDownload(string? path)
        {
            try { if (path is not null && File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>True if this process can create a file in <paramref name="dir"/>. Decides whether
        /// the swap needs elevating: Program Files installs are not writable by a normal user,
        /// per-user installs under LOCALAPPDATA always are.</summary>
        private static bool CanWriteTo(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, $".upd_write_{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                      1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }
    }
}
