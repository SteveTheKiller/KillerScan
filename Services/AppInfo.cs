using System.Reflection;

namespace KillerScan.Services
{
    /// <summary>
    /// Application identity and the build stamps baked into the assembly. One place for the values
    /// the About card, the update check, the command line and the release links all need.
    /// </summary>
    internal static class AppInfo
    {
        /// <summary>owner/repo used for update checks and release links.</summary>
        internal const string GitHubRepo = "SteveTheKiller/KillerScan";

        /// <summary>Release asset name, and the on-disk name of the exe it replaces.</summary>
        internal const string ExeName = "KillerScan.exe";

        /// <summary>Name shown in prompts and console output.</summary>
        internal const string DisplayName = "KillerScan";

        /// <summary>Product website, opened from the title-bar wordmark and stamped into reports.</summary>
        internal const string SiteUrl = "https://scan.killertools.net";

        /// <summary>Certificate subject fragment that unlocks the alias line on the About card. The
        /// subject is a legal name, so the card ties it back to the publicly known alias. A build
        /// signed by anyone else must not claim that alias, and an unsigned build has no subject.
        /// </summary>
        internal const string SignerName = "Stephen Riley";

        /// <summary>The alias shown when the signature verifies and the subject matches.</summary>
        internal const string AkaName = "Steve the Killer";

        /// <summary>--demo previews the About card as it looks on a signed release, so marketing
        /// captures taken from a local build match the real thing. These are the REAL certificate
        /// values, not invented ones: a published screenshot must never show a fingerprint that
        /// does not exist. Refresh both if the cert is replaced; the current one expires
        /// 2027-05-04.</summary>
        internal const string DemoSubject    = "Open Source Developer Stephen Riley";
        internal const string DemoThumbprint = "E478E4940DFD3547DAF2199494B399214FB3E0FD";

        /// <summary>Display version, from the informational version attribute.</summary>
        internal static string Version =>
            Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
            ?? "1.0.0";

        /// <summary>Numeric version used for comparing against a release tag.</summary>
        internal static System.Version? AssemblyVersion =>
            Assembly.GetExecutingAssembly().GetName().Version;

        /// <summary>Release date baked in from the csproj ReleaseDate property, so a user can see how
        /// old their build is. Empty when the attribute is missing, in which case the version line
        /// shows the version alone. A file timestamp does not survive being copied and the PE linker
        /// stamp is a build date, so neither of those can stand in for it.</summary>
        internal static string ReleaseDate
        {
            get
            {
                foreach (var a in Assembly.GetExecutingAssembly()
                                          .GetCustomAttributes<AssemblyMetadataAttribute>())
                    if (a.Key == "ReleaseDate") return a.Value ?? string.Empty;
                return string.Empty;
            }
        }
    }
}
