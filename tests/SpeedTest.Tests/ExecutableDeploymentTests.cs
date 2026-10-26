using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KillerScan.Services;

internal static class ExecutableDeploymentTests
{
    public static Task Run()
        => RunCase(0);

    public static Task RunPartial(int nativeError)
        => RunCase(nativeError);

    private static Task RunCase(int nativeErrorOnly)
    {
        string directory = Path.Combine(Path.GetTempPath(), "KillerScanDeployment-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string source = Path.Combine(directory, "download.exe"), target = Path.Combine(directory, "installed.exe");
        byte[] incoming = Enumerable.Range(0, 10000).Select(n => (byte)n).ToArray();
        byte[] original = new byte[] { 10, 20, 30, 40 };
        File.WriteAllBytes(source, incoming);
        try
        {
            if (nativeErrorOnly == 0)
            {
                foreach (bool existing in new[] { false, true })
                {
                    if (existing) File.WriteAllBytes(target, original); else if (File.Exists(target)) File.Delete(target);
                    ExecutableDeployment.Install(source, target);
                    Require(File.ReadAllBytes(target).SequenceEqual(incoming), "Complete executable publication lost bytes.");
                }
                File.WriteAllBytes(target, original);
                bool failed = false;
                try { ExecutableDeployment.Install(source, target, new ExecutableDeployment.FileOperations { Copy = (input, output) => output.WriteByte(1) }); }
                catch (Exception error) when (error is IOException || error is InvalidDataException) { failed = true; }
                Require(failed && File.ReadAllBytes(target).SequenceEqual(original), "Incomplete staging must fail verification before publication.");

                failed = false;
                try
                {
                    ExecutableDeployment.Install(source, target, new ExecutableDeployment.FileOperations
                    {
                        Copy = (input, output) => { byte[] changed = (byte[])incoming.Clone(); changed[0] ^= 1; output.Write(changed, 0, changed.Length); }
                    });
                }
                catch (InvalidDataException) { failed = true; }
                Require(failed && File.ReadAllBytes(target).SequenceEqual(original), "Same-length staging corruption must fail its hash check.");

                failed = false;
                try { ExecutableDeployment.Install(source, target, new ExecutableDeployment.FileOperations { Copy = (input, output) => { output.WriteByte(1); throw new IOException("Partial copy."); } }); }
                catch (IOException) { failed = true; }
                Require(failed && File.ReadAllBytes(target).SequenceEqual(original), "A partial staging write changed the installed executable.");
                using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    failed = false;
                    try { ExecutableDeployment.Install(source, target); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException) { failed = true; }
                    Require(failed, "A locked executable must not report a successful install.");
                }
                Require(File.ReadAllBytes(target).SequenceEqual(original), "A locked executable was changed.");
                Require(Directory.GetFiles(directory).Length == 2, "Failed staging and locked targets must clean unused staging.");
            }

            foreach (int nativeError in new[] { 1176, 1177 }.Where(value => nativeErrorOnly == 0 || value == nativeErrorOnly))
            {
                File.WriteAllBytes(target, original);
                string? staged = null, saved = null;
                var files = new ExecutableDeployment.FileOperations
                {
                    Replace = (temporary, destination, backup) =>
                    {
                        staged = temporary;
                        if (nativeError == 1177) { saved = backup ?? destination + ".native-backup"; File.Move(destination, saved); }
                        else File.Delete(destination);
                        throw new IOException("Partial replacement.", unchecked((int)(0x80070000u + nativeError)));
                    },
                    Move = (temporary, destination) => throw new IOException("Publication and restoration are unavailable.")
                };
                Exception? error = null;
                try { ExecutableDeployment.Install(source, target, files); }
                catch (IOException problem) { error = problem; }
                Require(error != null && File.Exists(staged) && File.ReadAllBytes(staged!).SequenceEqual(incoming), "Partial replacement discarded the complete staged executable.");
                Require(error!.Message.Contains(staged!), "Publication errors must name the retained complete staging path.");
                if (nativeError == 1177) Require(File.ReadAllBytes(saved!).SequenceEqual(original), "Partial replacement discarded the original backup.");
                foreach (string file in Directory.GetFiles(directory).Where(p => p != source)) File.Delete(file);
            }

            if (nativeErrorOnly == 0)
            {
                File.WriteAllBytes(target, original);
                ExecutableDeployment.Install(source, target, new ExecutableDeployment.FileOperations { Delete = file => throw new IOException("Cleanup is unavailable.") });
                Require(File.ReadAllBytes(target).SequenceEqual(incoming), "Cleanup failure must not report a failed completed install.");

                // A backup that was still mapped by the running exe when it was retired is removed by
                // the next publication. One still in use, and anything not named like a backup, stay.
                foreach (string file in Directory.GetFiles(directory).Where(p => p != source && p != target)) File.Delete(file);
                string stale = target + "." + Guid.NewGuid().ToString("N") + ".bak";
                string busy = target + "." + Guid.NewGuid().ToString("N") + ".bak";
                string unrelated = target + ".notes.bak";
                foreach (string file in new[] { stale, busy, unrelated }) File.WriteAllBytes(file, original);
                using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
                    ExecutableDeployment.Install(source, target);
                Require(!File.Exists(stale) && File.Exists(busy) && File.Exists(unrelated) && File.ReadAllBytes(target).SequenceEqual(incoming),
                    "Publication removes retired backups that are no longer in use and nothing else.");
                ExecutableDeployment.RemoveRetiredBackups(target);
                Require(!File.Exists(busy) && File.Exists(unrelated), "A retired backup goes once its image is released.");
            }
            Console.WriteLine("PASS executable deployment checks, selected partial failure: " + nativeErrorOnly);
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
