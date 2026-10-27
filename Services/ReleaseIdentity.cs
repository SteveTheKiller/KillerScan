using System;
using System.IO;
using System.Reflection;
using System.Security;

namespace KillerScan.Services
{
    /// <summary>
    /// Decides whether an update image is a newer release of this app from the same publisher as the
    /// running copy. Both update paths call it: the per-user swap on the downloaded file and the
    /// elevated helper on the held-open download and again on the protected staged copy.
    /// </summary>
    internal static class ReleaseIdentity
    {
        /// <summary>Throws <see cref="SecurityException"/> unless <paramref name="exe"/> is a validly
        /// signed <paramref name="version"/> that is newer than the running copy and, when
        /// the running copy is signed, signed by the same certificate. An unsigned running copy is a
        /// local build that cannot vouch for a publisher, so it accepts any validly signed release;
        /// <paramref name="requireInstalledSignature"/> removes that allowance for the elevated path.</summary>
        internal static void Verify(string exe, string version, bool requireInstalledSignature)
        {
            var installed = CodeSignature.GetSignerInfo();
            var incoming = CodeSignature.GetSignerInfo(exe);
            AssemblyName name;
            try { name = AssemblyName.GetAssemblyName(exe); }
            catch (Exception error) when (error is BadImageFormatException or FileLoadException)
            { throw new SecurityException("The update is not a " + AppInfo.DisplayName + " executable.", error); }
            Verify(name, version, Assembly.GetExecutingAssembly().GetName().Version!,
                (installed.valid, installed.thumb), (incoming.valid, incoming.thumb), requireInstalledSignature);
        }

        internal static void Verify(AssemblyName incoming, string version, Version installed,
            (bool valid, string thumb) installedSigner, (bool valid, string thumb) incomingSigner,
            bool requireInstalledSignature)
        {
            if (requireInstalledSignature && !installedSigner.valid)
                throw new SecurityException("The installed update helper has no valid signature.");
            if (!incomingSigner.valid)
                throw new SecurityException("The update has no valid Authenticode signature.");
            if (installedSigner.valid
                && !string.Equals(installedSigner.thumb, incomingSigner.thumb, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("The update must have a valid signature from the installed publisher certificate.");
            string requested = NormalizeVersion(version);
            if (incoming.Name != AppInfo.DisplayName || incoming.Version == null
                || NormalizeVersion(incoming.Version.ToString()) != requested
                || Version.Parse(requested) <= Version.Parse(NormalizeVersion(installed.ToString())))
                throw new SecurityException("The update's product or release version does not match a newer " + AppInfo.DisplayName + " release.");
        }

        internal static string NormalizeVersion(string? text)
        {
            text = text?.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(text, out var version) || version.Build < 0) throw new SecurityException("The release version is invalid.");
            return version.ToString(3);
        }

        /// <summary>Accepts only an ordinary file on a local drive letter, so the elevated helper never
        /// opens a UNC, device, pipe or reparse-point path handed to it on its command line.</summary>
        internal static string RequireLocalFile(string path)
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\", StringComparison.Ordinal) || full.Length < 4
                || !char.IsLetter(full[0]) || full[1] != ':' || full[2] != '\\')
                throw new SecurityException("The update download must be a file on a local drive.");
            if (new DriveInfo(full.Substring(0, 3)).DriveType is DriveType.Network or DriveType.NoRootDirectory)
                throw new SecurityException("The update download must be a file on a local drive.");
            var attributes = File.GetAttributes(full);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new SecurityException("The update download must be an ordinary file.");
            return full;
        }
    }
}
