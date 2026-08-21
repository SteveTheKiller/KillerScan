using System;
using System.Threading.Tasks;
using KillerScan.Controls;
using KillerScan.Services;

namespace KillerScan.Features
{
    /// <summary>
    /// The About card's content: version and release date, the code-signing publisher and
    /// thumbprint, the running exe's SHA-256, the bundled vendor database's size and origin, and a
    /// quiet update check with optional one-click self-update.
    /// </summary>
    internal sealed class AboutController
    {
        private readonly IAboutHost _host;

        /// <summary>Tag of the newer release found by the check, null when there is nothing to
        /// install.</summary>
        private string? _updateTag;

        internal AboutController(IAboutHost host) => _host = host;

        /// <summary>Fills the card in and shows it, then finishes the two slow parts in the
        /// background.</summary>
        internal void Show()
        {
            _host.Version     = $"v{AppInfo.Version}";
            _host.ReleaseDate = AppInfo.ReleaseDate;

            var (sigValid, subject, thumb) = CodeSignature.GetSignerInfo();
            // --demo previews the signed card using the real cert values, so a capture from a local
            // (unsigned) build matches a release. The sigValid override is what lets the alias gate
            // below pass naturally rather than being special-cased twice.
            if (DemoData.Enabled)
            {
                sigValid = true;
                subject  = AppInfo.DemoSubject;
                thumb    = AppInfo.DemoThumbprint;
            }
            _host.Publisher = sigValid ? subject : "(not signed or chain failed)";

            // Shown only when the exe is signed by the expected publisher AND the signature actually
            // verifies: reading a certificate out of a file does not prove the file is untampered.
            bool signedByPublisher = sigValid &&
                subject.IndexOf(AppInfo.SignerName, StringComparison.OrdinalIgnoreCase) >= 0;

            // Only the quoted alias goes here; the prefix and the surrounding hyperlink live in the
            // XAML. 0x201C / 0x201D are the curly quotes, built from codepoints so this line adds no
            // non-ASCII bytes to the source.
            _host.Alias         = (char)0x201C + AppInfo.AkaName + (char)0x201D;
            _host.AliasVisible  = signedByPublisher;
            _host.Thumbprint    = thumb;
            _host.Sha256        = "computing…";
            _host.UpdateVisible = false;

            RefreshDbInfo();
            _host.DbStatus = _host.Loc("Str_About_DbHint");

            _host.ShowCard();

            // SHA-256 is slow on a large exe, so it lands after the card is already up.
            Task.Run(() =>
            {
                var hash = CodeSignature.ExeSha256();
                _host.Window.Dispatcher.BeginInvoke((Action)(() => _host.Sha256 = hash));
            });

            CheckForUpdate();
        }

        /// <summary>Opens the release page for the running version.</summary>
        internal void OpenReleaseNotes() => WebLink.Open(UpdateService.ReleaseUrl(AppInfo.Version));

        // ---- Vendor database ----

        /// <summary>Current vendor-database size and where it came from (bundled, or the date of the
        /// last refresh).</summary>
        private void RefreshDbInfo()
        {
            var entries = string.Format(_host.Loc("Str_About_DbEntries"), OuiLookup.Count.ToString("N0"));
            var origin  = OuiLookup.LastRefreshed.HasValue
                ? string.Format(_host.Loc("Str_About_DbRefreshed"),
                                OuiLookup.LastRefreshed.Value.ToString("yyyy-MM-dd"))
                : _host.Loc("Str_About_DbBundled");
            _host.DbInfo = $"{entries} · {origin}";
        }

        /// <summary>Downloads a fresh OUI list and reloads it in place. Never shrinks the list; all
        /// status is reported inline under the link.</summary>
        internal async void UpdateVendorDb()
        {
            _host.DbUpdateEnabled = false;
            _host.DbStatus = "Checking for newer vendor data...";
            var progress = new Progress<string>(s => _host.DbStatus = s);
            try
            {
                var (_, _, msg) = await VendorDbUpdater.UpdateAsync(progress);
                _host.DbStatus = msg;
            }
            catch (Exception ex)
            {
                _host.DbStatus = "Update failed: " + ex.Message;
            }
            RefreshDbInfo();
            _host.DbUpdateEnabled = true;
        }

        // ---- Self-update ----

        /// <summary>Shows the update button if a newer release exists. Silent when offline.</summary>
        private async void CheckForUpdate()
        {
            var tag = await UpdateService.CheckAsync(AppInfo.AssemblyVersion);
            if (tag is null) return;

            _updateTag          = tag;
            _host.UpdateText    = $"Update available: {tag}";
            _host.UpdateVisible = true;
        }

        /// <summary>Downloads the newer release, verifies it, and hands off to the helper that swaps
        /// the exe and relaunches. Every failure path falls back to the releases page, so a user who
        /// cannot be updated automatically is never left thinking nothing happened.</summary>
        internal async void Update()
        {
            var tag = _updateTag;
            if (string.IsNullOrEmpty(tag)) return;

            var dlg = new ConfirmDialog(
                $"Download and install {AppInfo.DisplayName} {tag}?",
                "The app will close and reopen automatically.",
                "Update") { Owner = _host.Window };
            dlg.ShowDialog();
            if (!dlg.Confirmed) return;

            _host.UpdateEnabled = false;
            _host.UpdateText    = "Downloading...";

            string? newExe;
            try
            {
                newExe = await UpdateService.DownloadAsync(tag!);
            }
            catch
            {
                // Offline, timed out, or verification failed: restore the button and open the
                // releases page so the user can update by hand.
                _host.UpdateEnabled = true;
                _host.UpdateText    = $"Update available: {tag}";
                WebLink.Open(UpdateService.ReleasesUrl);
                return;
            }

            try
            {
                // Declining UAC throws, so the shutdown only happens once the helper is actually
                // running - otherwise the app would close without updating.
                UpdateService.StartSwap(newExe, tag!.TrimStart('v', 'V'));
                System.Windows.Application.Current.Shutdown();
            }
            catch
            {
                UpdateService.DiscardDownload(newExe);
                _host.UpdateEnabled = true;
                _host.UpdateText    = $"Update available: {tag}";
            }
        }
    }
}
