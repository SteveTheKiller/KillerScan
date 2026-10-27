using System;
using KillerScan.Services;

namespace KillerScan.Features.About
{
    /// <summary>The About card's vendor-database rows (KillerScan's own, beside the shared core).</summary>
    internal interface IAboutVendorDb
    {
        /// <summary>Vendor-database size and origin, and the line under the refresh link that
        /// reports what a refresh did. The link goes insensitive while one is running.</summary>
        string DbInfo { set; }
        string DbStatus { set; }
        bool DbUpdateEnabled { set; }
    }

    /// <summary>
    /// APP-OWNED: KillerScan's additions to the shared AboutController (kit file
    /// Features/About/AboutController.cs): the bundled vendor database's size, origin and refresh,
    /// the --demo signed preview, and localized text for values that could not be read. Kept out
    /// of the kit drift check (KillerHarness family/kit-owned.json).
    /// </summary>
    internal sealed partial class AboutController
    {
        private IAboutVendorDb? VendorDb => _host as IAboutVendorDb;

        partial void OnShow()
        {
            if (VendorDb is not { } db) return;
            RefreshDbInfo();
            db.DbStatus = _host.Loc("Str_About_DbHint");
        }

        // --demo previews the signed card using the real cert values, so a capture from a local
        // (unsigned) build matches a release. The valid override is what lets the alias gate pass
        // naturally rather than being special-cased twice.
        partial void AdjustSignature(ref bool valid, ref string subject, ref string thumbprint)
        {
            if (!DemoData.Enabled) return;
            valid = true;
            subject = AppInfo.DemoSubject;
            thumbprint = AppInfo.DemoThumbprint;
        }

        partial void ShownValue(ref string value)
        {
            if (value is "(none)" or "(unavailable)") value = _host.Loc("Str_Diag_Unavailable");
        }

        // Captures stay clean under --demo.
        partial void SkipStartupCheck(ref bool skip) => skip |= DemoData.Enabled;

        /// <summary>Current vendor-database size and where it came from (bundled, or the date of the
        /// last refresh).</summary>
        private void RefreshDbInfo()
        {
            if (VendorDb is not { } db) return;
            var entries = string.Format(_host.Loc("Str_About_DbEntries"), OuiLookup.Count.ToString("N0"));
            var origin  = OuiLookup.LastRefreshed.HasValue
                ? string.Format(_host.Loc("Str_About_DbRefreshed"),
                                OuiLookup.LastRefreshed.Value.ToString("yyyy-MM-dd"))
                : _host.Loc("Str_About_DbBundled");
            db.DbInfo = $"{entries} · {origin}";
        }

        /// <summary>Downloads a fresh OUI list and reloads it in place. Never shrinks the list; all
        /// status is reported inline under the link.</summary>
        internal async void UpdateVendorDb()
        {
            if (VendorDb is not { } db) return;
            db.DbUpdateEnabled = false;
            db.DbStatus = _host.Loc("Str_Db_Checking");
            // The updater reports progress and its outcome as RESOURCE KEYS plus arguments, not as
            // finished English. It lives in Services/ with no access to the resource dictionary,
            // and the alternative - handing it a localizer - would have made a background service
            // depend on the UI (VendorDbUpdater).
            var progress = new Progress<VendorDbUpdater.Status>(s => db.DbStatus = Format(s));
            try
            {
                var (_, _, status) = await VendorDbUpdater.UpdateAsync(progress);
                db.DbStatus = Format(status);
            }
            catch (Exception ex)
            {
                db.DbStatus = string.Format(_host.Loc("Str_Db_Failed"), ex.Message);
            }
            RefreshDbInfo();
            db.DbUpdateEnabled = true;
        }

        /// <summary>Turns one of the updater's key-plus-arguments reports into a sentence in the
        /// current language. Counts are formatted here rather than by the service, so they pick up
        /// the culture's own thousands separator.</summary>
        private string Format(VendorDbUpdater.Status s)
        {
            // Detail carries an exception's own text, which is not ours to translate - it goes in
            // as {0} verbatim. Otherwise the arguments are counts, formatted N0.
            if (s.Detail is string d) return string.Format(_host.Loc(s.Key), d);
            var args = new object[s.Args.Length];
            for (int i = 0; i < s.Args.Length; i++) args[i] = s.Args[i].ToString("N0");
            return string.Format(_host.Loc(s.Key), args);
        }
    }
}
