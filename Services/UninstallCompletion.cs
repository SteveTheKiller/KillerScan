using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace KillerScan.Services
{
    /// <summary>The registered Windows helper owns the final exit code and removes its stopped copy.</summary>
    internal static class UninstallCompletion
    {
        private const string ProductKey = @"Software\KillerScan";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KillerScan";
        private static readonly string[] ProductValues = { "Installed", "InstallPath", "Version" };
        private static readonly string[] UninstallValues = { "DisplayName", "DisplayVersion", "Publisher",
            "InstallLocation", "DisplayIcon", "UninstallString", "QuietUninstallString", "NoModify", "NoRepair" };

        internal static string InstallDirectory(bool machine) => machine
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KillerScan")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "KillerScan");

        internal static string MachineDirectoryPolicy =>
            "$trusted=@('S-1-5-18','S-1-5-32-544','S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464');"
            + "if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted){throw 'The machine installation has an untrusted owner.'};"
            + "if($creatorOwner -notin $trusted){throw 'The machine staging creator has an untrusted owner.'};"
            + "$writes=[Security.AccessControl.FileSystemRights]'Write,Delete,DeleteSubdirectoriesAndFiles,ChangePermissions,TakeOwnership';"
            + "$genericWrites=0x50000000;"
            + "foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){"
            + "if($rule.AccessControlType -eq 'Allow' -and (($rule.FileSystemRights -band $writes) -or ([long]$rule.FileSystemRights -band $genericWrites))"
            + " -and $rule.IdentityReference.Value -notin $trusted -and -not ($rule.IdentityReference.Value -eq 'S-1-3-0'"
            + " -and ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly))){throw 'The machine installation permits untrusted writes.'}};";

        internal static ProcessStartInfo StartInfo(bool machine, bool silent, int parentId = 0, long parentTicks = 0)
        {
            string script = "$ErrorActionPreference='Stop';try{\n";
            script += "$root=" + Data(InstallDirectory(machine)) + ";$image=[IO.Path]::Combine($root,'KillerScan.exe');\n";
            if (parentId != 0)
                script += "$owner=$null;try{$owner=[Diagnostics.Process]::GetProcessById(" + parentId + ")}catch [ArgumentException]{};"
                    + "if($null -ne $owner -and $owner.StartTime.ToUniversalTime().Ticks -eq " + parentTicks
                    + " -and -not $owner.WaitForExit(60000)){exit 1618};\n";
            script += "if(-not [IO.File]::Exists($image)){throw 'The installed image is missing. Retain the uninstall registration for repair.'};\n";
            script += "for($item=$root;$null -ne $item;$item=[IO.Path]::GetDirectoryName($item)){"
                + "if([IO.Directory]::Exists($item) -and ([IO.File]::GetAttributes($item) -band [IO.FileAttributes]::ReparsePoint)){throw 'Uninstall cannot traverse a linked installation path.'}};\n";
            if (machine)
            {
                // The registered command outlives updates, so it holds the image to the publisher
                // name, not a thumbprint that a renewed certificate would change.
                script += "$acl=[IO.Directory]::GetAccessControl($root);$creator=[Security.Principal.WindowsIdentity]::GetCurrent();"
                    + "try{$creatorOwner=$creator.Owner.Value}finally{$creator.Dispose()};" + MachineDirectoryPolicy
                    + "$signature=Get-AuthenticodeSignature -LiteralPath $image;"
                    + "if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName,$false) -notlike ('*'+"
                    + Data(AppInfo.SignerName) + "+'*')){throw 'The installed uninstall helper does not match its trusted publisher.'};\n";
            }
            // A machine uninstall asks in the unelevated wrapper below, before the UAC prompt.
            const string confirmation = "$confirm=[Diagnostics.Process]::Start($image,'/confirm-uninstall');if($null -eq $confirm){throw 'The uninstall confirmation did not start.'};"
                + "$confirm.WaitForExit();$answer=$confirm.ExitCode;$confirm.Dispose();if($answer -ne 0){exit $answer};";
            if (!silent && !machine)
                script += confirmation + "\n";
            script += "$stage=[IO.Path]::Combine($root,'.uninstall-'+[Guid]::NewGuid().ToString('N'));"
                + "[IO.Directory]::CreateDirectory($stage)|Out-Null;$helper=[IO.Path]::Combine($stage,'KillerScan.exe');\n"
                + "$input=[IO.File]::Open($image,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);"
                + "try{$hash=[Security.Cryptography.SHA256]::Create();try{$expected=[BitConverter]::ToString($hash.ComputeHash($input));$input.Position=0}finally{$hash.Dispose()};"
                + "$output=[IO.File]::Open($helper,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);"
                + "try{$input.CopyTo($output);$output.Flush($true)}finally{$output.Dispose()};"
                + "$copy=[IO.File]::OpenRead($helper);$hash=[Security.Cryptography.SHA256]::Create();"
                + "try{if([BitConverter]::ToString($hash.ComputeHash($copy)) -ne $expected){throw 'The uninstall helper copy failed verification.'}}"
                + "finally{$hash.Dispose();$copy.Dispose()}}finally{$input.Dispose()};\n"
                + "$start=New-Object Diagnostics.ProcessStartInfo;$start.FileName=$helper;$start.Arguments='/complete-uninstall "
                + (machine ? "machine" : "user") + "';$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$stage;"
                + "$child=[Diagnostics.Process]::Start($start);if($null -eq $child){throw 'The uninstall helper did not start.'};"
                + "$child.WaitForExit();$code=$child.ExitCode;$child.Dispose();if($code -ne 0){[Console]::Error.WriteLine('Uninstall failed. Recovery helper retained at: '+$helper);exit $code};\n"
                + "[IO.File]::Delete($helper);[IO.Directory]::Delete($stage,$false);"
                + "if([IO.File]::Exists($image)){throw 'The installed image remains.'};"
                + "if([IO.Directory]::Exists($root)){if([IO.Directory]::GetFileSystemEntries($root).Length -eq 0){[IO.Directory]::Delete($root,$false)}"
                + "else{[Console]::Error.WriteLine('Unrelated contents were preserved in: '+$root)}};exit 0\n"
                + "}catch{$failure=$_.Exception.GetBaseException();[Console]::Error.WriteLine($failure.Message);"
                + "if($failure -is [UnauthorizedAccessException]){exit 5};$code=$failure.HResult -band 65535;"
                + "if($code -in @(32,33)){exit 1618};exit 1}";

            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            string inner = ImmutablePowerShell.Arguments(script);
            if (machine)
            {
                // The elevated command is immutable process data. It never reads code from TEMP.
                // Confirmation runs here, unelevated, so cancelling never spends a UAC prompt and the
                // dialog never runs as administrator.
                string elevation = "$ErrorActionPreference='Stop';try{"
                    + (silent ? "" : "$image=[IO.Path]::Combine(" + Data(InstallDirectory(machine)) + ",'KillerScan.exe');" + confirmation)
                    + "$s=New-Object Diagnostics.ProcessStartInfo;"
                    + "$s.FileName=" + Data(powershell) + ";$s.Arguments='" + inner.Replace("'", "''") + "';"
                    + "$s.UseShellExecute=$true;$s.Verb='runas';$s.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden;"
                    + "$p=[Diagnostics.Process]::Start($s);if($null -eq $p){exit 1};$p.WaitForExit();$code=$p.ExitCode;$p.Dispose();exit $code"
                    + "}catch{$e=$_.Exception.GetBaseException();[Console]::Error.WriteLine($e.Message);"
                    + "if($e -is [ComponentModel.Win32Exception]){if($e.NativeErrorCode -eq 1223){exit 1223};if($e.NativeErrorCode -eq 5){exit 5}};exit 1}";
                inner = ImmutablePowerShell.Arguments(elevation);
            }
            var result = new ProcessStartInfo(powershell, inner)
            { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System) };
            if (result.Arguments.Length > 30000) throw new IOException("The uninstall helper command exceeds the Windows command limit.");
            return result;
        }

        internal static string Command(bool machine, bool silent)
        {
            var start = StartInfo(machine, silent);
            return "\"" + start.FileName + "\" " + start.Arguments;
        }

        internal static int Complete(string scope)
        {
            try
            {
                bool machine = scope == "machine";
                if (!machine && scope != "user") throw new ArgumentException("Invalid uninstall scope.");
                if (machine && !KillerFamily.Deployment.InstallerExitCodes.IsElevated())
                    throw new UnauthorizedAccessException("Machine uninstall requires administrator access.");
                string root = InstallDirectory(machine);
                string current = Process.GetCurrentProcess().MainModule!.FileName;
                string stage = Path.GetDirectoryName(current)!;
                string stageName = Path.GetFileName(stage);
                if (!string.Equals(Path.GetDirectoryName(stage), root, StringComparison.OrdinalIgnoreCase)
                    || !stageName.StartsWith(".uninstall-", StringComparison.Ordinal)
                    || !Guid.TryParseExact(stageName.Substring(11), "N", out _)
                    || !string.Equals(Path.GetFileName(current), "KillerScan.exe", StringComparison.OrdinalIgnoreCase))
                    throw new UnauthorizedAccessException("The completion helper must run from its owned installation staging directory.");
                using var lease = KillerFamily.Deployment.InstallerExitCodes.Acquire("KillerScan");
                using var pinned = InstallationFiles.PinDirectory(stage);
                string image = Path.Combine(root, "KillerScan.exe");
                using (var original = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var helper = new FileStream(current, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var sha = SHA256.Create())
                    if (!sha.ComputeHash(original).SequenceEqual(sha.ComputeHash(helper)))
                        throw new InvalidDataException("The installed image changed after preparation. Nothing was removed.");
                RemoveOwned(machine);
                RemoveStaleHelpers(root, stage);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return KillerFamily.Deployment.InstallerExitCodes.FromException(error);
            }
        }

        /// <summary>
        /// Removes the helper folders earlier uninstalls left behind, such as one refused because
        /// the app was running. Only a folder named .uninstall-&lt;32 hex&gt; holding nothing but the
        /// helper copy is touched, and never <paramref name="current"/>, the one running now. The
        /// installation is already gone by now, so anything that cannot go simply stays.
        /// </summary>
        internal static void RemoveStaleHelpers(string root, string current)
        {
            string[] folders;
            try { folders = Directory.GetDirectories(root, ".uninstall-*"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
            foreach (string folder in folders)
            {
                if (string.Equals(Path.GetFullPath(folder), Path.GetFullPath(current), StringComparison.OrdinalIgnoreCase)) continue;
                if (!Guid.TryParseExact(Path.GetFileName(folder).Substring(".uninstall-".Length), "N", out _)) continue;
                string helper = Path.Combine(folder, "KillerScan.exe");
                try
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                    var entries = Directory.GetFileSystemEntries(folder);
                    if (entries.Length != 1 || !string.Equals(entries[0], helper, StringComparison.OrdinalIgnoreCase)) continue;
                    if ((File.GetAttributes(helper) & FileAttributes.ReparsePoint) != 0) continue;
                    File.Delete(helper);
                    Directory.Delete(folder, recursive: false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }

        internal static void RemoveOwned(bool machine)
        {
            string directory = InstallDirectory(machine);
            string image = Path.Combine(directory, "KillerScan.exe");
            foreach (var process in Process.GetProcessesByName("KillerScan"))
                using (process)
                    if (RunsImage(() => process.MainModule?.FileName, image))
                        throw new Win32Exception(KillerFamily.Deployment.InstallerExitCodes.Busy);
            using (var registration = new Registration(machine))
                RemovalTransaction.Run(directory, registration.Remove, registration.Restore, CommandLinePath.BroadcastEnvironmentChange);
            string menu = Path.Combine(Environment.GetFolderPath(machine
                ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs), "KillerScan");
            InstallationFiles.RemoveEmpty(menu);
        }

        /// <summary>Whether a process is running <paramref name="image"/>. A process that exited
        /// first, or that belongs to another user or session and cannot be inspected, is a different
        /// image as far as this installation is concerned, so it never blocks removal.</summary>
        internal static bool RunsImage(Func<string?> mainModule, string image)
        {
            try { return string.Equals(mainModule(), image, StringComparison.OrdinalIgnoreCase); }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }

        private static string Data(string value) => "[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('"
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(value)) + "'))";

        private sealed class Registration : IDisposable
        {
            private readonly RegistryKey? _product, _uninstall;
            private readonly RegistryKey _environment;
            private readonly Values _productValues, _uninstallValues, _path;
            private readonly string _directory;
            private readonly List<(string Path, byte[] Bytes)> _shortcuts = new();
            private readonly List<IDisposable> _pins = new();

            internal Registration(bool machine)
            {
                _directory = InstallDirectory(machine);
                var hive = machine ? Registry.LocalMachine : Registry.CurrentUser;
                // A copy placed by hand, a half-finished install or a registry cleaner can leave the
                // files without these keys. A missing key has nothing to unregister; the files still go.
                _product = hive.OpenSubKey(ProductKey, writable: true);
                _uninstall = hive.OpenSubKey(UninstallKey, writable: true);
                _environment = hive.OpenSubKey(machine ? @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment" : "Environment", writable: true)
                    ?? throw new IOException("The environment registration is unavailable.");
                _productValues = new Values(_product, ProductValues);
                _uninstallValues = new Values(_uninstall, UninstallValues);
                _path = new Values(_environment, new[] { "Path" });
                string menu = Path.Combine(Environment.GetFolderPath(machine
                    ? Environment.SpecialFolder.CommonPrograms : Environment.SpecialFolder.Programs), "KillerScan");
                CaptureShortcut(Path.Combine(menu, "KillerScan.lnk"));
                if (!machine) CaptureShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "KillerScan.lnk"));
            }

            private void CaptureShortcut(string path)
            {
                _pins.Add(InstallationFiles.PinDirectory(Path.GetDirectoryName(path)!));
                if (!File.Exists(path)) return;
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Uninstall cannot read a linked shortcut: " + path);
                _shortcuts.Add((path, File.ReadAllBytes(path)));
            }

            internal void Remove()
            {
                CommandLinePath.RemoveFrom(_environment, _directory);
                foreach (var shortcut in _shortcuts) InstallationFiles.RemoveShortcut(shortcut.Path);
                _productValues.Remove();
                _uninstallValues.Remove();
            }

            internal void Restore()
            {
                var errors = new List<Exception>();
                foreach (var values in new[] { _productValues, _uninstallValues, _path })
                    try { values.Restore(); } catch (Exception error) { errors.Add(error); }
                foreach (var shortcut in _shortcuts)
                    try
                    {
                        if (File.Exists(shortcut.Path))
                        {
                            if (!File.ReadAllBytes(shortcut.Path).SequenceEqual(shortcut.Bytes))
                                throw new IOException("A replacement shortcut was preserved: " + shortcut.Path);
                        }
                        else
                            using (var output = new FileStream(shortcut.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            { output.Write(shortcut.Bytes, 0, shortcut.Bytes.Length); output.Flush(true); }
                    }
                    catch (Exception error) { errors.Add(error); }
                if (errors.Count != 0) throw new AggregateException(errors);
            }

            public void Dispose()
            {
                foreach (var pin in _pins) pin.Dispose();
                _environment.Dispose(); _uninstall?.Dispose(); _product?.Dispose();
            }
        }

        private sealed class Values
        {
            private readonly RegistryKey? _key;
            private readonly Dictionary<string, (object? Value, RegistryValueKind Kind)> _values = new();
            /// <summary>A null key is an absent registration: nothing to remove and nothing to restore.</summary>
            internal Values(RegistryKey? key, string[] names)
            {
                _key = key;
                if (key == null) return;
                foreach (string name in names)
                {
                    object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    _values.Add(name, (value, value == null ? RegistryValueKind.None : key.GetValueKind(name)));
                }
            }
            internal void Remove() { foreach (string name in _values.Keys) _key!.DeleteValue(name, false); }
            internal void Restore()
            {
                foreach (var value in _values)
                    if (value.Value.Value == null) _key!.DeleteValue(value.Key, false);
                    else _key!.SetValue(value.Key, value.Value.Value, value.Value.Kind);
            }
        }
    }
}
