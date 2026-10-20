using System.IO;
using System.Security.Cryptography;

namespace KillerScan.Services
{
    internal static class ExecutableDeployment
    {
        internal sealed class FileOperations
        {
            internal Action<Stream, Stream> Copy = (input, output) => input.CopyTo(output);
            internal Action<string, string, string?> Replace = File.Replace;
            internal Action<string, string> Move = File.Move;
            internal Action<string> Delete = File.Delete;
        }

        internal static void Install(string source, string destination)
            => Install(source, destination, new FileOperations());

        internal static void Install(string source, string destination, FileOperations files)
        {
            source = Path.GetFullPath(source);
            destination = Path.GetFullPath(destination);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string staged = destination + "." + Guid.NewGuid().ToString("N") + ".new";
            bool retainStaged = false;
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] expected;
                    using (var hash = SHA256.Create()) expected = hash.ComputeHash(input);
                    input.Position = 0;
                    using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        files.Copy(input, output);
                        output.Flush(true);
                    }
                    using (var copy = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var hash = SHA256.Create())
                    {
                        byte[] actual = hash.ComputeHash(copy);
                        if (copy.Length != input.Length || !System.Linq.Enumerable.SequenceEqual(expected, actual))
                            throw new InvalidDataException("The staged executable does not match the complete source.");
                    }
                }
                Publish(staged, destination, files, ref retainStaged);
            }
            finally
            {
                if (!retainStaged) TryDelete(staged, files);
            }
        }

        private static void Publish(string staged, string destination, FileOperations files, ref bool retainStaged)
        {
            string backup = destination + "." + Guid.NewGuid().ToString("N") + ".bak";
            try
            {
                if (File.Exists(destination))
                {
                    try
                    {
                        files.Replace(staged, destination, backup);
                        TryDelete(backup, files);
                        return;
                    }
                    catch (IOException) { retainStaged = File.Exists(backup) || !File.Exists(destination); }
                    catch (PlatformNotSupportedException) { }
                }
                if (File.Exists(destination))
                {
                    if (File.Exists(backup)) { retainStaged = true; throw new IOException("The replacement left both destination and backup files."); }
                    files.Move(destination, backup);
                    retainStaged = true;
                }
                files.Move(staged, destination);
                retainStaged = false;
                TryDelete(backup, files);
            }
            catch (Exception publicationError)
            {
                if (!File.Exists(destination))
                {
                    retainStaged = true;
                    string recovery = File.Exists(backup) ? backup : staged;
                    try
                    {
                        string restored = destination + "." + Guid.NewGuid().ToString("N") + ".restore";
                        File.Copy(recovery, restored);
                        files.Move(restored, destination);
                        if (recovery == staged) { retainStaged = false; return; }
                    }
                    catch (Exception recoveryError)
                    {
                        throw RecoveryError(staged, backup, new AggregateException(publicationError, recoveryError));
                    }
                }
                if (retainStaged) throw RecoveryError(staged, backup, publicationError);
                throw;
            }
        }

        private static IOException RecoveryError(string staged, string backup, Exception error)
            => new IOException("The executable could not be published. Recovery files were kept. Staged: " + staged + ". Backup: " + backup, error);

        private static void TryDelete(string path, FileOperations files)
        {
            try { files.Delete(path); } catch { }
        }
    }
}
