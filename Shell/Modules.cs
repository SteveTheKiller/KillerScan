using System;
using System.IO;
using System.Reflection;

// KillerScripts, shipped inside the exe. Partial of MainWindow. Same mechanism as KillerShell,
// which ships this module and KillerPivot the same way.
//
// It is an embedded resource (see the csproj) rather than a file beside the app: KillerScan is
// one portable exe, and a user who copies it to a USB stick and runs it on a customer's machine
// gets the module too, with no install, no PSGallery and no internet. That is the whole point -
// the machines these are most useful on are the ones you cannot install anything on.
//
// It is unpacked to LocalAppData rather than run from memory because PowerShell resolves
// modules by PATH: a module has to exist as a directory with a manifest in it before
// Import-Module or auto-loading can find it. The folder is stamped with the app version, so a
// KillerScan upgrade re-writes the modules instead of leaving an old copy in place forever.
namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private static readonly string[] BundledModules = ["KillerScripts"];

        private static bool _modulesReady;

        /// <summary>
        /// Unpack the bundled modules and put them on PSModulePath. Called before the first
        /// shell starts, not at launch: most sessions never open a terminal, and this touches
        /// the disk.
        /// </summary>
        /// <remarks>
        /// PSModulePath is set on THIS process, and the pty child inherits our environment block
        /// (ConPty passes lpEnvironment as null), so every shell opened from here sees it with
        /// no per-launch plumbing. It is APPENDED, never prepended: if the user has installed
        /// the module properly from the gallery, their copy is the one that should win, and
        /// ours is only the fallback that makes a portable run work.
        /// </remarks>
        internal static void EnsureBundledModules()
        {
            if (_modulesReady) return;
            _modulesReady = true;      // set first: a failure here must not retry on every tab

            try
            {
                string root = ModulesRoot();
                foreach (var name in BundledModules) Unpack(name, root);

                string current = Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty;
                if (current.IndexOf(root, StringComparison.OrdinalIgnoreCase) >= 0) return;

                Environment.SetEnvironmentVariable("PSModulePath",
                    current.Length == 0 ? root : current.TrimEnd(';') + ";" + root);
            }
            catch
            {
                // A locked or unwritable LocalAppData is not worth failing a shell over. The
                // terminal still opens; the modules are simply not there.
            }
        }

        /// <summary>
        /// Version-stamped so an upgrade lands its own copy. Without the stamp a module fixed in
        /// a later KillerScan would never replace the one unpacked by an earlier one, because
        /// the check below is "does the folder exist".
        /// </summary>
        private static string ModulesRoot() => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "Modules", AppVersion());

        private static string AppVersion() =>
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0";

        private static void Unpack(string module, string root)
        {
            string dir = Path.Combine(root, module);

            // A sentinel written LAST is the marker, not the directory and not the manifest:
            // resource order is not guaranteed, so the .psd1 can be the first file out, and a
            // run interrupted after it would leave a module that looks complete and is not.
            string done = Path.Combine(dir, ".unpacked");
            if (File.Exists(done)) return;

            Directory.CreateDirectory(dir);

            var asm = Assembly.GetExecutingAssembly();
            string prefix = asm.GetName().Name + ".Modules." + module + ".";

            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith(prefix, StringComparison.Ordinal)) continue;

                // Resource names flatten the path and there are no subfolders in the module,
                // so what is left after the prefix IS the file name, dot and all.
                string file = res[prefix.Length..];

                using var src = asm.GetManifestResourceStream(res);
                if (src == null) continue;

                // Written to a temp name and moved into place, so an interrupted write cannot
                // leave a truncated .psd1 that the check above would then accept as complete.
                string target = Path.Combine(dir, file);
                string temp   = target + ".tmp";

                using (var dst = File.Create(temp)) src.CopyTo(dst);

                if (File.Exists(target)) File.Delete(target);
                File.Move(temp, target);
            }

            File.WriteAllText(done, string.Empty);
        }
    }
}
