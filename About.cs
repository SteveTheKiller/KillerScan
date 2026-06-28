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
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.5.0";

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
            o.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(120))));
        }

        private static void FadeOverlayOut(UIElement o)
        {
            var a = new DoubleAnimation(o.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(120)));
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

        private void AboutUpdateButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_updateTag))
                OpenUrl($"https://github.com/{GitHubRepo}/releases/tag/{_updateTag}");
        }

        // Shows the current vendor-database size and where it came from (bundled vs last refresh date).
        private void RefreshDbInfo()
        {
            AboutDbBlock.Text = $"{Services.OuiLookup.Count:N0} entries · {Services.OuiLookup.LastRefreshedDisplay}";
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
