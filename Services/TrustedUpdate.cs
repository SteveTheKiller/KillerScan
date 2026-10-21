using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace KillerScan.Services
{
    internal static class TrustedUpdate
    {
        internal static void Start(string download, string? version, UpdateService.Platform platform)
        {
            string current = Process.GetCurrentProcess().MainModule!.FileName;
            string directory = UninstallCompletion.InstallDirectory(machine: true);
            string target = Path.Combine(directory, "KillerScan.exe");
            if (!string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                throw new SecurityException("Automatic elevated updates require the protected machine installation. Download retained at: "
                    + download + ". Manual download: " + UpdateService.ReleasesUrl);
            using var pinned = InstallationFiles.PinDirectory(directory);
            ValidateMachineOwner(directory);
            if (!CodeSignature.GetSignerInfo().valid) throw new SecurityException("The installed update helper has no valid signature.");
            string hash = UpdateImagePublication.VerifiedHash(download);
            string requested = NormalizeVersion(version);
            string identity = Guid.NewGuid().ToString("N");
            string staged = Path.Combine(directory, ".update-" + identity + ".exe");
            File.WriteAllText(download + ".recovery.txt", "Automatic update completion has not been confirmed."
                + Environment.NewLine + "Download: " + download + Environment.NewLine + "Protected staging (not yet verified): " + staged
                + Environment.NewLine + "Protected report: " + staged + ".recovery.txt"
                + Environment.NewLine + "Manual download: " + UpdateService.ReleasesUrl);
            using var owner = Process.GetCurrentProcess();
            // Keep the installed helper immutable across the UAC handoff.
            using var image = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read);
            var start = new ProcessStartInfo(current, "/apply-verified-update "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(Path.GetFullPath(download))) + " " + hash + " " + requested
                + " " + owner.Id.ToString(CultureInfo.InvariantCulture)
                + " " + owner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) + " " + identity)
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System) };
            using var helper = platform.Start(start) ?? throw new IOException("The trusted update helper did not start.");
            helper.WaitForExit();
            if (helper.ExitCode != 0) throw new Win32Exception(helper.ExitCode);
        }

        internal static int Apply(string[] args)
        {
            try
            {
                if (args.Length != 7 || !KillerFamily.Deployment.InstallerExitCodes.IsElevated())
                    throw new SecurityException("The machine update operation requires a complete elevated request.");
                string directory = UninstallCompletion.InstallDirectory(machine: true);
                string target = Path.Combine(directory, "KillerScan.exe");
                using var self = Process.GetCurrentProcess();
                if (!string.Equals(self.MainModule!.FileName, target, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("The update helper is not the protected installed application.");
                using var lease = KillerFamily.Deployment.InstallerExitCodes.Acquire("KillerScan");
                using var pinned = InstallationFiles.PinDirectory(directory);
                ValidateMachineOwner(directory);
                string download = Path.GetFullPath(Encoding.Unicode.GetString(Convert.FromBase64String(args[1])));
                string expected = args[2], version = NormalizeVersion(args[3]);
                UpdateImagePublication.RememberVerified(download, expected);
                if (!Guid.TryParseExact(args[6], "N", out _)) throw new SecurityException("The update staging identity is invalid.");
                int parentId = int.Parse(args[4], CultureInfo.InvariantCulture);
                long parentTicks = long.Parse(args[5], CultureInfo.InvariantCulture);
                using var parent = Process.GetProcessById(parentId);
                if (parent.StartTime.ToUniversalTime().Ticks != parentTicks
                    || !string.Equals(parent.MainModule?.FileName, target, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("The update owner does not match the installed application.");
                using var sourcePath = InstallationFiles.PinDirectory(Path.GetDirectoryName(download)!);
                using var source = new FileStream(download, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var hash = SHA256.Create();
                if (!Hex(hash.ComputeHash(source)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("The update changed after download verification.");
                string staged = Path.Combine(directory, ".update-" + args[6] + ".exe");
                if (File.Exists(staged) || Directory.Exists(staged)) throw new IOException("The update staging path is already occupied.");
                ExecutableDeployment.Install(download, staged);
                using (var protectedCopy = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!Hex(hash.ComputeHash(protectedCopy)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new SecurityException("The protected update staging failed SHA-256 verification.");
                    // Release identity belongs to the protected bytes that will be published,
                    // rather than another reopening of the caller-controlled source pathname.
                    var installedSigner = CodeSignature.GetSignerInfo();
                    var incomingSigner = CodeSignature.GetSignerInfo(staged);
                    if (!installedSigner.valid || !incomingSigner.valid
                        || !string.Equals(installedSigner.thumb, incomingSigner.thumb, StringComparison.OrdinalIgnoreCase))
                        throw new SecurityException("The update must have a valid signature from the installed publisher certificate.");
                    var incoming = AssemblyName.GetAssemblyName(staged);
                    var installed = Assembly.GetExecutingAssembly().GetName();
                    if (incoming.Name != "KillerScan" || incoming.Version == null
                        || NormalizeVersion(incoming.Version.ToString()) != version
                        || Version.Parse(version) <= Version.Parse(NormalizeVersion(installed.Version!.ToString())))
                        throw new SecurityException("The update's product or release version does not match a newer KillerScan release.");
                }
                string originalHash;
                using (var original = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                    originalHash = Hex(hash.ComputeHash(original));
                UpdateImagePublication.RememberVerified(staged, expected);
                var start = Publication(staged, target, version, originalHash,
                    parentId, parentTicks, self.Id, self.StartTime.ToUniversalTime().Ticks);
                using var child = Process.Start(start) ?? throw new IOException("The immutable publication helper did not start.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return KillerFamily.Deployment.InstallerExitCodes.FromException(error);
            }
        }

        internal static ProcessStartInfo Publication(string staged, string target, string version, string originalHash,
            int parentId, long parentTicks, int helperId, long helperTicks)
        {
            string publication = UpdateImagePublication.Script(staged, target, true, UpdateService.ReleasesUrl,
                "KillerScan", version, "HKLM");
            string report = staged + ".recovery.txt";
            string body = "$ErrorActionPreference='Stop';$lease=$null;$owned=$false;try{"
                + Wait(parentId, parentTicks) + Wait(helperId, helperTicks)
                + "$lease=New-Object Threading.Mutex($false,'Global\\KillerTools-Install-KillerScan');"
                + "try{$owned=$lease.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owned=$true};if(-not $owned){exit 1618};"
                + "$original=[IO.File]::OpenRead(" + Data(target) + ");$hash=[Security.Cryptography.SHA256]::Create();"
                + "try{if([BitConverter]::ToString($hash.ComputeHash($original)).Replace('-','') -ne " + Data(originalHash)
                + "){throw 'The installed image changed before update publication.'}}finally{$hash.Dispose();$original.Dispose()};\n"
                + publication
                + "\n}catch{$message=$_.Exception.ToString();[Console]::Error.WriteLine($message);try{[IO.File]::AppendAllText("
                + Data(report) + ",[Environment]::NewLine+$message)}catch{};exit 1}"
                + "finally{if($null -ne $lease){if($owned){$lease.ReleaseMutex()};$lease.Dispose()}}";
            return new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"), ImmutablePowerShell.Arguments(body))
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System) };
        }

        private static string Wait(int id, long ticks) =>
            "$p=$null;try{$p=[Diagnostics.Process]::GetProcessById(" + id.ToString(CultureInfo.InvariantCulture)
            + ")}catch [ArgumentException]{};if($null -ne $p -and $p.StartTime.ToUniversalTime().Ticks -eq "
            + ticks.ToString(CultureInfo.InvariantCulture) + " -and -not $p.WaitForExit(60000)){exit 1618};";

        private static string NormalizeVersion(string? text)
        {
            if (!Version.TryParse(text, out var version) || version.Build < 0) throw new SecurityException("The release version is invalid.");
            return version.ToString(3);
        }
        private static string Data(string text) => "[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(text)) + "'))";
        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

        internal static void ValidateMachineOwner(string directory)
        {
            var trusted = new[] { "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464" };
            var security = Directory.GetAccessControl(directory);
            if (!trusted.Contains(security.GetOwner(typeof(SecurityIdentifier)).Value))
                throw new SecurityException("The machine installation has an untrusted owner.");
            var writes = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
                | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                if (rule.AccessControlType == AccessControlType.Allow
                    && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0
                    && (rule.FileSystemRights & writes) != 0 && !trusted.Contains(rule.IdentityReference.Value))
                    throw new SecurityException("The machine installation permits untrusted writes.");
        }
    }
}
