using System;
using System.IO;
using System.Linq;
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
        private static readonly string[] BundledModules = ["KillerScripts", "KillerScan"];

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

                // The KillerScan module shells out to the command line, and a portable copy is
                // not on PATH. Publishing our own path means scan and probe work from a folder
                // the user just dropped the exe into.
                Environment.SetEnvironmentVariable("KS_EXE",
                    System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "");

                string current = Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty;
                Environment.SetEnvironmentVariable("PSModulePath",
                    ComposeModulePath(current, root, Path.GetDirectoryName(root)!));
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

        /// <summary>
        /// PSModulePath with this version's bundled folder last and no other version's. A per-user
        /// update reopens the app with the PSModulePath the old copy had, which already names the
        /// old version's folder; PowerShell takes the first match, so leaving it would load the
        /// previous release's modules. Entries outside <paramref name="bundledParent"/> are the
        /// user's and keep their order.
        /// </summary>
        internal static string ComposeModulePath(string current, string root, string bundledParent)
        {
            static string Normal(string path) => path.Trim().TrimEnd('\\', '/');
            string parent = Normal(bundledParent);
            var kept = current.Split(';')
                .Where(entry => Normal(entry).Length > 0)
                .Where(entry => !string.Equals(Path.GetDirectoryName(Normal(entry)), parent, StringComparison.OrdinalIgnoreCase))
                .ToList();
            kept.Add(root);
            return string.Join(";", kept);
        }

        internal static void UnpackBundled(string module, string root) => Unpack(module, root);

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
                // The temp name is unique per writer: two KillerScan windows opening their first
                // terminal at once unpack into the same version folder, and a shared temp name made
                // the second fail with a sharing violation and run without modules all session.
                string target = Path.Combine(dir, file);
                string temp   = target + "." + Guid.NewGuid().ToString("N") + ".tmp";

                // A target only ever appears by moving a complete temp file into place, so one that
                // exists is whole (the folder is version-stamped, so it is this build's content).
                if (File.Exists(target)) continue;
                try
                {
                    using (var dst = File.Create(temp)) src.CopyTo(dst);
                    // Another writer placed the same embedded file first; its copy is identical.
                    try { File.Move(temp, target); }
                    catch (IOException) when (File.Exists(target)) { }
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }

            try { File.WriteAllText(done, string.Empty); }
            catch (IOException) when (File.Exists(done)) { }
        }
    }
}
