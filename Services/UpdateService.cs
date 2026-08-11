using System;
using System.Diagnostics;
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

            var path = Path.Combine(Path.GetTempPath(),
                                    $"{AppInfo.DisplayName}_update_{Guid.NewGuid():N}.exe");
            File.WriteAllBytes(path, exeBytes);
            return path;
        }

        /// <summary>Starts a helper batch that waits for this process to exit, swaps
        /// <paramref name="newExe"/> over the running exe and relaunches it. Throws if the helper
        /// cannot be started, including when the user declines the UAC prompt (Win32 1223) - the
        /// caller must only shut down once this returns.
        ///
        /// A machine-wide install (Program Files, from the /silent path, winget, choco or an RMM) is
        /// not writable by a normal user, so the swap has to run elevated. The copy result is
        /// checked: without that, an unelevated swap fails silently and relaunches the OLD exe, and
        /// the app appears to update to the same version with no error.</summary>
        internal static void StartSwap(string newExe)
        {
            var curExe = Process.GetCurrentProcess().MainModule!.FileName;
            var pid    = Process.GetCurrentProcess().Id;
            var bat    = Path.Combine(Path.GetTempPath(),
                                      $"{AppInfo.DisplayName}_update_{Guid.NewGuid():N}.bat");

            bool needsElevation = !CanWriteTo(Path.GetDirectoryName(curExe)!);

            // When elevated, relaunch through explorer.exe so the app comes back at the user's
            // normal integrity level instead of inheriting the elevated token.
            string relaunch = needsElevation
                ? $"start \"\" explorer.exe \"{curExe}\""
                : $"start \"\" \"{curExe}\"";

            File.WriteAllText(bat,
                "@echo off\r\n" +
                ":wait\r\n" +
                $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
                "if not errorlevel 1 ( ping -n 2 127.0.0.1 >nul & goto wait )\r\n" +
                $"copy /y \"{newExe}\" \"{curExe}\" >nul 2>&1\r\n" +
                "if errorlevel 1 goto failed\r\n" +
                relaunch + "\r\n" +
                "goto cleanup\r\n" +
                ":failed\r\n" +
                // Do not relaunch a stale exe and call it an update: send the user to the releases
                // page so the failure is visible and fixable by hand.
                $"start \"\" \"{ReleasesUrl}\"\r\n" +
                ":cleanup\r\n" +
                $"del \"{newExe}\" >nul 2>&1\r\n" +
                "del \"%~f0\" >nul 2>&1\r\n");

            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                UseShellExecute = true
            };
            if (needsElevation) psi.Verb = "runas";   // triggers the UAC prompt

            Process.Start(psi);
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
                var probe = Path.Combine(dir, $".ks_write_{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                      1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }
    }
}
