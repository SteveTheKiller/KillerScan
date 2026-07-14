using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Navigation;

namespace KillerScan
{
    // About overlay: dims the window and shows a centred card. Ported from KillerPDF's About.cs.
    public partial class MainWindow
    {
        private const string GitHubRepo = "SteveTheKiller/KillerScan";
        private string? _updateTag;

        private static string CurrentVersion =>
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.5.2";

        private void ShowAboutOverlay()
        {
            AboutVersionBlock.Text = $"v{CurrentVersion}";

            var (subject, thumb) = GetSignerInfo();
            AboutPublisherBlock.Text  = subject;
            AboutThumbprintBlock.Text = thumb;
            AboutSha256Block.Text     = "computing…";
            AboutUpdateButton.Visibility = Visibility.Collapsed;

            RefreshDbInfo();
            AboutDbStatus.Text = Loc("Str_About_DbHint");

            FadeOverlayIn(AboutOverlay);

            // SHA-256 is slow on a large EXE; compute off the UI thread.
            Task.Run(() =>
            {
                var h = GetExeSha256();
                Dispatcher.BeginInvoke((Action)(() => AboutSha256Block.Text = h));
            });
            CheckForUpdateAsync(Assembly.GetExecutingAssembly().GetName().Version);
        }

        private static void FadeOverlayIn(UIElement o)
        {
            o.Visibility = Visibility.Visible;
            Anim.FadeIn(o);
        }

        private static void FadeOverlayOut(UIElement o)
        {
            var a = new DoubleAnimation(o.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(Anim.FadeMs)))
            {
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn
                }
            };
            a.Completed += (_, _) => o.Visibility = Visibility.Collapsed;
            o.BeginAnimation(UIElement.OpacityProperty, a);
        }

        // Click the dim backdrop to dismiss; a click on the card itself is swallowed.
        private void AboutOverlay_Click(object sender, MouseButtonEventArgs e) => FadeOverlayOut(AboutOverlay);
        private void AboutCard_Click(object sender, MouseButtonEventArgs e) => e.Handled = true;
        private void AboutClose_Click(object sender, RoutedEventArgs e) => FadeOverlayOut(AboutOverlay);

        private void AboutVersion_Click(object sender, MouseButtonEventArgs e) =>
            OpenUrl($"https://github.com/{GitHubRepo}/releases/tag/v{CurrentVersion}");

        private void AboutLink_Navigate(object sender, RequestNavigateEventArgs e)
        {
            OpenUrl(e.Uri.AbsoluteUri);
            e.Handled = true;
        }

        private void AboutUpdateButton_Click(object sender, RoutedEventArgs e) => DoSelfUpdateAsync();

        // One-click self-update (ported from KillerPDF): downloads the released exe, verifies it against
        // the published SHA256SUMS.txt at the tag, then hands off to a small batch that waits for this
        // process to exit, swaps the exe in place, and relaunches. Falls back to opening the releases
        // page if anything fails (offline, checksum mismatch, unwritable location) so the user can still
        // update by hand.
        private async void DoSelfUpdateAsync()
        {
            var tag = _updateTag;
            if (string.IsNullOrEmpty(tag)) return;

            var dlg = new ConfirmDialog(
                $"Download and install KillerScan {tag}?",
                "The app will close and reopen automatically.",
                "Update") { Owner = this };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            AboutUpdateButton.IsEnabled = false;
            AboutUpdateText.Text = "Downloading...";

            string? newExe = null;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerScan-UpdateCheck");

                var exeUrl  = $"https://github.com/{GitHubRepo}/releases/download/{tag}/KillerScan.exe";
                // Read the checksums from the release ASSET next to the exe, not from raw.githubusercontent
                // at the tag. Both files are uploaded to the release together, so the hash can never drift
                // from the exe the way a repo-committed file does when the tag/commit order gets muddled.
                var sumsUrl = $"https://github.com/{GitHubRepo}/releases/download/{tag}/SHA256SUMS.txt";

                var exeBytes = await http.GetByteArrayAsync(exeUrl);
                var sumsTxt  = await http.GetStringAsync(sumsUrl);

                // Find the expected hash for KillerScan.exe in the checksums file.
                string? expected = null;
                foreach (var line in sumsTxt.Replace("\r", "").Split('\n'))
                {
                    if (line.TrimStart().StartsWith("KillerScan.exe", StringComparison.OrdinalIgnoreCase))
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

                newExe = Path.Combine(Path.GetTempPath(), $"KillerScan_update_{Guid.NewGuid():N}.exe");
                File.WriteAllBytes(newExe, exeBytes);
            }
            catch
            {
                // Offline, timed out, or verification failed: restore the button and open the releases
                // page so the user can update manually.
                AboutUpdateButton.IsEnabled = true;
                AboutUpdateText.Text = $"Update available: {tag}";
                OpenUrl($"https://github.com/{GitHubRepo}/releases/latest");
                return;
            }

            // Apply the update after we exit, then relaunch.
            try
            {
                var curExe = Process.GetCurrentProcess().MainModule!.FileName;
                var pid    = Process.GetCurrentProcess().Id;
                var bat    = Path.Combine(Path.GetTempPath(), $"killerscan_update_{Guid.NewGuid():N}.bat");

                File.WriteAllText(bat,
                    "@echo off\r\n" +
                    ":wait\r\n" +
                    $"tasklist /fi \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul\r\n" +
                    "if not errorlevel 1 ( ping -n 2 127.0.0.1 >nul & goto wait )\r\n" +
                    $"copy /y \"{newExe}\" \"{curExe}\" >nul\r\n" +
                    $"start \"\" \"{curExe}\"\r\n" +
                    $"del \"{newExe}\" >nul 2>&1\r\n" +
                    "del \"%~f0\" >nul 2>&1\r\n");

                Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = true
                });

                Application.Current.Shutdown();
            }
            catch
            {
                AboutUpdateButton.IsEnabled = true;
                AboutUpdateText.Text = $"Update available: {tag}";
            }
        }

        // Shows the current vendor-database size and where it came from (bundled vs last refresh date).
        private void RefreshDbInfo()
        {
            var entries = string.Format(Loc("Str_About_DbEntries"), Services.OuiLookup.Count.ToString("N0"));
            var origin  = Services.OuiLookup.LastRefreshed.HasValue
                ? string.Format(Loc("Str_About_DbRefreshed"), Services.OuiLookup.LastRefreshed.Value.ToString("yyyy-MM-dd"))
                : Loc("Str_About_DbBundled");
            AboutDbBlock.Text = $"{entries} · {origin}";
        }

        // Downloads a fresh OUI list and reloads it in place. Never shrinks the list; all status
        // is reported inline under the link.
        private async void AboutDbUpdate_Click(object sender, RoutedEventArgs e)
        {
            AboutDbUpdateLink.IsEnabled = false;
            AboutDbStatus.Text = "Checking for newer vendor data...";
            var progress = new Progress<string>(s => AboutDbStatus.Text = s);
            try
            {
                var (_, _, msg) = await Services.VendorDbUpdater.UpdateAsync(progress);
                AboutDbStatus.Text = msg;
            }
            catch (Exception ex)
            {
                AboutDbStatus.Text = "Update failed: " + ex.Message;
            }
            RefreshDbInfo();
            AboutDbUpdateLink.IsEnabled = true;
        }

        // Quietly checks GitHub for a newer release when About opens. Times out fast and fails
        // silently with no internet; shows the update button only if a newer tag exists.
        private async void CheckForUpdateAsync(Version? current)
        {
            if (current is null) return;
            try
            {
                System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("KillerScan-UpdateCheck");
                var json = await http.GetStringAsync(
                    $"https://api.github.com/repos/{GitHubRepo}/releases/latest").ConfigureAwait(false);

                var m = System.Text.RegularExpressions.Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                if (!m.Success) return;
                if (!Version.TryParse(m.Groups[1].Value.TrimStart('v', 'V').Trim(), out var latest)) return;

                var cur = new Version(current.Major, current.Minor, current.Build < 0 ? 0 : current.Build);
                var lat = new Version(latest.Major, latest.Minor, latest.Build < 0 ? 0 : latest.Build);
                if (lat <= cur) return;

                await Dispatcher.BeginInvoke((Action)(() =>
                {
                    _updateTag = $"v{lat.ToString(3)}";
                    AboutUpdateText.Text = $"Update available: {_updateTag}";
                    AboutUpdateButton.Visibility = Visibility.Visible;
                }));
            }
            catch { /* offline or API error - silently ignore */ }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { /* no browser available - ignore */ }
        }

        private static (string subject, string thumb) GetSignerInfo()
        {
            try
            {
                var path = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(path)) return ("(unavailable)", "(none)");
                using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
                var subj = cert.GetNameInfo(X509NameType.SimpleName, false);
                return (string.IsNullOrEmpty(subj) ? cert.Subject : subj, cert.Thumbprint ?? "(none)");
            }
            catch { return ("(not signed)", "(none)"); }
        }

        private static string GetExeSha256()
        {
            try
            {
                var path = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(unavailable)";
                using var sha = SHA256.Create();
                using var fs  = File.OpenRead(path);
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            }
            catch { return "(unavailable)"; }
        }
    }
}
